using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenMagicMouse.Gestures;
using OpenMagicMouse.Models;

namespace OpenMagicMouse.Scrolling;

/// <summary>
/// macOS NSScrollView-style 125Hz sub-pixel 1-finger smooth tracking &amp;
/// force-proportional exponential inertial momentum engine (non-linear Apple gain,
/// peel-off immunity sliding window, flywheel multi-flick stacking, and adaptive tau).
/// </summary>
public sealed class MacScrollPhysicsEngine : IDisposable
{
    private struct VelocitySample
    {
        public long TsMs;
        public double SensorSpeedY;
        public double WheelVelY;
        public double WheelVelX;
        public byte Size;
    }

    private readonly Action<int, int> _scrollSink;
    private readonly LowLevelWheelFilter? _wheelFilter;
    private readonly double _osLineCompensation;
    private readonly AutoResetEvent _coastWake = new(false);
    private readonly object _coastLock = new();
    private readonly Thread _coastThread;
    private readonly VelocitySample[] _samples = new VelocitySample[8];

    private GestureConfig _config;
    private bool _disposed;

    // 1-finger active tracking state (single-producer thread from PipeTouchClient/tests)
    private bool _isTracking;
    private int _trackingId;
    private int _startX;
    private int _startY;
    private long _startMs;
    private int _prevX;
    private int _prevY;
    private long _prevMs;
    private bool _engaged;
    private bool _suppressUntilAllUp;
    private double _smoothDy;
    private double _smoothDx;
    private double _smoothSpeedY;
    private double _smoothSpeedX;
    private byte _peakContactSize;
    private long _lastSignificantMoveMs;
    private int _sampleHead;
    private int _sampleCount;
    private double _remY;
    private double _remX;

    // Momentum coasting state (shared with 125Hz coast thread under _coastLock)
    private bool _isCoasting;
    private double _coastVelY;
    private double _coastVelX;
    private double _coastTauMs = 325.0;
    private double _coastRemY;
    private double _coastRemX;
    private double _recentBrakeVelY;
    private long _recentBrakeMs;

    public bool IsCoasting
    {
        get { lock (_coastLock) return _isCoasting; }
    }

    public double CurrentCoastVelocity
    {
        get { lock (_coastLock) return _coastVelY; }
    }

    public MacScrollPhysicsEngine(
        GestureConfig initialConfig,
        Action<int, int>? customScrollSink = null,
        LowLevelWheelFilter? wheelFilter = null)
    {
        _config = initialConfig.Clone();
        _scrollSink = customScrollSink ?? ActionExecutor.SendPreciseScroll;
        _wheelFilter = wheelFilter;

        if (customScrollSink != null)
        {
            _osLineCompensation = 1.0;
        }
        else
        {
            int lines = 3;
            try
            {
                if (SystemInformation.MouseWheelScrollLines > 0)
                {
                    lines = SystemInformation.MouseWheelScrollLines;
                }
            }
            catch
            {
                lines = 3;
            }
            _osLineCompensation = 3.0 / Math.Clamp(lines, 1, 6);
        }

        if (_wheelFilter != null)
        {
            _wheelFilter.Enabled = _config.Enabled && _config.SmoothScrollEnabled;
        }

        _coastThread = new Thread(CoastLoop)
        {
            IsBackground = true,
            Name = "OpenMagicMouse.Coast125Hz",
            Priority = ThreadPriority.AboveNormal
        };
        _coastThread.Start();
    }

    public void UpdateConfig(GestureConfig config)
    {
        _config = config.Clone();
        if (_wheelFilter != null)
        {
            _wheelFilter.Enabled = _config.Enabled && _config.SmoothScrollEnabled;
        }
        if (!_config.Enabled || !_config.SmoothScrollEnabled)
        {
            StopMomentum(saveForFlickBoost: false);
            ResetTracking();
        }
    }

    public void ProcessFrame(TouchFrame frame)
    {
        if (!_config.Enabled || !_config.SmoothScrollEnabled)
        {
            StopMomentum(saveForFlickBoost: false);
            ResetTracking();
            return;
        }

        bool anyDown = false;
        int intentionalCount = 0;
        TouchFinger singleFinger = default;

        foreach (var f in frame.Fingers)
        {
            if (!f.IsDown) continue;
            anyDown = true;

            if (Math.Abs(f.X) > 980 || f.Y < -1450)
            {
                continue;
            }

            intentionalCount++;
            if (intentionalCount == 1)
            {
                singleFinger = f;
            }
        }

        if (anyDown)
        {
            _wheelFilter?.NotifyMagicMouseTouch(frame.TimestampMs);
        }

        // Physical click or 2+ fingers immediately cancel/mute 1-finger scrolling
        if (frame.Clicks != 0 || intentionalCount >= 2)
        {
            StopMomentum(saveForFlickBoost: false);
            ResetTracking();
            if (intentionalCount >= 2)
            {
                _suppressUntilAllUp = true;
            }
            return;
        }

        if (intentionalCount == 0)
        {
            if (_suppressUntilAllUp)
            {
                _suppressUntilAllUp = false;
                ResetTracking();
                return;
            }

            TryLaunchInertiaFromWindow(frame.TimestampMs);
            ResetTracking();
            return;
        }

        if (_suppressUntilAllUp)
        {
            return;
        }

        // Intentional 1-finger touch on deck
        if (!_isTracking || _trackingId != singleFinger.Id)
        {
            StopMomentum(saveForFlickBoost: true, timestampMs: frame.TimestampMs);
            _isTracking = true;
            _trackingId = singleFinger.Id;
            _startX = _prevX = singleFinger.X;
            _startY = _prevY = singleFinger.Y;
            _startMs = _prevMs = frame.TimestampMs;
            _engaged = false;
            _smoothDy = 0;
            _smoothDx = 0;
            _smoothSpeedY = 0;
            _smoothSpeedX = 0;
            _peakContactSize = singleFinger.Size;
            _sampleHead = 0;
            _sampleCount = 0;
            _remY = 0;
            _remX = 0;
            _lastSignificantMoveMs = frame.TimestampMs;
            return;
        }

        long dt = Math.Clamp(frame.TimestampMs - _prevMs, 4, 45);
        _prevMs = frame.TimestampMs;

        int rawDx = singleFinger.X - _prevX;
        int rawDy = singleFinger.Y - _prevY;
        _prevX = singleFinger.X;
        _prevY = singleFinger.Y;

        if (singleFinger.Size > _peakContactSize)
        {
            _peakContactSize = singleFinger.Size;
        }

        if (!_engaged)
        {
            double distFromStart = Math.Sqrt(
                Math.Pow(singleFinger.X - _startX, 2) +
                Math.Pow(singleFinger.Y - _startY, 2));
            if (distFromStart < 10.0)
            {
                return;
            }

            // Protect 1-finger horizontal Safari Back/Forward flicks from early horizontal scroll
            if (!string.Equals(_config.OneFingerFastFlickAction, "None", StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(singleFinger.X - _startX) > 2.2 * Math.Abs(singleFinger.Y - _startY) &&
                Math.Abs(singleFinger.Y - _startY) < 16 &&
                (frame.TimestampMs - _startMs) <= 140)
            {
                return;
            }

            _engaged = true;
        }

        double rawSpeedY = Math.Abs(rawDy) / (double)dt;
        _smoothSpeedY = rawSpeedY > _smoothSpeedY
            ? (0.75 * rawSpeedY + 0.25 * _smoothSpeedY)
            : (0.45 * rawSpeedY + 0.55 * _smoothSpeedY);

        double alphaPosY = Math.Clamp(0.60 + rawSpeedY * 0.10, 0.60, 0.96);
        _smoothDy = alphaPosY * rawDy + (1.0 - alphaPosY) * _smoothDy;

        double gainY = ComputeAppleGain(_smoothSpeedY) * _config.ScrollSpeed * _osLineCompensation;
        // On sensor: +Y is forward/UP, -Y is rear/DOWN.
        // NaturalScroll ON => sliding finger UP (+dy) scrolls content UP so viewport moves DOWN (negative WHEEL_DELTA).
        double signedDy = _config.NaturalScroll ? -_smoothDy : _smoothDy;
        double stepWheelY = signedDy * gainY;

        double stepWheelX = 0;
        if (Math.Abs(rawDx) > Math.Abs(rawDy) * 1.35 && Math.Abs(rawDx) >= 3)
        {
            double rawSpeedX = Math.Abs(rawDx) / (double)dt;
            _smoothSpeedX = rawSpeedX > _smoothSpeedX
                ? (0.75 * rawSpeedX + 0.25 * _smoothSpeedX)
                : (0.45 * rawSpeedX + 0.55 * _smoothSpeedX);

            double alphaPosX = Math.Clamp(0.60 + rawSpeedX * 0.10, 0.60, 0.96);
            _smoothDx = alphaPosX * rawDx + (1.0 - alphaPosX) * _smoothDx;

            double gainX = ComputeAppleGain(_smoothSpeedX) * _config.ScrollSpeed * _osLineCompensation * 0.85;
            double signedDx = _config.NaturalScroll ? -_smoothDx : _smoothDx;
            stepWheelX = signedDx * gainX;
        }

        EmitSubPixel(stepWheelY, stepWheelX, ref _remY, ref _remX);

        if (Math.Abs(rawDy) >= 2 || Math.Abs(rawDx) >= 2)
        {
            _lastSignificantMoveMs = frame.TimestampMs;
            PushSample(new VelocitySample
            {
                TsMs = frame.TimestampMs,
                SensorSpeedY = rawSpeedY,
                WheelVelY = stepWheelY / dt,
                WheelVelX = stepWheelX / dt,
                Size = singleFinger.Size
            });
        }
    }

    private static double ComputeAppleGain(double sensorUnitsPerMs)
    {
        if (sensorUnitsPerMs <= 1.2)
        {
            return 0.14 + 0.09 * sensorUnitsPerMs;
        }

        double excess = sensorUnitsPerMs - 1.2;
        return Math.Min(0.248 + 0.095 * Math.Pow(excess, 1.35), 3.85);
    }

    private void PushSample(VelocitySample sample)
    {
        _samples[_sampleHead] = sample;
        _sampleHead = (_sampleHead + 1) % _samples.Length;
        if (_sampleCount < _samples.Length)
        {
            _sampleCount++;
        }
    }

    private void TryLaunchInertiaFromWindow(long liftoffTsMs)
    {
        if (!_isTracking || !_engaged || _sampleCount == 0 || (liftoffTsMs - _lastSignificantMoveMs) > 75)
        {
            return;
        }

        bool foundFullContact = false;
        double peakSensorSpeedY = 0;
        double bestWheelVelY = 0;
        double bestWheelVelX = 0;
        double latestSignY = 0;

        for (int i = 0; i < _sampleCount; i++)
        {
            int idx = (_sampleHead - 1 - i + _samples.Length) % _samples.Length;
            var s = _samples[idx];
            if (liftoffTsMs - s.TsMs > 80)
            {
                continue;
            }

            if (latestSignY == 0 && Math.Abs(s.WheelVelY) > 0.05)
            {
                latestSignY = Math.Sign(s.WheelVelY);
            }

            bool isPeelOff = _peakContactSize >= 10 && s.Size < _peakContactSize * 0.62;
            if (!isPeelOff)
            {
                foundFullContact = true;
            }
        }

        for (int i = 0; i < _sampleCount; i++)
        {
            int idx = (_sampleHead - 1 - i + _samples.Length) % _samples.Length;
            var s = _samples[idx];
            if (liftoffTsMs - s.TsMs > 80)
            {
                continue;
            }

            bool isPeelOff = _peakContactSize >= 10 && s.Size < _peakContactSize * 0.62;
            if (foundFullContact && isPeelOff)
            {
                continue;
            }

            if (s.SensorSpeedY > peakSensorSpeedY)
            {
                peakSensorSpeedY = s.SensorSpeedY;
            }

            if ((latestSignY == 0 || Math.Sign(s.WheelVelY) == latestSignY) &&
                Math.Abs(s.WheelVelY) > Math.Abs(bestWheelVelY))
            {
                bestWheelVelY = s.WheelVelY;
            }

            if (Math.Abs(s.WheelVelX) > Math.Abs(bestWheelVelX))
            {
                bestWheelVelX = s.WheelVelX;
            }
        }

        bool hasVerticalInertia = peakSensorSpeedY >= 1.45 && Math.Abs(bestWheelVelY) >= 0.22;
        bool hasHorizontalInertia = Math.Abs(bestWheelVelX) >= 0.22;
        if (!hasVerticalInertia && !hasHorizontalInertia)
        {
            return;
        }

        double launchY = 0;
        if (hasVerticalInertia)
        {
            double flickBoost = 1.0 + 0.32 * Math.Clamp((peakSensorSpeedY - 2.0) / 10.0, 0.0, 1.6);
            launchY = bestWheelVelY * flickBoost;

            if (liftoffTsMs - _recentBrakeMs <= 280 && Math.Sign(launchY) == Math.Sign(_recentBrakeVelY))
            {
                launchY += 0.65 * _recentBrakeVelY;
            }

            launchY = Math.Clamp(launchY, -48.0, 48.0);
        }

        double launchX = hasHorizontalInertia ? Math.Clamp(bestWheelVelX, -18.0, 18.0) : 0;
        double tauMs = 310.0 + 270.0 * Math.Clamp(Math.Abs(launchY) / 22.0, 0.0, 1.0);
        StartMomentum(launchY, launchX, tauMs);
    }

    private void StartMomentum(double velY, double velX, double tauMs)
    {
        lock (_coastLock)
        {
            _coastVelY = velY;
            _coastVelX = velX;
            _coastTauMs = tauMs;
            _coastRemY = 0;
            _coastRemX = 0;
            _isCoasting = true;
        }
        _coastWake.Set();
    }

    private void StopMomentum(bool saveForFlickBoost, long timestampMs = 0)
    {
        lock (_coastLock)
        {
            if (saveForFlickBoost && _isCoasting)
            {
                _recentBrakeVelY = _coastVelY;
                _recentBrakeMs = timestampMs;
            }
            _isCoasting = false;
            _coastVelY = 0;
            _coastVelX = 0;
            _coastRemY = 0;
            _coastRemX = 0;
        }
    }

    private void ResetTracking()
    {
        _isTracking = false;
        _trackingId = 0;
        _engaged = false;
        _smoothDy = 0;
        _smoothDx = 0;
        _smoothSpeedY = 0;
        _smoothSpeedX = 0;
        _peakContactSize = 0;
        _sampleHead = 0;
        _sampleCount = 0;
        _remY = 0;
        _remX = 0;
    }

    private void CoastLoop()
    {
        double ticksToMs = 1000.0 / Stopwatch.Frequency;

        while (!_disposed)
        {
            _coastWake.WaitOne();
            if (_disposed) break;

            bool highResSet = false;
            try
            {
                timeBeginPeriod(2);
                highResSet = true;
                long prevStamp = Stopwatch.GetTimestamp();

                while (!_disposed)
                {
                    Thread.Sleep(7);
                    if (_disposed) break;

                    long nowStamp = Stopwatch.GetTimestamp();
                    double dtMs = Math.Clamp((nowStamp - prevStamp) * ticksToMs, 1.0, 40.0);
                    prevStamp = nowStamp;

                    int emitY = 0;
                    int emitX = 0;

                    lock (_coastLock)
                    {
                        if (!_isCoasting)
                        {
                            break;
                        }

                        double tauMs = _coastTauMs;
                        double decay = Math.Exp(-dtMs / tauMs);
                        double stepY = _coastVelY * tauMs * (1.0 - decay);
                        double stepX = _coastVelX * tauMs * (1.0 - decay);

                        _coastVelY *= decay;
                        _coastVelX *= decay;

                        if (Math.Abs(_coastVelY) < 0.018 && Math.Abs(_coastVelX) < 0.018)
                        {
                            _isCoasting = false;
                            _coastVelY = 0;
                            _coastVelX = 0;
                        }

                        AccumulateSubPixel(stepY, stepX, ref _coastRemY, ref _coastRemX, out emitY, out emitX);
                    }

                    if (emitY != 0 || emitX != 0)
                    {
                        _scrollSink(emitY, emitX);
                    }
                }
            }
            finally
            {
                if (highResSet)
                {
                    timeEndPeriod(2);
                }
            }
        }
    }

    private void EmitSubPixel(double stepY, double stepX, ref double remY, ref double remX)
    {
        AccumulateSubPixel(stepY, stepX, ref remY, ref remX, out int emitY, out int emitX);
        if (emitY != 0 || emitX != 0)
        {
            _scrollSink(emitY, emitX);
        }
    }

    private static void AccumulateSubPixel(
        double stepY,
        double stepX,
        ref double remY,
        ref double remX,
        out int emitY,
        out int emitX)
    {
        remY += stepY;
        emitY = (int)Math.Truncate(remY);
        remY -= emitY;
        if (emitY != 0 && emitY % 120 == 0)
        {
            emitY += emitY > 0 ? -1 : 1;
        }

        remX += stepX;
        emitX = (int)Math.Truncate(remX);
        remX -= emitX;
        if (emitX != 0 && emitX % 120 == 0)
        {
            emitX += emitX > 0 ? -1 : 1;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopMomentum(saveForFlickBoost: false);
        _coastWake.Set();
        try { _coastThread.Join(500); } catch { }
        _coastWake.Dispose();
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
