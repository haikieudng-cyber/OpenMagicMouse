using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using OpenMagicMouse.Models;

namespace OpenMagicMouse.Backend;

/// <summary>
/// Connects to OpenMagicMouseService via Named Pipe, or falls back to
/// in-process ETW capture if running elevated and the service is unavailable.
/// </summary>
public sealed class PipeTouchClient : IDisposable
{
    public event Action<TouchFrame>? OnFrameReceived;
    public event Action<bool, int, string>? OnConnectionChanged;

    private readonly CancellationTokenSource _cts = new();
    private Thread? _workerThread;
    private BthportEtwCapture? _fallbackCapture;
    private bool _disposed;

    public void Start()
    {
        _workerThread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "OpenMagicMouse.PipeClient"
        };
        _workerThread.Start();
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private void RunLoop()
    {
        bool triedFallback = false;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    PipeTouchServer.PipeName,
                    PipeDirection.In,
                    PipeOptions.Asynchronous);

                pipe.Connect(1500);
                triedFallback = false;

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                OnConnectionChanged?.Invoke(true, -1, "Dịch vụ hệ thống (LocalSystem)");

                while (!_cts.IsCancellationRequested)
                {
                    string? line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    if (TouchWireProtocol.TryParseHello(line, out int batt, out bool mouseConn, out long decoded, out long scrubbed, out _, out _))
                    {
                        if (scrubbed > 0 && decoded == 0)
                        {
                            OnConnectionChanged?.Invoke(false, batt, "Cần bật EtwLogSensitiveData cho Bluetooth (chạy Install-OpenMagicMouse.ps1)");
                        }
                        else
                        {
                            string msg = mouseConn
                                ? (decoded > 0
                                    ? $"Magic Mouse 2 đang kết nối ({decoded} khung)"
                                    : "Magic Mouse 2 sẵn sàng — Chạm chuột để bắt đầu")
                                : "Dịch vụ Đa điểm 125Hz sẵn sàng — Chạm chuột để bắt đầu";
                            OnConnectionChanged?.Invoke(true, batt, msg);
                        }
                    }
                    else if (TouchWireProtocol.TryParseFrame(line, out var frame) && frame != null)
                    {
                        OnFrameReceived?.Invoke(frame);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (!triedFallback && IsAdministrator())
                {
                    triedFallback = true;
                    if (TryRunInProcessFallback())
                    {
                        continue;
                    }
                }

                OnConnectionChanged?.Invoke(false, -1, "Đang kết nối lại OpenMagicMouseService...");
                if (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(1000);
                }
            }
        }
    }

    private bool TryRunInProcessFallback()
    {
        try
        {
            _fallbackCapture = new BthportEtwCapture(frame => OnFrameReceived?.Invoke(frame));
            _fallbackCapture.Start();
            OnConnectionChanged?.Invoke(true, _fallbackCapture.CurrentBatteryPercent, "Chế độ Admin trực tiếp (ETW)");

            while (!_cts.IsCancellationRequested)
            {
                Thread.Sleep(500);
            }
            return true;
        }
        catch
        {
            _fallbackCapture?.Dispose();
            _fallbackCapture = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _fallbackCapture?.Dispose();
        try { _workerThread?.Join(500); } catch { }
        _cts.Dispose();
    }
}
