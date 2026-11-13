using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenMagicMouse.Models;

namespace OpenMagicMouse.Backend;

/// <summary>
/// Captures raw Bluetooth HCI ACL/L2CAP HID frames (Event 402) from
/// Microsoft-Windows-BTH-BTHPORT below applewirelessmouse.sys using
/// real-time ETW with adaptive 8ms/30ms micro-flush and zero packet loss.
/// </summary>
public sealed class BthportEtwCapture : IDisposable
{
    public const string SessionName = "OpenMagicMouseBthSession";
    private static readonly Guid BthPortProviderGuid = new("8a1f9517-3a8c-4a9e-a018-4f17a200f277");

    private const uint EventTraceRealTimeMode = 0x00000100;
    private const uint EventTraceNoPerProcessorBuffering = 0x10000000;
    private const uint ProcessTraceModeRealTime = 0x00000100;
    private const uint ProcessTraceModeEventRecord = 0x10000000;
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint EventControlCodeEnableProvider = 1;
    private const uint EventTraceControlStop = 1;
    private const uint EventTraceControlFlush = 3;
    private const ulong InvalidTraceHandle = ulong.MaxValue;
    private const int PropertiesSize = 4096;

    private readonly Action<TouchFrame> _onFrame;
    private readonly Action<string>? _onLog;
    private readonly CancellationTokenSource _cts = new();
    private readonly L2capReassembler _reassembler = new();

    private EventRecordCallback? _callbackDelegate;
    private ulong _sessionHandle;
    private ulong _traceHandle = InvalidTraceHandle;
    private nint _logfilePtr;
    private nint _sessionNamePtr;
    private Thread? _processThread;
    private Thread? _flushThread;

    private long _lastPacketMs;
    private long _lastBatteryPollMs;
    private int _batteryPollInFlight;
    private int _batteryPercent = -1;
    private bool _isConnected;
    private long _totalEvent402Count;
    private long _decodedFrameCount;
    private long _scrubbedPacketCount;
    private int _maxEvent402Bytes;
    private bool _disposed;

    public int CurrentBatteryPercent => Volatile.Read(ref _batteryPercent);
    public bool IsMouseConnected => Volatile.Read(ref _isConnected);
    public long TotalEvent402Count => Interlocked.Read(ref _totalEvent402Count);
    public long DecodedFrameCount => Interlocked.Read(ref _decodedFrameCount);
    public long ScrubbedPacketCount => Interlocked.Read(ref _scrubbedPacketCount);
    public int MaxEvent402Bytes => Volatile.Read(ref _maxEvent402Bytes);

    public BthportEtwCapture(Action<TouchFrame> onFrame, Action<string>? onLog = null)
    {
        _onFrame = onFrame ?? throw new ArgumentNullException(nameof(onFrame));
        _onLog = onLog;
    }

    public unsafe void Start()
    {
        StopExistingSession();

        nint props = AllocateProperties();
        try
        {
            uint rc = StartTraceW(out _sessionHandle, SessionName, props);
            if (rc != 0)
            {
                throw new InvalidOperationException($"StartTraceW('{SessionName}') failed with Win32 error {rc}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        Guid provider = BthPortProviderGuid;
        uint rcEnable = EnableTraceEx2(
            _sessionHandle,
            ref provider,
            EventControlCodeEnableProvider,
            5, // TRACE_LEVEL_VERBOSE
            0xFFFFFFFFFFFFFFFFUL, // All keywords including HCIRAW (0x8000000000000000)
            0,
            0,
            nint.Zero);

        if (rcEnable != 0)
        {
            StopTraceSafe();
            throw new InvalidOperationException($"EnableTraceEx2 failed with Win32 error {rcEnable}.");
        }

        _callbackDelegate = OnEventRecord;
        _sessionNamePtr = Marshal.StringToHGlobalUni(SessionName);
        _logfilePtr = Marshal.AllocHGlobal(448);
        unsafe
        {
            NativeMemory.Clear((void*)_logfilePtr, 448);
            Marshal.WriteIntPtr(_logfilePtr, 8, _sessionNamePtr);
            Marshal.WriteInt32(_logfilePtr, 28, unchecked((int)(ProcessTraceModeRealTime | ProcessTraceModeEventRecord)));
            Marshal.WriteIntPtr(_logfilePtr, 424, Marshal.GetFunctionPointerForDelegate(_callbackDelegate));
        }

        _traceHandle = OpenTraceW(_logfilePtr);
        if (_traceHandle == InvalidTraceHandle)
        {
            int err = Marshal.GetLastWin32Error();
            StopTraceSafe();
            throw new InvalidOperationException($"OpenTraceW failed with Win32 error {err}.");
        }

        _processThread = new Thread(ProcessTraceLoop)
        {
            IsBackground = true,
            Name = "OpenMagicMouse.ETWProcess"
        };
        _processThread.Start();

        _flushThread = new Thread(AdaptiveFlushLoop)
        {
            IsBackground = true,
            Name = "OpenMagicMouse.ETWFlush",
            Priority = ThreadPriority.AboveNormal
        };
        _flushThread.Start();

        _onLog?.Invoke("ETW session started cleanly on BTHPORT HCIRAW.");
    }

    private void ProcessTraceLoop()
    {
        try
        {
            ulong handle = _traceHandle;
            if (handle != InvalidTraceHandle)
            {
                ProcessTrace(ref handle, 1, nint.Zero, nint.Zero);
            }
        }
        catch (Exception ex)
        {
            _onLog?.Invoke($"ProcessTrace loop exited: {ex.Message}");
        }
    }

    private void AdaptiveFlushLoop()
    {
        nint flushProps = AllocateProperties();
        bool highResActive = false;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                long lastPkt = Volatile.Read(ref _lastPacketMs);
                bool active = lastPkt != 0 && (now - lastPkt) < 1500;

                if (active && !highResActive)
                {
                    timeBeginPeriod(2);
                    highResActive = true;
                }
                else if (!active && highResActive)
                {
                    timeEndPeriod(2);
                    highResActive = false;
                }

                ControlTraceW(_sessionHandle, null, flushProps, EventTraceControlFlush);
                Thread.Sleep(active ? 8 : 30);
            }
        }
        catch (Exception ex)
        {
            _onLog?.Invoke($"AdaptiveFlushLoop exited: {ex.Message}");
        }
        finally
        {
            if (highResActive)
            {
                timeEndPeriod(2);
            }
            Marshal.FreeHGlobal(flushProps);
        }
    }

    private unsafe void OnEventRecord(EVENT_RECORD* rec)
    {
        if (rec == null || rec->EventHeader.EventDescriptor.Id != 402 || rec->UserData == nint.Zero || rec->UserDataLength < 8)
        {
            return;
        }

        Interlocked.Increment(ref _totalEvent402Count);
        int uLen = rec->UserDataLength;
        if (uLen > Volatile.Read(ref _maxEvent402Bytes))
        {
            Volatile.Write(ref _maxEvent402Bytes, uLen);
        }

        long wallNow = Environment.TickCount64;
        long pktTsMs = rec->EventHeader.TimeStamp > 0
            ? (long)(rec->EventHeader.TimeStamp * 1000.0 / Stopwatch.Frequency)
            : wallNow;
        var span = new ReadOnlySpan<byte>((void*)rec->UserData, uLen);
        int batt = Volatile.Read(ref _batteryPercent);
        bool conn = Volatile.Read(ref _isConnected);

        if (MagicMousePacketDecoder.TryDecodeEvent402(span, pktTsMs, _reassembler, ref batt, ref conn, out bool wasScrubbed, out var frame))
        {
            Volatile.Write(ref _lastPacketMs, wallNow);
            Volatile.Write(ref _batteryPercent, batt);
            Volatile.Write(ref _isConnected, conn);

            if (frame != null)
            {
                Interlocked.Increment(ref _decodedFrameCount);
                _onFrame(frame);
            }

            long lastBatt = Volatile.Read(ref _lastBatteryPollMs);
            if ((batt < 0 && wallNow - lastBatt > 5000) || (wallNow - lastBatt > 60000))
            {
                TriggerActiveOnlyBatteryPoll(wallNow);
            }
        }
        else
        {
            if (wasScrubbed)
            {
                Interlocked.Increment(ref _scrubbedPacketCount);
                Volatile.Write(ref _isConnected, true);
            }
            if (batt >= 0) Volatile.Write(ref _batteryPercent, batt);
            if (conn) Volatile.Write(ref _isConnected, true);
        }
    }

    private void TriggerActiveOnlyBatteryPoll(long now)
    {
        if (Interlocked.CompareExchange(ref _batteryPollInFlight, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _lastBatteryPollMs, now);
        Task.Run(() =>
        {
            try
            {
                int pct = TryReadMagicMouseBattery();
                if (pct >= 0 && pct <= 100)
                {
                    Volatile.Write(ref _batteryPercent, pct);
                }
            }
            catch
            {
                // Non-fatal
            }
            finally
            {
                Volatile.Write(ref _batteryPollInFlight, 0);
            }
        });
    }

    private static int TryReadMagicMouseBattery()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        nint hDevInfo = SetupDiGetClassDevsW(ref hidGuid, null, nint.Zero, 0x12);
        if (hDevInfo == nint.Zero || hDevInfo == new nint(-1))
        {
            return -1;
        }

        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };

            for (uint idx = 0; SetupDiEnumDeviceInterfaces(hDevInfo, nint.Zero, ref hidGuid, idx, ref did); idx++)
            {
                SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref did, nint.Zero, 0, out uint reqSize, nint.Zero);
                if (reqSize <= 8) continue;

                nint detailBuf = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detailBuf, 0, 8); // x64 SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                    if (!SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref did, detailBuf, reqSize, out _, nint.Zero))
                    {
                        continue;
                    }

                    string path = Marshal.PtrToStringUni(detailBuf + 4) ?? "";
                    if (!path.Contains("vid&0001004c_pid&0269", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    nint hFile = CreateFileW(path, 0, 3, nint.Zero, 3, 0, nint.Zero);
                    if (hFile == nint.Zero || hFile == new nint(-1))
                    {
                        continue;
                    }

                    try
                    {
                        byte[] buf90 = [0x90, 0xFF, 0xFF];
                        if (HidD_GetFeature(hFile, buf90, buf90.Length) && buf90[2] >= 1 && buf90[2] <= 100)
                        {
                            return buf90[2];
                        }

                        byte[] buf47 = [0x47, 0xFF];
                        if (HidD_GetFeature(hFile, buf47, buf47.Length) && buf47[1] >= 1 && buf47[1] <= 100)
                        {
                            return buf47[1];
                        }
                    }
                    finally
                    {
                        CloseHandle(hFile);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }

        return -1;
    }

    private static nint AllocateProperties()
    {
        nint props = Marshal.AllocHGlobal(PropertiesSize);
        unsafe
        {
            NativeMemory.Clear((void*)props, PropertiesSize);
        }

        byte[] guidBytes = Guid.NewGuid().ToByteArray();
        Marshal.WriteInt32(props, 0, PropertiesSize);
        Marshal.Copy(guidBytes, 0, props + 24, 16);
        Marshal.WriteInt32(props, 40, 1); // ClientContext = QPC
        Marshal.WriteInt32(props, 44, unchecked((int)WnodeFlagTracedGuid));

        Marshal.WriteInt32(props, 48, 16); // BufferSize = 16 KB
        Marshal.WriteInt32(props, 52, 4);  // MinimumBuffers
        Marshal.WriteInt32(props, 56, 32); // MaximumBuffers
        Marshal.WriteInt32(props, 64, unchecked((int)(EventTraceRealTimeMode | EventTraceNoPerProcessorBuffering)));
        Marshal.WriteInt32(props, 68, 1);  // FlushTimer = 1 sec fallback
        Marshal.WriteInt32(props, 112, 0);   // LogFileNameOffset = 0 (strictly real-time, NO disk file!)
        Marshal.WriteInt32(props, 116, 256); // LoggerNameOffset = 256

        byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(SessionName + "\0");
        Marshal.Copy(nameBytes, 0, props + 256, nameBytes.Length);
        return props;
    }

    private static void StopExistingSession()
    {
        nint props = AllocateProperties();
        try
        {
            ControlTraceW(0, SessionName, props, EventTraceControlStop);
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    private void StopTraceSafe()
    {
        if (_sessionHandle != 0)
        {
            nint props = AllocateProperties();
            try
            {
                ControlTraceW(_sessionHandle, null, props, EventTraceControlStop);
            }
            finally
            {
                Marshal.FreeHGlobal(props);
                _sessionHandle = 0;
            }
        }

        if (_traceHandle != InvalidTraceHandle)
        {
            CloseTrace(_traceHandle);
            _traceHandle = InvalidTraceHandle;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        StopTraceSafe();

        try { _flushThread?.Join(500); } catch { }
        try { _processThread?.Join(500); } catch { }

        if (_logfilePtr != nint.Zero)
        {
            Marshal.FreeHGlobal(_logfilePtr);
            _logfilePtr = nint.Zero;
        }
        if (_sessionNamePtr != nint.Zero)
        {
            Marshal.FreeHGlobal(_sessionNamePtr);
            _sessionNamePtr = nint.Zero;
        }
        _cts.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate void EventRecordCallback(EVENT_RECORD* eventRecord);

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public fixed byte ProviderId[16];
        public EVENT_DESCRIPTOR EventDescriptor;
        public uint KernelTime;
        public uint UserTime;
        public fixed byte ActivityId[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ETW_BUFFER_CONTEXT
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public nint ExtendedData;
        public nint UserData;
        public nint UserContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong traceHandle, string instanceName, nint properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong traceHandle, string? instanceName, nint properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(
        ulong traceHandle,
        ref Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        nint enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(nint logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ref ulong handleArray, uint handleCount, nint startTime, nint endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(nint hidDeviceObject, [Out] byte[] reportBuffer, int reportBufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet, nint deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        nint deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, nint deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, nint deviceInfoData);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
