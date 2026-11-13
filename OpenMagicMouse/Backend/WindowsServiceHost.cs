using System.Runtime.InteropServices;

namespace OpenMagicMouse.Backend;

/// <summary>
/// Minimal zero-dependency Windows Service SCM host for OpenMagicMouseService.
/// </summary>
public static class WindowsServiceHost
{
    public const string ServiceName = "OpenMagicMouseService";

    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceAcceptStop = 0x00000001;
    private const uint ServiceAcceptShutdown = 0x00000004;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceControlShutdown = 0x00000005;

    private static readonly ManualResetEventSlim StopEvent = new(false);
    private static nint _statusHandle;
    private static ServiceMainCallback? _serviceMainDelegate;
    private static ServiceControlHandlerEx? _ctrlHandlerDelegate;

    public static void Run()
    {
        _serviceMainDelegate = ServiceMain;
        nint namePtr = Marshal.StringToHGlobalUni(ServiceName);
        try
        {
            var table = new SERVICE_TABLE_ENTRYW[]
            {
                new() { lpServiceName = namePtr, lpServiceProc = Marshal.GetFunctionPointerForDelegate(_serviceMainDelegate) },
                new() { lpServiceName = nint.Zero, lpServiceProc = nint.Zero }
            };
            StartServiceCtrlDispatcherW(table);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    private static void ServiceMain(uint argc, nint argv)
    {
        _ctrlHandlerDelegate = ControlHandler;
        _statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, _ctrlHandlerDelegate, nint.Zero);
        if (_statusHandle == nint.Zero)
        {
            return;
        }

        ReportStatus(ServiceStartPending, 0, 3000);

        PipeTouchServer? pipeServer = null;
        BthportEtwCapture? etw = null;

        try
        {
            etw = new BthportEtwCapture(frame => pipeServer?.BroadcastFrame(frame));
            pipeServer = new PipeTouchServer(() => TouchWireProtocol.SerializeHello(
                etw.CurrentBatteryPercent,
                etw.IsMouseConnected,
                etw.DecodedFrameCount,
                etw.ScrubbedPacketCount,
                etw.TotalEvent402Count,
                etw.MaxEvent402Bytes));

            pipeServer.Start();
            etw.Start();

            ReportStatus(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown, 0);
            StopEvent.Wait();
        }
        catch
        {
            ReportStatus(ServiceStopPending, 0, 2000);
        }
        finally
        {
            try { etw?.Dispose(); } catch { }
            try { pipeServer?.Dispose(); } catch { }
            ReportStatus(ServiceStopped, 0, 0);
        }
    }

    private static uint ControlHandler(uint control, uint eventType, nint eventData, nint context)
    {
        if (control == ServiceControlStop || control == ServiceControlShutdown)
        {
            ReportStatus(ServiceStopPending, 0, 3000);
            StopEvent.Set();
            return 0;
        }
        return 0;
    }

    private static void ReportStatus(uint currentState, uint controlsAccepted, uint waitHint)
    {
        if (_statusHandle == nint.Zero) return;
        var status = new SERVICE_STATUS
        {
            dwServiceType = ServiceWin32OwnProcess,
            dwCurrentState = currentState,
            dwControlsAccepted = controlsAccepted,
            dwWin32ExitCode = 0,
            dwServiceSpecificExitCode = 0,
            dwCheckPoint = 0,
            dwWaitHint = waitHint
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(uint dwNumServicesArgs, nint lpServiceArgVectors);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServiceControlHandlerEx(uint dwControl, uint dwEventType, nint lpEventData, nint lpContext);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_TABLE_ENTRYW
    {
        public nint lpServiceName;
        public nint lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRYW[] lpServiceStartTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandlerExW(string lpServiceName, ServiceControlHandlerEx lpHandlerProc, nint lpContext);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(nint hServiceStatus, ref SERVICE_STATUS lpServiceStatus);
}
