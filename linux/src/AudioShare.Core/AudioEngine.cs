using System.Diagnostics;

namespace AudioShare;

/// <summary>采集到的原始立体声 PCM 帧（交错、16bit 小端）。</summary>
public sealed class PcmFrameEventArgs(byte[] data, bool isSilence) : EventArgs
{
    public byte[] Data { get; } = data;
    public bool IsSilence { get; } = isSilence;
}

public sealed record AudioStats(
    long Frames,
    long SilenceFrames,
    long CapturedFrames,
    long Bytes,
    double Seconds,
    int SampleRate,
    string Source,
    string? LastError,
    int FrameMilliseconds = 100)
{
    /// <summary>实际投递速率（帧/秒），用于验证"按实时速率补齐"。</summary>
    public double FramesPerSecond => Seconds <= 0 ? 0 : Frames / Seconds;

    public double RealTimeRatio => ExpectedFramesPerSecond <= 0 ? 0 : FramesPerSecond / ExpectedFramesPerSecond;

    /// <summary>期望帧率（帧/秒），由帧长决定：1000/FrameMs。</summary>
    public double ExpectedFramesPerSecond => FrameMilliseconds <= 0 ? 0 : 1000.0 / FrameMilliseconds;
}

/// <summary>
/// 音频引擎：从 PulseAudio/PipeWire 的 monitor 源采集系统播放声音，并把 PCM 帧稳定地按实时节拍投递。
///
/// 保活机制（对应 PROTOCOL_AND_ARCH.md 第 6 节 / commit dac02fd）：
/// Linux 的 monitor 源在系统静默时会暂停产出数据，若直接透传会导致 TCP 流长时间静默，
/// Android 端 AudioTrack 欠载后即出现"一段时间后静音"。因此本引擎以实时时钟为节拍：
/// 有真实数据就投递真实数据，没有就以 0x00 静音帧补齐，保证每 10ms 恒定一帧。
/// </summary>
public class AudioEngine : IDisposable
{
    /// <summary>
    /// 默认帧长（毫秒）。历史实现固定 10ms，但真机验证发现：安卓端每帧都要回主线程清 isWriting
    /// （TcpService.java:462-477），10ms 帧（100 帧/秒）会大量触发 "write audio busy" 而丢帧断音；
    /// Windows 端 NAudio loopback 默认约 100ms（10 帧/秒）。因此默认取 100ms，与 Windows 端节奏一致，
    /// 并保留可配置能力（越小延迟越低、但真机越容易丢帧）。
    /// </summary>
    public const int DefaultFrameMilliseconds = 100;

    /// <summary>开始投递前预缓冲的帧数（≈200ms）：吸收采集抖动，避免刚起步或稍有抖动就补静音断音。</summary>
    public const int PrebufferFrames = 2;

    /// <summary>预缓冲的最长等待（毫秒）：采集源完全静默时也要尽快开始补静音保活。</summary>
    public const int PrebufferTimeoutMs = 300;

    /// <summary>缓冲超过该帧数（≈600ms）时丢弃最旧数据回到预缓冲水位，防止延迟随时间无限累积。</summary>
    public const int MaxBufferedFrames = 6;

    /// <summary>环形缓冲容量（帧）：远大于正常水位，仅作异常情况下的安全上限。</summary>
    public const int RingCapacityFrames = 16;

    public const int Channels = 2;
    public const int BytesPerSample = 2;

    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private long _frames;
    private long _silenceFrames;
    private long _capturedFrames;
    private string? _lastError;
    private bool _disposed;

    public event EventHandler<PcmFrameEventArgs>? Frame;

    public int SampleRate { get; private set; } = 48000;

    /// <summary>当前帧长（毫秒）。</summary>
    public int FrameMilliseconds { get; private set; } = DefaultFrameMilliseconds;

    /// <summary>采集源：null/空 = @DEFAULT_MONITOR@。</summary>
    public string Source { get; private set; } = string.Empty;

    public bool IsRunning => _pump is { IsCompleted: false };

    public int FrameBytes => Protocol.FrameBytes(SampleRate, FrameMilliseconds);

    public virtual ICaptureSource CreateCaptureSource(string source, int sampleRate) => new ParecCaptureSource(source, sampleRate);

    public void Start(string source, int sampleRate, int frameMilliseconds = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            StopLocked();
            SampleRate = sampleRate;
            FrameMilliseconds = frameMilliseconds > 0 ? frameMilliseconds : DefaultFrameMilliseconds;
            Source = source ?? string.Empty;
            _frames = 0;
            _silenceFrames = 0;
            _capturedFrames = 0;
            _lastError = null;
            _clock.Restart();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _pump = Task.Factory.StartNew(() => PumpLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        try
        {
            _cts?.Cancel();
            _pump?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 停止过程中的取消异常无需上报
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _pump = null;
            _clock.Stop();
        }
    }

    /// <summary>
    /// 核心节拍循环。采集与投递彻底解耦：
    /// - 生产者线程（ProduceLoop）只管把 parec 的 PCM 灌进环形缓冲；
    /// - 本循环严格按实时时钟每 FrameMilliseconds 投递一帧：环形缓冲有数据就投真实数据，
    ///   没有就投静音帧。这样即使 monitor 源停止产出数据（Linux 静音时会发生），
    ///   下游仍按 100 帧/秒的节拍持续收到数据，不会出现 Android 端 AudioTrack 欠载而"一段时间后静音"。
    /// </summary>
    private void PumpLoop(CancellationToken token)
    {
        var frameBytes = FrameBytes;
        var silence = new byte[frameBytes];
        var ring = new RingBuffer(Math.Max(frameBytes * RingCapacityFrames, 65536));
        ICaptureSource? source = null;
        var producing = true;
        var producer = Task.Run(() =>
        {
            while (producing && !token.IsCancellationRequested)
            {
                if (source == null)
                {
                    try
                    {
                        source = CreateCaptureSource(Source, SampleRate);
                        source.Start();
                        _lastError = null;
                        Logger.Info($"audio capture started: source={DisplaySource} rate={SampleRate} frame={frameBytes}B");
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                        Logger.Error("audio capture start failed: " + ex.Message);
                        Thread.Sleep(1000);
                        continue;
                    }
                }
                try
                {
                    var read = source.Read(ring, timeoutMs: 200);
                    if (read > 0) continue;
                    Thread.Sleep(2);
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    Logger.Warning($"audio capture read failed, restarting: {ex.Message}");
                    try
                    {
                        source.Dispose();
                    }
                    catch
                    {
                        // 忽略关闭异常
                    }
                    source = null;
                    Thread.Sleep(500);
                }
            }
            try
            {
                source?.Dispose();
            }
            catch
            {
                // 忽略关闭异常
            }
            Logger.Info("audio capture stopped");
        }, token);

        var frame = new byte[frameBytes];
        var clock = Stopwatch.StartNew();
        FramePacer? pacer = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = clock.ElapsedMilliseconds;
                if (pacer == null)
                {
                    // 预缓冲：攒够 PrebufferFrames 再开始按节拍投递（采集源静默时超时后照常补静音）
                    if (ring.Length < frameBytes * PrebufferFrames && now < PrebufferTimeoutMs)
                    {
                        Thread.Sleep(5);
                        continue;
                    }
                    pacer = new FramePacer(FrameMilliseconds);
                }

                if (pacer.IsDue(now))
                {
                    if (ring.Length > frameBytes * MaxBufferedFrames)
                    {
                        var dropped = ring.DiscardOldest(ring.Length - frameBytes * PrebufferFrames);
                        Logger.Warning($"latency trim: dropped {dropped}B (buffer above {MaxBufferedFrames} frames)");
                    }
                    if (ring.Take(frame, frameBytes))
                    {
                        Interlocked.Increment(ref _capturedFrames);
                        if (_capturedFrames <= 2)
                        {
                            Logger.Info($"pump first frame: len={frameBytes} ring={ring.Length} head={BitConverter.ToString(frame, 0, 16)}");
                        }
                        Emit(frame, false);
                    }
                    else
                    {
                        Interlocked.Increment(ref _silenceFrames);
                        Emit(silence, true);
                    }
                }

                var wait = pacer.NextDue - clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)Math.Min(wait, 5));
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Logger.Error("audio pump failed: " + ex.Message);
        }
        finally
        {
            producing = false;
            try
            {
                producer.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // 忽略生产者退出异常
            }
        }
    }

    private string DisplaySource => string.IsNullOrWhiteSpace(Source) ? "@DEFAULT_MONITOR@" : Source;

    private void Emit(byte[] frame, bool isSilence)
    {
        Interlocked.Increment(ref _frames);
        Frame?.Invoke(this, new PcmFrameEventArgs(frame, isSilence));
    }

    public AudioStats Stats()
    {
        lock (_gate)
        {
            var seconds = _clock.Elapsed.TotalSeconds;
            return new AudioStats(
                Interlocked.Read(ref _frames),
                Interlocked.Read(ref _silenceFrames),
                Interlocked.Read(ref _capturedFrames),
                Interlocked.Read(ref _frames) * FrameBytes,
                seconds,
                SampleRate,
                DisplaySource,
                _lastError,
                FrameMilliseconds);
        }
    }

    /// <summary>采集监控源状态（用于 CLI 自检报告）。</summary>
    public static async Task<string> QuerySourceStateAsync(string source, CancellationToken token = default)
    {
        var name = string.IsNullOrWhiteSpace(source) ? "@DEFAULT_MONITOR@" : source;
        var result = await ProcessRunner.RunAsync("pactl", TimeSpan.FromSeconds(5), "list", "short", "sources").ConfigureAwait(false);
        if (!result.Success) return "pactl unavailable";
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            if (line.Contains(name.Replace("@DEFAULT_MONITOR@", ".monitor"), StringComparison.Ordinal))
            {
                return line.Trim();
            }
        }
        return "(not found in sources)";
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>
/// 按实时时钟决定"此刻是否该投递一帧"。每次调用最多放行一帧；若已落后超过一帧，则重新锚定到当前时刻，
/// 避免把积压的节拍一次性补发出去（连续帧会被设备判为 busy 而丢弃）。
/// </summary>
public sealed class FramePacer(int frameMilliseconds)
{
    private long _nextDue;
    private bool _started;

    public long NextDue => _nextDue;

    public bool IsDue(long nowMilliseconds)
    {
        if (!_started)
        {
            _started = true;
            _nextDue = nowMilliseconds;
        }
        if (nowMilliseconds < _nextDue) return false;
        _nextDue += frameMilliseconds;
        if (_nextDue <= nowMilliseconds) _nextDue = nowMilliseconds + frameMilliseconds;
        return true;
    }
}

/// <summary>
/// 单生产者/单消费者环形缓冲（字节流）。生产者写入 parec 的 PCM，消费者按帧节拍取出。
/// 溢出时丢弃最老数据（丢帧不阻塞读取），保证节拍不被 IO 拖住。
/// </summary>
public sealed class RingBuffer
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private int _head;
    private int _length;
    private long _overflowBytes;

    public RingBuffer(int capacity) => _buffer = new byte[Math.Max(capacity, 65536)];

    public long OverflowBytes => Interlocked.Read(ref _overflowBytes);


    public int Length
    {
        get
        {
            lock (_gate) return _length;
        }
    }

    /// <summary>写入（线性段），返回写入字节数。</summary>
    public int Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var space = _buffer.Length - _length;
            var count = Math.Min(space, data.Length);
            if (count < data.Length)
            {
                Interlocked.Add(ref _overflowBytes, data.Length - count);
                // 丢弃最老数据，保留最新 count 字节
                var drop = data.Length - count;
                _head = (_head + drop) % _buffer.Length;
                _length -= drop;
            }
            var tail = (_head + _length) % _buffer.Length;
            var first = Math.Min(count, _buffer.Length - tail);
            data[..first].CopyTo(_buffer.AsSpan(tail, first));
            if (count > first)
            {
                data.Slice(first, count - first).CopyTo(_buffer.AsSpan(0, count - first));
            }
            _length += count;
            return count;
        }
    }

    /// <summary>丢弃最旧的 count 字节（用于把延迟拉回预缓冲水位）；返回实际丢弃的字节数。</summary>
    public int DiscardOldest(int count)
    {
        lock (_gate)
        {
            var drop = Math.Clamp(count, 0, _length);
            _head = (_head + drop) % _buffer.Length;
            _length -= drop;
            return drop;
        }
    }

    /// <summary>取出恰好 count 字节（不足则返回 false 且不消费）。</summary>
    public bool Take(Span<byte> destination, int count)
    {
        lock (_gate)
        {
            if (_length < count) return false;
            var first = Math.Min(count, _buffer.Length - _head);
            _buffer.AsSpan(_head, first).CopyTo(destination);
            if (count > first)
            {
                _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);
            }
            _head = (_head + count) % _buffer.Length;
            _length -= count;
            return true;
        }
    }
}

/// <summary>可替换的采集源，便于在测试中注入合成的 PCM。</summary>
public interface ICaptureSource : IDisposable
{
    void Start();

    /// <summary>把新采集到的字节写入环形缓冲；返回 0 表示当前没有数据（超时）。</summary>
    int Read(RingBuffer ring, int timeoutMs);
}

/// <summary>
/// parec 采集源。命令： parec --device=&lt;monitor&gt; --format=s16le --rate=&lt;rate&gt; --channels=2 --raw
/// 选择 parec 而非 P/Invoke：无需额外原生依赖，且采样率转换由 PulseAudio/PipeWire 服务端完成。
/// </summary>
public sealed class ParecCaptureSource(string source, int sampleRate) : ICaptureSource
{
    private Process? _process;
    private Stream? _stdout;

    public string Arguments { get; } = BuildArguments(source, sampleRate);

    public static string BuildArguments(string source, int sampleRate)
    {
        var device = string.IsNullOrWhiteSpace(source) ? "@DEFAULT_MONITOR@" : source;
        return $"--device={device} --format=s16le --rate={sampleRate} --channels=2 --raw --latency-msec=20";
    }

    public void Start()
    {
        var parec = ProcessRunner.Which("parec") ?? throw new FileNotFoundException("parec not found (install pulseaudio-utils or pipewire-bin)");
        var psi = new ProcessStartInfo
        {
            FileName = parec,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var part in Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(part);
        var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("cannot start parec");
        _process = process;
        _stdout = process.StandardOutput.BaseStream;
        _ = Task.Run(async () =>
        {
            try
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error)) Logger.Warning("parec: " + error.Trim());
            }
            catch
            {
                // 忽略 stderr 读取失败
            }
        });
    }

    public int Read(RingBuffer ring, int timeoutMs)
    {
        var stream = _stdout;
        if (stream == null) return 0;
        return stream.ReadWithTimeout(ring, timeoutMs);
    }
    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1000);
            }
        }
        catch
        {
            // 忽略终止异常
        }
        _process?.Dispose();
        _process = null;
        _stdout = null;
    }
}
