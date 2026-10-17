using System.Diagnostics;
using AudioShare;

namespace AudioShare.Cli;

/// <summary>
/// 回归测试：本轮修复的行为（无线调试误判为 USB、采样率切换后重新握手、发送与采集节拍防突发、临时配置隔离）。
/// 全部在进程内完成：不接真机、不播放声音；配置写入临时目录，不触碰用户的真实配置。
/// </summary>
public static class RegressionTests
{
    private const int SampleRate = 48000;

    public static int RunAll()
    {
        var failures = 0;
        failures += TestHarness.Run("帧节拍器：每次最多放行一帧，落后时重新锚定（防突发）", FramePacerNoBurst);
        failures += TestHarness.Run("环形缓冲：丢弃最旧数据后仍保持先进先出", RingDiscardOldest);
        failures += TestHarness.Run("帧长一致性：AudioEngine.FrameBytes 随帧长变化", FrameSizeFollowsFrameMs);
        failures += TestHarness.Run("ADB 连接方式识别：无线调试（ip:port）不计入 USB 列表", AdbTransportSplit);
        failures += TestHarness.Run("无线调试设备：登记到 Wi-Fi 列表（ip:8088），不进入 USB 列表", RegisterWirelessEntry);
        failures += TestHarness.Run("USB 模式下发现的 Wi-Fi 设备只登记、不显示在 USB 列表", UsbModeHidesDiscoveredIpSpeakers);
        failures += TestHarness.Run("采集引擎：预缓冲后按节拍投递，采集停顿时补静音且不突发", EngineBurstAndPrebuffer);
        failures += TestHarness.Run("发送节拍：突发入队的音频帧仍按一帧间隔写出（防设备端 busy 丢帧）", SpeakerPacesWrites);
        failures += TestHarness.Run("采样率切换：已连接设备重新握手，握手下发新采样率", SampleRateChangeReHandshakes);
        return failures;
    }

    private static string TempSettings(string tag)
        => Path.Combine(Path.GetTempPath(), $"audioshare-regress-{tag}-{Guid.NewGuid():N}.json");

    private static void FramePacerNoBurst()
    {
        var pacer = new FramePacer(100);
        TestHarness.Assert(pacer.IsDue(0), "t=0 首帧到期");
        TestHarness.Assert(!pacer.IsDue(50), "t=50 未到期");
        TestHarness.Assert(pacer.IsDue(100), "t=100 第二帧到期");
        TestHarness.Assert(pacer.IsDue(350), "t=350 已落后两帧，仍只放行一帧");
        TestHarness.Assert(!pacer.IsDue(350), "同一时刻不再放行（无突发）");
        TestHarness.Assert(!pacer.IsDue(449), "重新锚定后 t=449 未到期");
        TestHarness.Assert(pacer.IsDue(450), "t=450 到期，间隔严格为一帧");
    }

    private static void RingDiscardOldest()
    {
        var ring = new RingBuffer(65536);
        ring.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        TestHarness.AssertEqual(4, ring.DiscardOldest(4), "丢弃最旧 4 字节");
        TestHarness.AssertEqual(6, ring.Length, "剩余 6 字节");
        var buffer = new byte[6];
        TestHarness.Assert(ring.Take(buffer, 6), "取出 6 字节");
        TestHarness.Assert(buffer.AsSpan().SequenceEqual(new byte[] { 5, 6, 7, 8, 9, 10 }), "剩余数据顺序正确");
    }

    private static void FrameSizeFollowsFrameMs()
    {
        using var engine = new SilentEngine();
        engine.Start(string.Empty, SampleRate, 50);
        TestHarness.AssertEqual(9600, engine.FrameBytes, "50ms@48kHz 立体声 = 9600 字节");
        engine.Stop();
        engine.Start(string.Empty, SampleRate, 100);
        TestHarness.AssertEqual(19200, engine.FrameBytes, "100ms@48kHz 立体声 = 19200 字节");
        engine.Stop();
    }

    private static void AdbTransportSplit()
    {
        const string wirelessOnly = "List of devices attached\n192.168.8.4:5555       device product:rk322x_echo model:rk322x_box device:rk322x_echo transport_id:2\n\n";
        var devices = AdbController.ParseDevices(wirelessOnly);
        TestHarness.AssertEqual(1, devices.Count, "解析出 1 台设备");
        TestHarness.Assert(devices[0].IsWireless, "192.168.8.4:5555 是无线调试设备");
        TestHarness.AssertEqual("rk322x_box", devices[0].Model, "型号字段解析");
        var (usbOnly, wirelessList) = AdbController.SplitByTransport(devices);
        TestHarness.AssertEqual(0, usbOnly.Count, "USB 列表为空（无线调试不计入）");
        TestHarness.AssertEqual(1, wirelessList.Count, "无线列表包含该设备");

        const string mixed = "List of devices attached\nR58M123ABC\tdevice usb:1-2 product:a model:Phone device:a transport_id:4\n192.168.8.4:5555 device product:b model:box device:b transport_id:2\n";
        var (usb, wireless) = AdbController.SplitByTransport(AdbController.ParseDevices(mixed));
        TestHarness.AssertEqual("R58M123ABC", usb.Single().Serial, "USB 设备（制表符分隔行）保留序列号");
        TestHarness.AssertEqual("Phone", usb.Single().Model, "USB 设备型号解析");
        TestHarness.AssertEqual("192.168.8.4:5555", wireless.Single().Serial, "无线设备序列号保留");
    }

    private static void RegisterWirelessEntry()
    {
        var settings = new SettingsStore { IsUSB = true, FilePath = TempSettings("wireless") };
        using var pool = new SilentDevicePool(settings);
        var wireless = AdbController.SplitByTransport(AdbController.ParseDevices(
            "List of devices attached\n192.168.8.4:5555 device product:x model:box device:x transport_id:2\n")).Wireless;
        pool.RegisterWirelessAdbDevices(wireless);
        TestHarness.Assert(pool.Settings.IPDevices.Any(d => d.Id == "192.168.8.4:8088" && d.Channel == AudioChannel.None),
            "Wi-Fi 列表新增 192.168.8.4:8088（未选声道，需用户确认后连接）");
        TestHarness.AssertEqual(0, pool.Speakers.Count, "USB 列表（Speakers）未被无线设备污染");
        TestHarness.AssertEqual("192.168.8.4:5555", pool.WirelessAdbSerials.Single(), "状态栏可读取无线调试序列号");
        pool.RegisterWirelessAdbDevices(wireless);
        TestHarness.AssertEqual(1, pool.Settings.IPDevices.Count(d => d.Id == "192.168.8.4:8088"), "重复扫描不会重复登记");
    }

    private static void UsbModeHidesDiscoveredIpSpeakers()
    {
        var settings = new SettingsStore { IsUSB = true, FilePath = TempSettings("usb-discover") };
        using var pool = new SilentDevicePool(settings);
        pool.AddIPSpeaker("192.168.8.9", 8088, 8080, persist: true, connect: false);
        TestHarness.AssertEqual(0, pool.Speakers.Count, "USB 模式下 Speakers（USB 列表）为空");
        TestHarness.Assert(pool.Settings.IPDevices.Any(d => d.Id == "192.168.8.9:8088"), "Wi-Fi 设备已登记到设置，切到 Wi-Fi 模式即可见");
    }

    private static void EngineBurstAndPrebuffer()
    {
        using var engine = new FixedSourceEngine(new StutterSource(SampleRate, stallFromMs: 1000, stallMs: 450));
        var emitted = new List<(long Timestamp, bool Silence)>();
        engine.Frame += (_, frame) =>
        {
            lock (emitted) emitted.Add((Stopwatch.GetTimestamp(), frame.IsSilence));
        };
        engine.Start(string.Empty, SampleRate);
        Thread.Sleep(2600);
        engine.Stop();

        (long Timestamp, bool Silence)[] frames;
        lock (emitted) frames = [.. emitted];
        var gaps = frames.Zip(frames.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a.Timestamp, b.Timestamp).TotalMilliseconds).ToArray();
        var silence = frames.Count(f => f.Silence);
        TestHarness.Assert(frames.Length >= 20, $"按实时速率投递（{frames.Length} 帧 / 2.6s）");
        TestHarness.Evidence($"相邻投递间隔(ms) 最小 {gaps.Min():0}、最大 {gaps.Max():0}；补静音 {silence} 帧");
        TestHarness.Assert(gaps.All(g => g >= 90), "相邻投递间隔均 ≥ 90ms（无突发）");
        TestHarness.Assert(silence <= 5, $"采集停顿 450ms 期间补静音不超过停顿时长对应的帧数 + 1（实际 {silence}）");
    }

    private static void SpeakerPacesWrites()
    {
        var server = new TestTcpServer("pacing");
        try
        {
            var frameBytes = Protocol.FrameBytes(SampleRate);
            var stamps = new List<long>();
            var speaker = new Speaker($"127.0.0.1:{server.Port}", AudioChannel.Stereo) { SampleRate = SampleRate };
            speaker.OnWireWrite = (data, tag) =>
            {
                if (tag == 3 && data.Length == 4 + frameBytes)
                {
                    lock (stamps) stamps.Add(Stopwatch.GetTimestamp());
                }
            };
            try
            {
                TestHarness.Assert(speaker.ConnectAsync().GetAwaiter().GetResult(), "设备连接成功");
                var frame = new byte[frameBytes];
                for (var i = 0; i < 6; i++) speaker.OnPcmFrame(frame);
                Thread.Sleep(1200);
                long[] written;
                lock (stamps) written = [.. stamps];
                var gaps = written.Zip(written.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).ToArray();
                TestHarness.Assert(written.Length >= 2, $"至少写出 2 帧（实际 {written.Length}）");
                TestHarness.Evidence($"相邻帧间隔(ms) = {string.Join(", ", gaps.Select(g => g.ToString("0")))}；丢帧 {speaker.FramesDropped}");
                TestHarness.Assert(gaps.All(g => g >= 90), "相邻音频帧间隔 ≥ 90ms（一个帧长），未出现突发");
                TestHarness.Assert(speaker.FramesDropped >= 1, "队列有界：超出容量时丢弃最旧帧，延迟不会累积");
            }
            finally
            {
                speaker.Dispose();
            }
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void SampleRateChangeReHandshakes()
    {
        var server = new TestTcpServer("rate");
        var settings = new SettingsStore { IsUSB = false, SampleRate = SampleRate, FilePath = TempSettings("rate") };
        var pool = new SilentDevicePool(settings);
        try
        {
            var speaker = new Speaker($"127.0.0.1:{server.Port}", AudioChannel.Stereo);
            pool.Speakers.Add(speaker);
            TestHarness.Assert(pool.ConnectAsync(speaker).GetAwaiter().GetResult(), "初次连接成功");
            TestHarness.AssertEqual(SampleRate, server.HandshakeSampleRate, "初次握手采样率 = 48000");

            pool.SampleRate = 44100;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && (server.HandshakeSampleRate != 44100 || !speaker.Connected || pool.Engine.SampleRate != 44100))
            {
                Thread.Sleep(50);
            }
            TestHarness.AssertEqual(44100, server.HandshakeSampleRate, "切换后设备收到 44100 的新握手（否则会按旧采样率播放）");
            TestHarness.Assert(speaker.Connected, "切换后设备保持连接");
            TestHarness.AssertEqual(44100, pool.Engine.SampleRate, "采集引擎已切换到 44100");
        }
        finally
        {
            pool.Dispose();
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class SilentSource : ICaptureSource
    {
        public void Start() { }

        public int Read(RingBuffer ring, int timeoutMs)
        {
            Thread.Sleep(Math.Min(timeoutMs, 5));
            return 0;
        }

        public void Dispose() { }
    }

    private sealed class SilentEngine : AudioEngine
    {
        public override ICaptureSource CreateCaptureSource(string source, int sampleRate) => new SilentSource();
    }

    private sealed class SilentDevicePool(SettingsStore settings) : DevicePool(settings, new AdbController())
    {
        protected override AudioEngine CreateEngine() => new SilentEngine();
    }

    private sealed class FixedSourceEngine(ICaptureSource capture) : AudioEngine
    {
        public override ICaptureSource CreateCaptureSource(string source, int sampleRate) => capture;
    }

    /// <summary>按实时速率产出音频；stallFromMs 起停顿 stallMs，停顿结束后一次性交付积压数据（与 PipeWire 突发交付相似）。</summary>
    private sealed class StutterSource(int sampleRate, int stallFromMs, int stallMs) : ICaptureSource
    {
        private readonly Stopwatch _clock = new();
        private long _deliveredBytes;

        public void Start() => _clock.Start();

        public int Read(RingBuffer ring, int timeoutMs)
        {
            var elapsed = _clock.Elapsed.TotalMilliseconds;
            if (elapsed >= stallFromMs && elapsed < stallFromMs + stallMs)
            {
                Thread.Sleep(Math.Min(timeoutMs, 5));
                return 0;
            }
            var due = (long)(elapsed * sampleRate / 1000.0) * 4;
            var available = due - _deliveredBytes;
            if (available < 1920)
            {
                Thread.Sleep(2);
                return 0;
            }
            var chunk = new byte[(int)available];
            Array.Fill(chunk, (byte)0x40);
            ring.Write(chunk);
            _deliveredBytes += chunk.Length;
            return chunk.Length;
        }

        public void Dispose() { }
    }
}
