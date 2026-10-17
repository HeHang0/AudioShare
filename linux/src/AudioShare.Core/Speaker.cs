using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AudioShare;

/// <summary>
/// 单台设备（Android 客户端）的连接。对应 windows/Speaker.cs：
/// - 每设备一条音频 TCP 连接（命令 1），控制指令走独立短连接（命令 2/3/4）。
/// - 音频帧格式：[4 字节小端长度][PCM]，长度为 0 即心跳。
/// - 发送忙时丢帧（Windows 同款 isBusy 语义），避免阻塞采集节拍。
/// </summary>
public sealed class Speaker : IDisposable
{
    private static readonly Regex AddressPattern = new(
        @"^(?:\[(?<v6>[0-9A-Fa-f:]+)\]|(?<ip>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})|(?<bare>[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}))(?::(?<port>\d+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _stateGate = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _connectionCts;
    private System.Threading.Channels.Channel<(byte[] Data, bool IsFrame)>? _queue;
    private bool _retried;
    private bool _disposed;
    private long _lastSendUnixSeconds;
    private long _framesSent;
    private long _framesDropped;
    private long _bytesSent;
    private string? _lastError;

    public Speaker(string id, AudioChannel channel = AudioChannel.None, bool isUsb = false, string name = "")
    {
        Id = id;
        Channel = channel;
        IsUsb = isUsb;
        Name = name;
    }

    public string Id { get; private set; }

    public string Name { get; }

    public bool IsUsb { get; }

    public AudioChannel Channel { get; set; }

    public string Display => IsUsb && !string.IsNullOrEmpty(Name) ? $"{Name} [{Id}]" : Id;

    /// <summary>GUI 允许编辑非 USB 设备的显示串（windows/Speaker.cs 的 Display setter 等价）。</summary>
    public void SetDisplayForUi(string value)
    {
        if (IsUsb) return;
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed)) return;
        Id = trimmed;
    }

    public string RemoteAddress { get; private set; } = string.Empty;

    public int RemotePort { get; private set; } = -1;

    public int ForwardPort { get; set; }

    public int HttpPort { get; set; }

    public string? HttpAddress { get; set; }

    public ConnectStatus Status { get; private set; } = ConnectStatus.UnConnected;

    public bool Connected => Status == ConnectStatus.Connected;

    public bool Connecting => Status == ConnectStatus.Connecting;

    public bool UnConnected => Status != ConnectStatus.Connected;

    public long FramesSent => Interlocked.Read(ref _framesSent);

    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public string? LastError => _lastError;

    /// <summary>旁录统计（仅测试使用）：OnWireWrite 已挂载时的写入次数与字节数。</summary>
    public long TappedWrites { get; private set; }

    public long TappedBytes { get; private set; }

    /// <summary>旁录统计：OnWireWrite 未挂载时的写入次数。</summary>
    public long UntappedWrites { get; private set; }

    /// <summary>重试次数（自动重连一次，对应 windows/Speaker.cs:290-301）。</summary>
    public int ReconnectCount { get; private set; }

    public event EventHandler<ConnectStatus>? StatusChanged;

    public event EventHandler<Speaker>? Disconnected;

    /// <summary>发送前回调（仅测试使用，用于在真实字节层面取样）。</summary>
    public Action<byte[], int>? OnWireWrite { get; set; }

    /// <summary>连接成功后回调（仅测试使用，用于在首次写入前挂载旁录钩子）。</summary>
    public Action<Speaker>? OnConnectedForTest { get; set; }

    /// <summary>控制指令发送前回调（仅测试使用）。</summary>
    public Action<Speaker, Command>? BeforeControlForTest { get; set; }

    public Func<CancellationToken, Task>? PortForwardAcquire { get; set; }

    public Func<CancellationToken, Task>? PortForwardRelease { get; set; }

    /// <summary>供 GUI 校验并解析 IP:端口（等价 windows/Speaker.cs 的正则解析）。</summary>
    public bool TryParseAddress(out string ip, out int port)
    {
        ip = string.Empty;
        port = -1;
        var match = AddressPattern.Match(Id);
        if (!match.Success) return false;
        ip = match.Groups["v6"].Success
            ? match.Groups["v6"].Value
            : match.Groups["ip"].Success ? match.Groups["ip"].Value : match.Groups["bare"].Value;
        port = int.TryParse(match.Groups["port"].Value, out var parsed) ? parsed : 80; // 与 windows/Speaker.cs:205-208 一致
        return true;
    }

    public async Task<bool> ConnectAsync(bool retry = false, CancellationToken token = default)
    {
        if (Connecting) return false;
        _disposed = false;
        await DisconnectAsync(retry: retry).ConfigureAwait(false);
        SetStatus(ConnectStatus.Connecting);
        Logger.Info($"connect start: {Display} channel={Channel} usb={IsUsb} retry={retry}");
        try
        {
            if (!retry)
            {
                if (IsUsb)
                {
                    if (PortForwardAcquire != null) await PortForwardAcquire(token).ConfigureAwait(false);
                    RemoteAddress = "127.0.0.1";
                    RemotePort = ForwardPort;
                }
                else
                {
                    if (!TryParseAddress(out var ip, out var port))
                    {
                        throw new InvalidOperationException($"ip parse error: {Id}");
                    }
                    RemoteAddress = ip;
                    RemotePort = port;
                }
            }

            // 建连前先发 Stop（命令 4），终止设备上可能存在的旧会话（windows/Speaker.cs:217）
            await SendControlAsync(Command.Stop, null, force: true, token).ConfigureAwait(false);

            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Parse(RemoteAddress), RemotePort, token).ConfigureAwait(false);
            if (!client.Connected)
            {
                client.Dispose();
                throw new IOException("tcp connect failed");
            }

            _client = client;
            _stream = client.GetStream();
            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var connectionToken = _connectionCts.Token;

            // 握手：head + 命令 1 + 采样率 + 声道掩码，随后等待 1 字节确认
            OnConnectedForTest?.Invoke(this);
            await WriteAsync(Protocol.HeadBytes, connectionToken).ConfigureAwait(false);
            await WriteAsync(Protocol.CommandByte(Command.AudioData), connectionToken).ConfigureAwait(false);
            await WriteAsync(Protocol.Int32LittleEndian(SampleRate), connectionToken).ConfigureAwait(false);
            await WriteAsync(Protocol.Int32LittleEndian(Protocol.ChannelMask(Channel)), connectionToken).ConfigureAwait(false);
            await ReadAckAsync(connectionToken).ConfigureAwait(false);

            _queue = System.Threading.Channels.Channel.CreateBounded<(byte[], bool)>(
                new System.Threading.Channels.BoundedChannelOptions(QueueCapacityFrames)
                {
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                });
            _ = Task.Run(() => ReadLoopAsync(connectionToken), connectionToken);
            _ = Task.Run(() => WriteLoopAsync(connectionToken), connectionToken);

            SetStatus(ConnectStatus.Connected);
            _retried = false;
            Logger.Info($"connect end: {Display} -> {RemoteAddress}:{RemotePort} rate={SampleRate} mask={Protocol.ChannelMask(Channel)}");
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Logger.Error($"connect error: {Display} {ex.Message}");
            await DisconnectAsync(toast: false, retry: retry).ConfigureAwait(false);
            return false;
        }
    }

    public int SampleRate { get; set; } = 48000;

    /// <summary>发送队列容量（帧）。有界：队列满时丢弃最旧帧，避免网络抖动后积压成秒级的突发。</summary>
    public const int QueueCapacityFrames = 3;

    /// <summary>帧长（毫秒）：相邻音频帧的最小发送间隔，需与采集引擎一致。</summary>
    public int FrameMilliseconds { get; set; } = AudioEngine.DefaultFrameMilliseconds;

    /// <summary>由设备池在采集帧到达时调用；忙则丢帧（对应 windows/Speaker.cs:283-306）。</summary>
    public void OnPcmFrame(byte[] interleavedFrame)
    {
        if (Status != ConnectStatus.Connected || Channel == AudioChannel.None)
        {
            return;
        }

        var payload = Channel == AudioChannel.Stereo ? interleavedFrame : ChannelSplitter.ForChannel(interleavedFrame, Channel);
        if (!Protocol.IsValidFrameLength(payload.Length))
        {
            Logger.Warning($"drop invalid frame length {payload.Length}");
            return;
        }

        var queue = _queue;
        if (queue == null)
        {
            Interlocked.Increment(ref _framesDropped);
            return;
        }

        // 有界队列：满时 DropOldest 会丢弃最旧一帧，计为丢帧（延迟因此有界，不会积压成突发）
        if (queue.Reader.CanCount && queue.Reader.Count >= QueueCapacityFrames) Interlocked.Increment(ref _framesDropped);
        queue.Writer.TryWrite((payload, true));
    }

    /// <summary>
    /// 发送循环。除音频帧/心跳帧外，还内建"静默补心跳"：只要距上次写入超过 5 秒就投递长度 0 帧
    /// （windows/Model.cs:38-42 + windows/Speaker.cs:461-474 的等价语义，且不依赖外部定时器）。
    /// </summary>
    private async Task WriteLoopAsync(CancellationToken token)
    {
        var queue = _queue;
        if (queue == null) return;
        try
        {
            var reader = queue.Reader;
            var lastFrameAt = 0L;
            while (!token.IsCancellationRequested)
            {
                (byte[] Data, bool IsFrame) item;
                try
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCts.CancelAfter(500);
                    item = await reader.ReadAsync(readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // 500ms 内无数据：处理心跳
                    IdleTicks++;
                    await SendHeartbeatIfIdleAsync(token).ConfigureAwait(false);
                    continue;
                }

                if (item.IsFrame && lastFrameAt != 0)
                {
                    // 相邻音频帧至少间隔一个帧长：设备端每帧都要回主线程清 isWriting，
                    // 突发的连续帧会被判为 "write audio busy" 而直接丢弃（TcpService.java:462-465）
                    var wait = TimeSpan.FromMilliseconds(FrameMilliseconds) - Stopwatch.GetElapsedTime(lastFrameAt);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                }
                var frame = item.IsFrame ? Protocol.EncodeFrame(item.Data) : item.Data;
                await WriteAsync(frame, token).ConfigureAwait(false);
                Interlocked.Add(ref _bytesSent, frame.Length);
                if (item.IsFrame)
                {
                    Interlocked.Increment(ref _framesSent);
                    lastFrameAt = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Logger.Error($"write loop error: {Display} {ex.Message}");
            if (!_disposed) _ = HandleWriteFailureAsync();
        }
    }

    private async Task HandleWriteFailureAsync()
    {
        if (_disposed) return;
        if (!_retried && Status == ConnectStatus.Connected)
        {
            _retried = true;
            ReconnectCount++;
            Logger.Warning($"audio write failed, retrying once: {Display}");
            await ConnectAsync(retry: true).ConfigureAwait(false);
        }
        else
        {
            await DisconnectAsync(toast: true).ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        // Windows 端保持读循环（windows/Speaker.cs:260-274）：既用于消费 Android 每 10s 的 1 字节心跳，
        // 也用于及时发现对端关闭。
        var buffer = new byte[512];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var stream = _stream;
                if (stream == null) return;
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0) break;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
        Logger.Warning($"remote closed: {Display}");
        if (_disposed) return;
        await DisconnectAsync(toast: true).ConfigureAwait(false);
    }

    /// <summary>静默超时（>5s）就补一帧长度 0；写失败即断开（windows/Speaker.cs:461-474）。</summary>
    private async Task SendHeartbeatIfIdleAsync(CancellationToken token)
    {
        if (Status != ConnectStatus.Connected) return;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Interlocked.Read(ref _lastSendUnixSeconds) <= 5) return;
        try
        {
            await WriteAsync(Protocol.HeartbeatBytes, token).ConfigureAwait(false);
            Interlocked.Increment(ref _heartbeatsSent);
            Logger.Info($"heartbeat sent: {Display}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Logger.Warning($"heartbeat failed: {Display} {ex.Message}");
            if (!_disposed) await DisconnectAsync().ConfigureAwait(false);
        }
    }

    /// <summary>心跳计数（验收：静默期间仍持续发送长度 0 帧）。</summary>
    public long HeartbeatsSent => Interlocked.Read(ref _heartbeatsSent);

    private long _heartbeatsSent;

    /// <summary>写循环中的空转计数（仅测试/诊断使用）。</summary>
    public long IdleTicks { get; private set; }

    /// <summary>心跳：距上次发送 > 5 秒则补一帧长度 0，失败即断开（windows/Speaker.cs:461-474）。</summary>
    public void SendHeartbeat()
    {
        if (Status != ConnectStatus.Connected) return;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Interlocked.Read(ref _lastSendUnixSeconds) <= 5) return;
        var queue = _queue;
        if (queue == null || !queue.Writer.TryWrite((Protocol.HeartbeatBytes, false)))
        {
            _ = DisconnectAsync();
        }
    }

    public async Task SetVolumeAsync(int volume, CancellationToken token = default)
        => await SendControlAsync(Command.Volume, Protocol.Int32LittleEndian(Math.Clamp(volume, 0, 100)), force: false, token)
            .ConfigureAwait(false);

    public async Task SyncTimeAsync(CancellationToken token = default)
        => await SendControlAsync(Command.SyncTime, null, force: false, token).ConfigureAwait(false);

    /// <summary>控制指令使用独立短连接：head + 命令 [+ 4 字节小端参数]，随后读 1 字节确认。</summary>
    public async Task<bool> SendControlAsync(Command command, byte[]? payload, bool force, CancellationToken token = default)
    {
        if (command == Command.None || string.IsNullOrWhiteSpace(RemoteAddress) || RemotePort <= 0) return false;
        if (!force && Status != ConnectStatus.Connected) return false;
        BeforeControlForTest?.Invoke(this, command);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Parse(RemoteAddress), RemotePort, timeoutCts.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var head = Protocol.HeadBytes;
            await stream.WriteAsync(head, timeoutCts.Token).ConfigureAwait(false);
            var commandBytes = Protocol.CommandByte(command);
            await stream.WriteAsync(commandBytes, timeoutCts.Token).ConfigureAwait(false);
            if (payload is { Length: > 0 })
            {
                await stream.WriteAsync(payload, timeoutCts.Token).ConfigureAwait(false);
            }
            await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            var ack = new byte[1];
            await stream.ReadAsync(ack, timeoutCts.Token).ConfigureAwait(false);
            Logger.Info($"control {command} sent to {Display}");
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Logger.Warning($"control {command} failed to {Display}: {ex.Message}");
            return false;
        }
    }

    private async Task WriteAsync(byte[] data, CancellationToken token)
    {
        var stream = _stream ?? throw new IOException("not connected");
        var tap = OnWireWrite;
        if (tap == null)
        {
            UntappedWrites++;
        }
        else
        {
            TappedWrites++;
            TappedBytes += data.Length;
        }
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastSendUnixSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        tap?.Invoke(data, 3);
    }

    private async Task ReadAckAsync(CancellationToken token)
    {
        var stream = _stream ?? throw new IOException("not connected");
        var ack = new byte[1];
        var read = 0;
        while (read < 1)
        {
            var count = await stream.ReadAsync(ack.AsMemory(read, 1 - read), token).ConfigureAwait(false);
            if (count <= 0) throw new IOException("handshake ack missing");
            read += count;
        }
    }

    public async Task DisconnectAsync(bool toast = false, bool retry = false)
    {
        if (Status == ConnectStatus.UnConnected && _client == null) return;
        SetStatus(ConnectStatus.UnConnected, toast);
        Logger.Info($"disconnect start: {Display}");
        if (!retry)
        {
            RemoteAddress = string.Empty;
            RemotePort = -1;
        }

        try
        {
            _connectionCts?.Cancel();
        }
        catch
        {
            // 忽略取消异常
        }

        try
        {
            _client?.Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"close socket error: {Display} {ex.Message}");
        }

        _client = null;
        _stream = null;
        _queue = null;
        _connectionCts?.Dispose();
        _connectionCts = null;

        if (PortForwardRelease != null)
        {
            try
            {
                await PortForwardRelease(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning($"forward release failed: {ex.Message}");
            }
        }

        Logger.Info($"disconnect end: {Display}");
    }

    private void SetStatus(ConnectStatus status, bool toast = false)
    {
        if (Status == status) return;
        var previous = Status;
        Status = status;
        Logger.Info($"set connect status {Display}: {previous} -> {status}");
        StatusChanged?.Invoke(this, status);
        if (toast && status == ConnectStatus.UnConnected)
        {
            Disconnected?.Invoke(this, this);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _ = DisconnectAsync();
    }
}
