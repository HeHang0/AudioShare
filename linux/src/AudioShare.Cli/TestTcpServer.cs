using System.Net;
using System.Net.Sockets;

namespace AudioShare.Cli;

/// <summary>
/// 模拟 Android 端 TcpService 的接收端（严格按 android TcpService.java 的读取/应答语义实现），
/// 用于在没有真机时验证客户端真实字节流、多设备分发与静音保活。
/// </summary>
public sealed class TestTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = [];
    private readonly object _gate = new();

    private List<byte> _audioStream = [];
    private List<byte> _controlStream = [];
    private int _audioSessions;
    private int _controlSessions;
    private int _handshakeLength = -1;
    private int _handshakeSampleRate = -1;
    private int _handshakeChannelMask = -1;
    private int _ackWritten;

    public TestTcpServer(string label)
    {
        Label = label;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _tasks.Add(AcceptLoopAsync());
    }

    public string Label { get; }

    public int Port { get; }

    /// <summary>连接已建立（握手完成后）的等待点。</summary>
    public TaskCompletionSource<bool> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int AudioSessions { get { lock (_gate) return _audioSessions; } }

    public int ControlSessions { get { lock (_gate) return _controlSessions; } }

    public int HandshakeLength { get { lock (_gate) return _handshakeLength; } }

    public int HandshakeSampleRate { get { lock (_gate) return _handshakeSampleRate; } }

    public int HandshakeChannelMask { get { lock (_gate) return _handshakeChannelMask; } }

    public int AckBytesWritten => Volatile.Read(ref _ackWritten);

    public byte[] AudioStreamSnapshot()
    {
        lock (_gate) return [.. _audioStream];
    }

    public byte[] ControlStreamSnapshot()
    {
        lock (_gate) return [.. _controlStream];
    }

    public void ResetAudioStream()
    {
        lock (_gate) _audioStream = [];
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => HandleClientAsync(client, token), token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var head = new byte[Protocol.Head.Length];
            if (!await ReadFullyAsync(stream, head, 0, head.Length, token).ConfigureAwait(false)) return;
            if (System.Text.Encoding.ASCII.GetString(head) != Protocol.Head) return;
            var command = new byte[1];
            if (!await ReadFullyAsync(stream, command, 0, 1, token).ConfigureAwait(false)) return;

            if (command[0] == (byte)Command.AudioData)
            {
                var parameters = new byte[8];
                if (!await ReadFullyAsync(stream, parameters, 0, 8, token).ConfigureAwait(false)) return;
                lock (_gate)
                {
                    _audioSessions++;
                    _handshakeSampleRate = BitConverter.ToInt32(parameters, 0);
                    _handshakeChannelMask = BitConverter.ToInt32(parameters, 4);
                }
                // Android 端在 AudioTrack 就绪后回 1 字节（TcpService.java:442-443）
                var ack = new byte[1];
                await stream.WriteAsync(ack, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                Interlocked.Increment(ref _ackWritten);
                Connected.TrySetResult(true);
                await ReadAudioFramesAsync(stream, token).ConfigureAwait(false);
            }
            else
            {
                var control = new List<byte> { command[0] };
                if (command[0] == (byte)Command.Volume)
                {
                    var payload = new byte[4];
                    if (await ReadFullyAsync(stream, payload, 0, 4, token).ConfigureAwait(false))
                    {
                        control.AddRange(payload);
                    }
                }
                lock (_gate)
                {
                    _controlSessions++;
                    _controlStream.AddRange(control);
                }
                // 控制连接：Android 端处理完即关闭；这里回 1 字节确认（windows/Speaker.cs 会等这一字节）
                await stream.WriteAsync(new byte[1], token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                client.Close();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            try
            {
                client.Dispose();
            }
            catch
            {
            }
        }
    }

    private async Task ReadAudioFramesAsync(NetworkStream stream, CancellationToken token)
    {
        var lengthBuffer = new byte[4];
        while (!token.IsCancellationRequested)
        {
            if (!await ReadFullyAsync(stream, lengthBuffer, 0, 4, token).ConfigureAwait(false)) return;
            var length = BitConverter.ToInt32(lengthBuffer, 0);
            if (length == 0)
            {
                lock (_gate)
                {
                    _audioStream.AddRange(lengthBuffer);
                    _handshakeLength = 0;
                }
                continue;
            }
            if (length < 0 || length > 4 * 1024 * 1024) return;
            var payload = new byte[length];
            if (!await ReadFullyAsync(stream, payload, 0, length, token).ConfigureAwait(false)) return;
            lock (_gate)
            {
                _audioStream.AddRange(lengthBuffer);
                _audioStream.AddRange(payload);
                _handshakeLength = length;
            }
        }
    }

    private static async Task<bool> ReadFullyAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
    {
        var read = 0;
        while (read < count)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), token).ConfigureAwait(false);
            if (got <= 0) return false;
            read += got;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await Task.WhenAll(_tasks).ConfigureAwait(false);
        }
        catch
        {
        }
        _cts.Dispose();
    }
}
