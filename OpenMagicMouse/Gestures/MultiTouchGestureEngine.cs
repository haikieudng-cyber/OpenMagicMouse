using OpenMagicMouse.Models;

namespace OpenMagicMouse.Gestures;

/// <summary>
/// Multi-finger gesture recognizer with palm/edge-grip rejection.
/// Designed cleanly so unit self-tests can inject deterministic TouchFrames
/// without triggering real Win32 SendInput events.
/// </summary>
public sealed class MultiTouchGestureEngine
{
    private sealed class TrackSlot
    {
        public int Id { get; init; }
        public int StartX { get; set; }
        public int StartY { get; set; }
        public int CurX { get; set; }
        public int CurY { get; set; }
        public long StartMs { get; set; }
        public long LastMs { get; set; }
        public double MaxTravel { get; set; }
        public bool IsEdgeGrip { get; set; }
    }

    private readonly Dictionary<int, TrackSlot> _slots = new();
    private readonly Action<GestureAction, string> _onGestureTriggered;
    private GestureConfig _config;

    private int _episodePeakIntentional;
    private long _episodeStartMs;
    private long _episodeSecondFingerEnterMs;
    private double _maxTravelInEpisode;
    private bool _episodeConsumed;
    private bool _hadPhysicalClick;
    private byte _prevClicks;
    private int _prevIntentionalCount;
    private long _lastGestureMs;
    private long _lastTwoFingerTapMs;

    public MultiTouchGestureEngine(GestureConfig config, Action<GestureAction, string> onGestureTriggered)
    {
        _config = config.Clone();
        _onGestureTriggered = onGestureTriggered;
    }

    public void UpdateConfig(GestureConfig config)
    {
        _config = config.Clone();
    }

    public int ActiveIntentionalCount => _prevIntentionalCount;

    public void ProcessFrame(TouchFrame frame)
    {
        if (!_config.Enabled)
        {
            _slots.Clear();
            ResetEpisode();
            _prevClicks = frame.Clicks;
            _prevIntentionalCount = 0;
            return;
        }

        // Update tracked finger slots
        var downIds = new HashSet<int>();
        foreach (var f in frame.Fingers)
        {
            if (!f.IsDown) continue;
            downIds.Add(f.Id);

            if (!_slots.TryGetValue(f.Id, out var slot))
            {
                bool edgeGrip = Math.Abs(f.X) > 980 || f.Y < -1450;
                _slots[f.Id] = new TrackSlot
                {
                    Id = f.Id,
                    StartX = f.X,
                    StartY = f.Y,
                    CurX = f.X,
                    CurY = f.Y,
                    StartMs = frame.TimestampMs,
                    LastMs = frame.TimestampMs,
                    MaxTravel = 0,
                    IsEdgeGrip = edgeGrip
                };
            }
            else
            {
                slot.CurX = f.X;
                slot.CurY = f.Y;
                slot.LastMs = frame.TimestampMs;
                double dx = slot.CurX - slot.StartX;
                double dy = slot.CurY - slot.StartY;
                double travel = Math.Sqrt(dx * dx + dy * dy);
                if (travel > slot.MaxTravel) slot.MaxTravel = travel;

                if (slot.IsEdgeGrip && travel > 220)
                {
                    slot.IsEdgeGrip = false;
                    slot.StartX = slot.CurX;
                    slot.StartY = slot.CurY;
                    slot.StartMs = frame.TimestampMs;
                    slot.MaxTravel = 0;
                }
            }
        }

        foreach (int existingId in _slots.Keys.ToArray())
        {
            if (!downIds.Contains(existingId))
            {
                _slots.Remove(existingId);
            }
        }

        var intentional = _slots.Values.Where(s => !s.IsEdgeGrip).OrderBy(s => s.StartMs).ToList();

        if (intentional.Count > 0)
        {
            if (_prevIntentionalCount == 0)
            {
                _episodeStartMs = frame.TimestampMs;
                _episodePeakIntentional = intentional.Count;
                _episodeSecondFingerEnterMs = intentional.Count >= 2 ? frame.TimestampMs : 0;
                _maxTravelInEpisode = 0;
                _episodeConsumed = false;
                _hadPhysicalClick = frame.Clicks != 0;
            }
            else
            {
                if (intentional.Count > _episodePeakIntentional)
                {
                    if (_episodePeakIntentional < 2 && intentional.Count >= 2)
                    {
                        _episodeSecondFingerEnterMs = frame.TimestampMs;
                    }
                    _episodePeakIntentional = intentional.Count;
                }
            }

            foreach (var s in intentional)
            {
                if (s.MaxTravel > _maxTravelInEpisode)
                {
                    _maxTravelInEpisode = s.MaxTravel;
                }
            }

            if (frame.Clicks != 0)
            {
                _hadPhysicalClick = true;
            }

            // 3-Finger Physical Click -> Middle Click (on rising edge of physical click)
            if (_prevClicks == 0 && frame.Clicks != 0 && intentional.Count >= 3 && !_episodeConsumed)
            {
                var clickAct = ResolveThreeFingerTap(_config.ThreeFingerTapAction);
                if (clickAct != GestureAction.None)
                {
                    Fire(clickAct, "Bấm 3 ngón (Click giữa)", frame.TimestampMs);
                    _episodeConsumed = true;
                }
            }

            // Evaluate continuous co-moving swipes when no physical click is held
            if (!_episodeConsumed && frame.Clicks == 0 && (frame.TimestampMs - _lastGestureMs) >= 220)
            {
                double invSens = 1.0 / Math.Clamp(_config.Sensitivity, 0.5, 2.0);

                if (intentional.Count >= 3)
                {
                    TryThreeFingerSwipe(intentional, invSens, frame.TimestampMs);
                }
                else if (intentional.Count == 2)
                {
                    TryTwoFingerSwipe(intentional[0], intentional[1], invSens, frame.TimestampMs);
                }
                else if (intentional.Count == 1 && _episodePeakIntentional == 1)
                {
                    TryOneFingerFastFlick(intentional[0], invSens, frame.TimestampMs);
                }
            }
        }
        else if (_prevIntentionalCount > 0)
        {
            // All intentional fingers lifted -> evaluate taps
            if (!_episodeConsumed && !_hadPhysicalClick && (frame.TimestampMs - _lastGestureMs) >= 180)
            {
                long dur = frame.TimestampMs - _episodeStartMs;
                long enterDelta = _episodeSecondFingerEnterMs > 0 ? (_episodeSecondFingerEnterMs - _episodeStartMs) : 0;

                if (_episodePeakIntentional == 2 && dur >= 20 && dur <= 260 && _maxTravelInEpisode < 185 && enterDelta <= 110)
                {
                    if (_lastTwoFingerTapMs > 0 && (frame.TimestampMs - _lastTwoFingerTapMs) <= 380)
                    {
                        _lastTwoFingerTapMs = 0;
                        var dblAct = ResolveSingleAction(_config.TwoFingerDoubleTapAction);
                        if (dblAct != GestureAction.None)
                        {
                            Fire(dblAct, "Chạm đúp 2 ngón", frame.TimestampMs);
                        }
                    }
                    else
                    {
                        _lastTwoFingerTapMs = frame.TimestampMs;
                    }
                }
                else if (_episodePeakIntentional >= 3 && dur >= 20 && dur <= 290 && _maxTravelInEpisode < 210)
                {
                    var tapAct = ResolveThreeFingerTap(_config.ThreeFingerTapAction);
                    if (tapAct != GestureAction.None)
                    {
                        Fire(tapAct, "Chạm 3 ngón", frame.TimestampMs);
                    }
                }
            }

            ResetEpisode();
        }

        _prevClicks = frame.Clicks;
        _prevIntentionalCount = intentional.Count;
    }

    private void TryTwoFingerSwipe(TrackSlot f0, TrackSlot f1, double invSens, long nowMs)
    {
        double dx0 = f0.CurX - f0.StartX;
        double dy0 = f0.CurY - f0.StartY;
        double dx1 = f1.CurX - f1.StartX;
        double dy1 = f1.CurY - f1.StartY;

        double avgDx = (dx0 + dx1) * 0.5;
        double avgDy = (dy0 + dy1) * 0.5;

        // 2-finger horizontal co-moving swipe
        if (dx0 * dx1 > 0 &&
            Math.Abs(dx0) >= 170 * invSens &&
            Math.Abs(dx1) >= 170 * invSens &&
            Math.Abs(avgDx) >= 350 * invSens &&
            Math.Abs(avgDx) > 1.4 * Math.Abs(avgDy))
        {
            bool swipedLeft = avgDx < 0;
            var act = ResolveHorizontal(_config.TwoFingerHorizontalAction, swipedLeft, _config.NaturalTwoFingerSwipe);
            if (act != GestureAction.None)
            {
                Fire(act, swipedLeft ? "Vuốt 2 ngón ← Trái" : "Vuốt 2 ngón → Phải", nowMs);
            }
            else
            {
                _episodeConsumed = true;
            }
            return;
        }

        // 2-finger vertical co-moving swipe (+Y = UP toward front, -Y = DOWN toward rear)
        if (dy0 * dy1 > 0 &&
            Math.Abs(dy0) >= 180 * invSens &&
            Math.Abs(dy1) >= 180 * invSens &&
            Math.Abs(avgDy) >= 370 * invSens &&
            Math.Abs(avgDy) > 1.4 * Math.Abs(avgDx))
        {
            bool swipedUp = avgDy > 0;
            var act = ResolveVertical(swipedUp ? _config.TwoFingerSwipeUpAction : _config.TwoFingerSwipeDownAction, swipedUp);
            if (act != GestureAction.None)
            {
                Fire(act, swipedUp ? "Vuốt 2 ngón ↑ Lên" : "Vuốt 2 ngón ↓ Xuống", nowMs);
            }
            else
            {
                _episodeConsumed = true;
            }
        }
    }

    private void TryThreeFingerSwipe(List<TrackSlot> fingers, double invSens, long nowMs)
    {
        double avgDx = fingers.Average(f => f.CurX - f.StartX);
        double avgDy = fingers.Average(f => f.CurY - f.StartY);

        int sameX = fingers.Count(f => (f.CurX - f.StartX) * avgDx > 0 && Math.Abs(f.CurX - f.StartX) >= 170 * invSens);
        if (sameX >= 2 && Math.Abs(avgDx) >= 340 * invSens && Math.Abs(avgDx) > 1.4 * Math.Abs(avgDy))
        {
            bool swipedLeft = avgDx < 0;
            var act = ResolveHorizontal(_config.ThreeFingerHorizontalAction, swipedLeft, _config.NaturalTwoFingerSwipe);
            if (act != GestureAction.None)
            {
                Fire(act, swipedLeft ? "Vuốt 3 ngón ← Trái" : "Vuốt 3 ngón → Phải", nowMs);
            }
            else
            {
                _episodeConsumed = true;
            }
            return;
        }

        int sameY = fingers.Count(f => (f.CurY - f.StartY) * avgDy > 0 && Math.Abs(f.CurY - f.StartY) >= 180 * invSens);
        if (sameY >= 2 && Math.Abs(avgDy) >= 360 * invSens && Math.Abs(avgDy) > 1.4 * Math.Abs(avgDx))
        {
            var act = avgDy > 0 ? GestureAction.MaximizeWindow : GestureAction.MinimizeWindow;
            Fire(act, avgDy > 0 ? "Vuốt 3 ngón ↑ Phóng to" : "Vuốt 3 ngón ↓ Thu nhỏ", nowMs);
        }
    }

    private void TryOneFingerFastFlick(TrackSlot f0, double invSens, long nowMs)
    {
        if (!string.Equals(_config.OneFingerFastFlickAction, "BrowserBackForward", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        long dt = nowMs - f0.StartMs;
        double dx = f0.CurX - f0.StartX;
        double dy = f0.CurY - f0.StartY;

        if (dt >= 25 && dt <= 200 &&
            Math.Abs(dx) >= 580 * invSens &&
            Math.Abs(dy) <= 150 &&
            (Math.Abs(dx) / dt) >= 3.2 * invSens)
        {
            bool flickLeft = dx < 0;
            // On macOS Safari with Natural Swipe: flicking right goes Back, flicking left goes Forward
            GestureAction act = _config.NaturalTwoFingerSwipe
                ? (flickLeft ? GestureAction.BrowserForward : GestureAction.BrowserBack)
                : (flickLeft ? GestureAction.BrowserBack : GestureAction.BrowserForward);

            Fire(act, flickLeft ? "Hất 1 ngón ← Nhanh" : "Hất 1 ngón → Nhanh", nowMs);
        }
    }

    private void Fire(GestureAction action, string label, long nowMs)
    {
        _episodeConsumed = true;
        _lastGestureMs = nowMs;
        _onGestureTriggered(action, label);
    }

    private void ResetEpisode()
    {
        _episodePeakIntentional = 0;
        _episodeStartMs = 0;
        _episodeSecondFingerEnterMs = 0;
        _maxTravelInEpisode = 0;
        _episodeConsumed = false;
        _hadPhysicalClick = false;
    }

    private static GestureAction ResolveHorizontal(string setting, bool swipedLeft, bool natural)
    {
        // With Natural Swipe ON: swiping left reveals next item on the right
        bool goNext = natural ? swipedLeft : !swipedLeft;
        return setting switch
        {
            "VirtualDesktop" => goNext ? GestureAction.VirtualDesktopRight : GestureAction.VirtualDesktopLeft,
            "BrowserBackForward" => goNext ? GestureAction.BrowserForward : GestureAction.BrowserBack,
            "AltTab" => goNext ? GestureAction.AltTabNext : GestureAction.AltTabPrev,
            _ => GestureAction.None
        };
    }

    private static GestureAction ResolveVertical(string setting, bool swipedUp) => setting switch
    {
        "TaskView" => GestureAction.TaskView,
        "ShowDesktop" => GestureAction.ShowDesktop,
        "MaximizeWindow" => GestureAction.MaximizeWindow,
        "MinimizeWindow" => GestureAction.MinimizeWindow,
        _ => GestureAction.None
    };

    private static GestureAction ResolveSingleAction(string setting) => setting switch
    {
        "TaskView" => GestureAction.TaskView,
        "ShowDesktop" => GestureAction.ShowDesktop,
        "MiddleClick" => GestureAction.MiddleClick,
        _ => GestureAction.None
    };

    private static GestureAction ResolveThreeFingerTap(string setting) => setting switch
    {
        "MiddleClick" => GestureAction.MiddleClick,
        "TaskView" => GestureAction.TaskView,
        "ShowDesktop" => GestureAction.ShowDesktop,
        _ => GestureAction.None
    };
}
