using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using OpenMagicMouse.Models;

namespace OpenMagicMouse.Backend;

/// <summary>
/// Named pipe server (\\.\pipe\OpenMagicMouseTouch) owned by the LocalSystem service.
/// Streams live decoded touch frames to unprivileged user Tray/UI instances.
/// </summary>
public sealed class PipeTouchServer : IDisposable
{
    public const string PipeName = "OpenMagicMouseTouch";
    private readonly ConcurrentDictionary<int, StreamWriter> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string> _buildHelloLine;
    private int _nextClientId;
    private Thread? _acceptThread;
    private Thread? _heartbeatThread;
    private bool _disposed;

    public PipeTouchServer(Func<string> buildHelloLine)
    {
        _buildHelloLine = buildHelloLine ?? throw new ArgumentNullException(nameof(buildHelloLine));
    }

    public void Start()
    {
        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "OpenMagicMouse.PipeAccept"
        };
        _acceptThread.Start();

        _heartbeatThread = new Thread(HeartbeatLoop)
        {
            IsBackground = true,
            Name = "OpenMagicMouse.PipeHeartbeat"
        };
        _heartbeatThread.Start();
    }

    private void AcceptLoop()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.Out,
                    16,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 65536,
                    pipeSecurity: pipeSecurity);

                var waitTask = server.WaitForConnectionAsync(_cts.Token);
                waitTask.GetAwaiter().GetResult();

                int id = Interlocked.Increment(ref _nextClientId);
                var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: false)
                {
                    AutoFlush = true
                };

                writer.WriteLine(_buildHelloLine());
                _clients[id] = writer;
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                if (!_cts.IsCancellationRequested)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }

    private void HeartbeatLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(2500);
                if (_cts.IsCancellationRequested || _clients.IsEmpty) continue;
                BroadcastLine(_buildHelloLine());
            }
            catch
            {
                // Non-fatal
            }
        }
    }

    public void BroadcastFrame(TouchFrame frame)
    {
        if (_clients.IsEmpty) return;
        BroadcastLine(TouchWireProtocol.SerializeFrame(frame));
    }

    private void BroadcastLine(string line)
    {
        foreach (var kvp in _clients)
        {
            try
            {
                lock (kvp.Value)
                {
                    kvp.Value.WriteLine(line);
                }
            }
            catch
            {
                if (_clients.TryRemove(kvp.Key, out var dead))
                {
                    try { dead.Dispose(); } catch { }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        foreach (var kvp in _clients)
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _clients.Clear();

        try { _acceptThread?.Join(500); } catch { }
        try { _heartbeatThread?.Join(500); } catch { }
        _cts.Dispose();
    }
}
