using System.Net;
using AudioShare;

namespace AudioShare.Cli;

/// <summary>
/// 验收自检：协议字节级一致性、声道抽取、静音保活、多设备分发、发现与网卡过滤。
/// 断言全部基于真实进程内字节流（TestTcpServer 按 android TcpService.java 语义实现）。
/// </summary>
public static class SelfTests
{
    private const int SampleRate = 48000;
    private const double LeftTone = 1000;
    private const double RightTone = 2000;

    /// <summary>按帧节拍产出 1kHz/2kHz 立体声正弦的合成采集源（真实 PCM 时序）。</summary>
    private sealed class SyntheticCaptureSource(int sampleRate) : ICaptureSource
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private long _emittedBytes;

        public int Reads { get; private set; }

        public double Peak { get; private set; }

        public void Start() { }

        public int Read(RingBuffer ring, int timeoutMs)
        {
            var elapsedBytes = _clock.Elapsed.TotalSeconds * sampleRate * 4;
            var available = (long)elapsedBytes - _emittedBytes;
            if (available < 4096)
            {
                Thread.Sleep(Math.Min(timeoutMs, 5));
                return 0;
            }
            // 按整帧产出（默认帧长），保证每帧含整数个正弦周期，频谱分析无泄漏
            var frameBytes = Protocol.FrameBytes(sampleRate);
            var blocks = (int)Math.Min(available / frameBytes, 8);
            if (blocks <= 0)
            {
                Thread.Sleep(Math.Min(timeoutMs, 5));
                return 0;
            }
            var chunk = new byte[blocks * frameBytes];
            var frames = chunk.Length / 4;
            for (var i = 0; i < frames; i++)
            {
                var index = _emittedBytes / 4 + i;
                var t = index / (double)sampleRate;
                var left = (short)(Math.Sin(2 * Math.PI * LeftTone * t) * 0.5 * short.MaxValue);
                var right = (short)(Math.Sin(2 * Math.PI * RightTone * t) * 0.5 * short.MaxValue);
                BitConverter.TryWriteBytes(chunk.AsSpan(i * 4, 2), left);
                BitConverter.TryWriteBytes(chunk.AsSpan(i * 4 + 2, 2), right);
            }
            ring.Write(chunk);
            _emittedBytes += chunk.Length;
            Reads++;
            Peak = Math.Max(Peak, ToneGenerator.Peak(ToneGenerator.ToSamples(chunk, 0, 2)));
            return chunk.Length;
        }

        public void Dispose() { }
    }

    /// <summary>故意完全静默的采集源：用于真实复现"Linux monitor 在系统静音时停止产出数据"。</summary>
    private sealed class StallingCaptureSource : ICaptureSource
    {
        public int Reads { get; private set; }

        public void Start() { }

        public int Read(RingBuffer ring, int timeoutMs)
        {
            Reads++;
            Thread.Sleep(timeoutMs);
            return 0;
        }

        public void Dispose() { }
    }

    private sealed class SyntheticAudioEngine : AudioEngine
    {
        public int Created { get; private set; }

        public List<SyntheticCaptureSource> Sources { get; } = [];

        public override ICaptureSource CreateCaptureSource(string source, int sampleRate)
        {
            Created++;
            var created = new SyntheticCaptureSource(sampleRate);
            Sources.Add(created);
            return created;
        }
    }

    private sealed class StallingAudioEngine(StallingCaptureSource capture) : AudioEngine
    {
        public override ICaptureSource CreateCaptureSource(string source, int sampleRate) => capture;
    }

    private static Speaker NewSpeaker(int port, AudioChannel channel)
        => new($"127.0.0.1:{port}", channel) { SampleRate = SampleRate };

    public static int ProtocolConstants()
        => TestHarness.Run("协议常量与编码（对照 windows/Speaker.cs 与 android/TcpService.java）", () =>
        {
            TestHarness.AssertEqual(20, Protocol.HeadBytes.Length, "头部字节长度（\"picapico-audio-share\" = 20 字节）");
            TestHarness.AssertEqual(Protocol.Head, System.Text.Encoding.ASCII.GetString(Protocol.HeadBytes), "头部字面量");
            TestHarness.AssertEqual(29, Protocol.HeadBytes.Length + 1 + 4 + 4, "head+command+rate+mask 握手字节数");
            TestHarness.AssertEqual(12, Protocol.ChannelMask(AudioChannel.Stereo), "Stereo -> channelMask");
            TestHarness.AssertEqual(4, Protocol.ChannelMask(AudioChannel.Left), "Left -> channelMask（非 Stereo 恒为 4）");
            TestHarness.AssertEqual(4, Protocol.ChannelMask(AudioChannel.Right), "Right -> channelMask（非 Stereo 恒为 4）");
            TestHarness.AssertEqual(4, Protocol.ChannelMask(AudioChannel.None), "None -> channelMask");
            TestHarness.AssertEqual(0, (int)AudioChannel.None, "枚举 None");
            TestHarness.AssertEqual(8, (int)AudioChannel.Right, "枚举 Right");
            TestHarness.AssertEqual(SampleRate / 1000 * AudioEngine.DefaultFrameMilliseconds * 4, Protocol.FrameBytes(SampleRate), $"48kHz {AudioEngine.DefaultFrameMilliseconds}ms 立体声帧字节数");
            foreach (var rate in new[] { 8000, 16000, 44100, 48000, 96000, 176400, 192000 })
            {
                TestHarness.Assert(Protocol.FrameBytes(rate) % 4 == 0, $"{rate}Hz 帧长 4 字节对齐");
            }
            var encoded = Protocol.EncodeFrame([0x01, 0x02, 0x03, 0x04]);
            TestHarness.AssertEqual(8, encoded.Length, "帧编码长度");
            TestHarness.AssertEqual(4, BitConverter.ToInt32(encoded, 0), "帧编码长度前缀（小端）");
            TestHarness.AssertEqual(4, Protocol.HeartbeatBytes.Length, "心跳帧长度（4 个 0 字节）");
            TestHarness.AssertEqual(0, BitConverter.ToInt32(Protocol.HeartbeatBytes, 0), "心跳帧长度字段 = 0");
        });

    public static int ChannelSplit()
        => TestHarness.Run("声道抽取算法（对照 windows/AudioManager.cs:116-132）", () =>
        {
            var pcm = ToneGenerator.Stereo(LeftTone, RightTone, SampleRate, 40);
            var left = ChannelSplitter.ForChannel(pcm, AudioChannel.Left);
            var right = ChannelSplitter.ForChannel(pcm, AudioChannel.Right);
            var stereo = ChannelSplitter.ForChannel(pcm, AudioChannel.Stereo);

            TestHarness.AssertEqual(pcm.Length, stereo.Length, "Stereo 输出与输入等长");
            TestHarness.AssertEqual(pcm.Length / 2, left.Length, "Left 输出为输入一半");
            TestHarness.AssertEqual(pcm.Length / 2, right.Length, "Right 输出为输入一半");

            var leftSamples = ToneGenerator.ToSamples(left, channel: 0, channels: 1);
            var rightSamples = ToneGenerator.ToSamples(right, channel: 0, channels: 1);
            var left1k = ToneGenerator.GoertzelAmplitude(leftSamples, LeftTone, SampleRate);
            var left2k = ToneGenerator.GoertzelAmplitude(leftSamples, RightTone, SampleRate);
            var right1k = ToneGenerator.GoertzelAmplitude(rightSamples, LeftTone, SampleRate);
            var right2k = ToneGenerator.GoertzelAmplitude(rightSamples, RightTone, SampleRate);

            TestHarness.Evidence($"Goertzel 幅度 L: 1k={left1k:0} 2k={left2k:0} / R: 1k={right1k:0} 2k={right2k:0}");
            TestHarness.Assert(left1k > left2k * 20, "Left 通道只含 1kHz");
            TestHarness.Assert(right2k > right1k * 20, "Right 通道只含 2kHz");
            TestHarness.AssertNear(0.5 * short.MaxValue / Math.Sqrt(2), ToneGenerator.Rms(leftSamples), 300, "抽取后 RMS 与幅度一致");

            // 逐字节比对：抽取结果必须等于原始交错流中的对应样本
            var referenceLeft = new byte[pcm.Length / 2];
            var referenceRight = new byte[pcm.Length / 2];
            ChannelSplitter.Split(pcm, referenceLeft, referenceRight);
            TestHarness.Assert(left.AsSpan().SequenceEqual(referenceLeft), "Left 逐字节等于 Split 结果（每 4 字节取前 2）");
            TestHarness.Assert(right.AsSpan().SequenceEqual(referenceRight), "Right 逐字节等于 Split 结果（每 4 字节取后 2）");
        });

    public static int AddressParsing()
        => TestHarness.Run("设备地址解析（对照 windows/Speaker.cs:193-208）", () =>
        {
            var cases = new (string Id, string Ip, int Port)[]
            {
                ("192.168.1.10:8088", "192.168.1.10", 8088),
                ("192.168.1.10", "192.168.1.10", 80),
                ("10.0.0.2:1234", "10.0.0.2", 1234),
                ("[fe80::1]:8088", "fe80::1", 8088),
            };
            foreach (var (id, ip, port) in cases)
            {
                var speaker = new Speaker(id);
                TestHarness.Assert(speaker.TryParseAddress(out var parsedIp, out var parsedPort), $"解析成功: {id}");
                TestHarness.AssertEqual(ip, parsedIp, $"{id} -> ip");
                TestHarness.AssertEqual(port, parsedPort, $"{id} -> port");
            }
            TestHarness.Assert(!new Speaker("not-an-address").TryParseAddress(out _, out _), "非法地址被拒绝");
        });

    public static int Discovery()
        => TestHarness.Run("UDP 发现与网卡过滤（伪造 android 广播驱动设备入列）", () =>
        {
            var tempSettings = SettingsStore.Load(Path.Combine(Path.GetTempPath(), $"audioshare-discovery-{Guid.NewGuid():N}.json"));
            tempSettings.IsUSB = false;
            tempSettings.IPDevices = [];
            var pool = new DevicePool(tempSettings, new AdbController());
            try
            {
                var settings = pool.Settings;
                pool.Start();
                var discovery = pool.Discovery ?? throw new InvalidOperationException("discovery not started");
                TestHarness.AssertEqual(10, discovery.BoundPorts.Count, "绑定 58261-58270 全部端口");
                TestHarness.Evidence($"bound ports: {string.Join(',', discovery.BoundPorts)}");

                var discovered = new List<DiscoveredDevice>();
                pool.DeviceDiscovered += (_, device) => discovered.Add(device);

                // Android 端真实报文： "picapico-audio-share@<tcpPort>@<httpPort>"（30 字节）
                var payload = UdpDiscovery.BuildBroadcastPayload(8088, 8080);
                TestHarness.Evidence($"@8088/@8080 报文长度 = {payload.Length}，内容 = {System.Text.Encoding.ASCII.GetString(payload)}");
                TestHarness.AssertEqual(Protocol.Head.Length + 1 + 4 + 1 + 4, payload.Length, "报文长度 = HEAD + '@tcp' + '@http'");
                TestHarness.Assert(payload.Length <= Protocol.DiscoveryMaxMessageLength, $"报文在阈值 {Protocol.DiscoveryMaxMessageLength} 内（原版 windows 阈值为 26，会全部丢弃）");

                UdpDiscovery.Send(payload, Protocol.DiscoveryPortFirst);
                UdpDiscovery.Send(payload, Protocol.DiscoveryPortLast);
                Thread.Sleep(600);

                TestHarness.Assert(discovered.Count >= 2, $"收到两条广播（实际 {discovered.Count}）");
                TestHarness.AssertEqual("127.0.0.1:8088", discovered[0].Id, "发现设备 Id");
                TestHarness.AssertEqual(8080, discovered[0].HttpPort, "发现设备 http 端口");
                TestHarness.AssertEqual("http://127.0.0.1:8080", discovered[0].HttpUrl, "远程管理页地址");
                TestHarness.Assert(pool.Speakers.Any(s => s.Id == "127.0.0.1:8088"), "发现设备已加入设备池");
                TestHarness.Assert(pool.Settings.IPDevices.Any(d => d.Id == "127.0.0.1:8088"), "发现设备已持久化到 IP 列表");

                // 反例：非法前缀与非法端口必须被丢弃
                UdpDiscovery.Send(System.Text.Encoding.ASCII.GetBytes("not-audio-share@1234@80"), Protocol.DiscoveryPortFirst);
                UdpDiscovery.Send(System.Text.Encoding.ASCII.GetBytes($"{Protocol.BroadcastPrefix}@70000@80"), Protocol.DiscoveryPortFirst);
                UdpDiscovery.Send(new byte[40], Protocol.DiscoveryPortFirst);
                Thread.Sleep(500);
                TestHarness.AssertEqual(2, discovered.Count, "非法/超长报文被丢弃（发现数不变）");
                TestHarness.Evidence($"accepted={discovery.Accepted} dropped={discovery.Dropped} messages={discovery.Messages}");

                // 网卡过滤：虚拟网卡必须全部拒绝，物理网卡接受
                foreach (var virtualName in new[] { "lo", "docker0", "veth1a2b3c", "br-9f8e7d", "tun0", "tailscale0", "wg0", "virbr0", "dummy0" })
                {
                    TestHarness.Assert(!NetworkInterfaceFilter.Accepts(virtualName), $"拒绝虚拟网卡 {virtualName}");
                }
                foreach (var physicalName in new[] { "enp5s0", "eth0", "wlan0", "eno1" })
                {
                    TestHarness.Assert(NetworkInterfaceFilter.Accepts(physicalName), $"接受物理网卡 {physicalName}");
                }
                var report = NetworkInterfaceFilter.ReportAsync().GetAwaiter().GetResult();
                foreach (var line in report.Split('\n')) TestHarness.Evidence(line.Trim());
                TestHarness.Assert(!report.Contains("ACCEPT lo "), "本机实际网卡报告中 lo 被拒绝");
                TestHarness.Evidence($"扫描网段：{NetworkInterfaceFilter.DescribeLocalNetworks().Replace('\n', ' ')}");
            }
            finally
            {
                pool.Dispose();
            }
        });

    /// <summary>
    /// 字节级协议回放（验收标准 2）：旁录必须挂在建连之前，因此旁录内容 = 握手 + 全部音频帧。
    /// 用独立解析器（ProtocolVerifier，按 android readHead/readInt/parseInt 语义实现）校验每个字段。
    /// </summary>
    public static int ProtocolOnWire(TestTcpServer server, Speaker speaker, List<byte> clientTap, List<string> firstWrites)
        => TestHarness.Run("真实字节流解析回放（独立解析器，对照 android readHead/readInt/parseInt）", () =>
        {
            // 先让客户端真实发送若干音频帧，才能在字节层面回放校验
            using (var engine = new AudioEngine())
            {
                engine.Frame += (_, frame) => speaker.OnPcmFrame(frame.Data);
                engine.Start(string.Empty, SampleRate);
                Thread.Sleep(900);
                engine.Stop();
            }
            Thread.Sleep(300);
            var stream = server.AudioStreamSnapshot();
            byte[] clientBytes;
            lock (clientTap) clientBytes = [.. clientTap];
            TestHarness.Evidence($"旁录={clientBytes.Length}B 服务端帧桶={stream.Length}B 会话={server.AudioSessions} ACK={server.AckBytesWritten} 已发送帧={speaker.FramesSent}");
            TestHarness.Evidence($"旁录首段(hex): {ProtocolVerifier.ToHex(clientBytes, 48)}");
            TestHarness.Evidence($"旁录首段(可读): {ProtocolVerifier.Escape(clientBytes, 48)}");
            foreach (var line in firstWrites) TestHarness.Evidence($"写入: {line}");
            TestHarness.Evidence($"写入序列总记录数={firstWrites.Count}（用于定位多余字节）");
            TestHarness.Evidence($"旁录中 \"picapico-audio-share\" 出现位置: {string.Join(',', FindHeadOffsets(clientBytes))}");
            var handshakeLength = Protocol.Head.Length + 1 + 8;
            var sentFrames = speaker.FramesSent;
            TestHarness.AssertEqual(0L, speaker.UntappedWrites, "全部写入都在旁录覆盖范围内");
            TestHarness.Assert(sentFrames >= 3, $"客户端已发送 {sentFrames} 个音频帧（帧长 {AudioEngine.DefaultFrameMilliseconds}ms）");
            TestHarness.AssertEqual(sentFrames * (4 + Protocol.FrameBytes(SampleRate)), stream.Length, $"服务端帧桶 = {sentFrames} 帧 × (4 + {Protocol.FrameBytes(SampleRate)} 字节)");
            TestHarness.AssertEqual(handshakeLength + sentFrames * (4 + Protocol.FrameBytes(SampleRate)), clientTap.Count, $"旁录 = 握手 {handshakeLength} 字节 + {sentFrames} 帧 × {4 + Protocol.FrameBytes(SampleRate)} 字节");
            TestHarness.AssertEqual(1, server.AudioSessions, "音频会话数");
            TestHarness.AssertEqual(1, FindHeadOffsets(clientBytes).Count, "旁录中头部只出现一次（无重复握手）");

            var verifier = new ProtocolVerifier();
            verifier.Feed(clientBytes);
            var (head, command, consumed) = verifier.ReadHead();
            TestHarness.AssertEqual(Protocol.Head, head, "头部字面量（客户端真实写入 20 字节）");
            TestHarness.AssertEqual((byte)Command.AudioData, command, "命令字节 = 1 (AudioData)");
            TestHarness.AssertEqual(Protocol.Head.Length + 1, consumed, "头部 + 命令字节偏移");
            var handshakeEnd = Protocol.Head.Length + 1 + 8;
            TestHarness.AssertEqual(SampleRate, verifier.ReadIntAt(Protocol.Head.Length + 1), "采样率（小端 int32）");
            TestHarness.AssertEqual(Protocol.ChannelMask(AudioChannel.Stereo), verifier.ReadIntAt(Protocol.Head.Length + 5), "声道掩码（小端 int32）");
            TestHarness.AssertEqual(SampleRate, server.HandshakeSampleRate, "服务端读到的采样率");
            TestHarness.AssertEqual(12, server.HandshakeChannelMask, "服务端读到的声道掩码");

            var frames = verifier.ReadFrames(handshakeEnd);
            TestHarness.Assert(frames.Count > 0, $"解析出音频帧 {frames.Count} 个");
            var dataFrames = frames.Where(f => f.Length > 0).ToList();
            var heartbeatFrames = frames.Count - dataFrames.Count;
            TestHarness.Evidence($"帧数={frames.Count} 数据帧={dataFrames.Count} 心跳帧={heartbeatFrames} 每帧字节={dataFrames[0].Length}");
            TestHarness.Assert(dataFrames.All(f => f.Length == Protocol.FrameBytes(SampleRate)), $"所有数据帧长度 = {Protocol.FrameBytes(SampleRate)}B（{AudioEngine.DefaultFrameMilliseconds}ms@48k 立体声）");
            TestHarness.Assert(dataFrames.All(f => f.Length % 4 == 0), "所有数据帧长度为 4 的整数倍");
            TestHarness.Evidence($"数据帧={dataFrames.Count} 心跳帧={heartbeatFrames}（心跳需 >5s 静默触发，由保活测试验证）");
            TestHarness.AssertEqual(1, server.AudioSessions, "音频会话数");
            TestHarness.Assert(server.AckBytesWritten >= 1, "服务端已回 1 字节 ACK（客户端握手读取路径生效）");
        });

    public static int ControlCommands(TestTcpServer server, Speaker speaker)
        => TestHarness.Run("控制指令 Volume/SyncTime/Stop（独立连接 + 小端参数）", () =>
        {
            TestHarness.Assert(speaker.SendControlAsync(Command.Volume, Protocol.Int32LittleEndian(37), force: false).GetAwaiter().GetResult(), "Volume 指令发送成功");
            Thread.Sleep(200);
            var control = server.ControlStreamSnapshot();
            TestHarness.Assert(control.Length >= 2, $"已捕获控制连接数据 {control.Length} 字节");
            TestHarness.Evidence($"控制流字节: {BitConverter.ToString(control)}");

            var verifier = new ProtocolVerifier();
            verifier.Feed(control);
            var stopSeen = false;
            var volumeValue = -1;
            var cursor = 0;
            while (cursor < control.Length)
            {
                var commandByte = control[cursor];
                if (commandByte == (byte)Command.Stop || commandByte == (byte)Command.SyncTime)
                {
                    stopSeen = true;
                    cursor += 1;
                    continue;
                }
                if (commandByte == (byte)Command.Volume)
                {
                    volumeValue = verifier.ReadIntAt(cursor + 1);
                    cursor += 5;
                    continue;
                }
                break;
            }
            TestHarness.Assert(stopSeen, "建连前已发送 Stop(4) 或 SyncTime(3)");
            TestHarness.AssertEqual(37, volumeValue, "Volume 参数（小端 int32 = 37）");
            TestHarness.AssertEqual(0, control.Count(b => b == (byte)Command.AudioData), "控制流中不含 AudioData 命令");
            TestHarness.Assert(server.AudioSessions == 1, "控制指令未复用音频连接（独立连接）");
        });

    public static int MultiDeviceAndChannels()
        => TestHarness.Run("多设备并发 + 独立声道分离（1kHz 左 / 2kHz 右 频谱验证）", () =>
        {
            var settings = new SettingsStore { SampleRate = SampleRate, FilePath = Path.Combine(Path.GetTempPath(), $"audioshare-test-multi-{Guid.NewGuid():N}.json") };
            var pool = new DemoDevicePool(settings);
            TestTcpServer? stereo = null;
            TestTcpServer? leftServer = null;
            TestTcpServer? rightServer = null;
            Speaker? stereoSpeaker = null;
            Speaker? leftSpeaker = null;
            Speaker? rightSpeaker = null;
            try
            {
                stereo = new TestTcpServer("stereo");
                leftServer = new TestTcpServer("left");
                rightServer = new TestTcpServer("right");
                stereoSpeaker = NewSpeaker(stereo.Port, AudioChannel.Stereo);
                leftSpeaker = NewSpeaker(leftServer.Port, AudioChannel.Left);
                rightSpeaker = NewSpeaker(rightServer.Port, AudioChannel.Right);

                TestHarness.Assert(stereoSpeaker.ConnectAsync().GetAwaiter().GetResult(), "Stereo 设备连接");
                TestHarness.Assert(leftSpeaker.ConnectAsync().GetAwaiter().GetResult(), "Left 设备连接");
                TestHarness.Assert(rightSpeaker.ConnectAsync().GetAwaiter().GetResult(), "Right 设备连接");

                pool.Register(stereoSpeaker, leftSpeaker, rightSpeaker);
                TestHarness.Evidence($"握手掩码: stereo={stereo.HandshakeChannelMask} left={leftServer.HandshakeChannelMask} right={rightServer.HandshakeChannelMask}");
                TestHarness.AssertEqual(12, stereo.HandshakeChannelMask, "Stereo 设备下发掩码 12");
                TestHarness.AssertEqual(4, leftServer.HandshakeChannelMask, "Left 设备下发掩码 4");
                TestHarness.AssertEqual(4, rightServer.HandshakeChannelMask, "Right 设备下发掩码 4");

                foreach (var server in new[] { stereo, leftServer, rightServer }) server.ResetAudioStream();
                pool.StartEngine();
                // 按帧长缩放运行时长，保证每台设备至少收到 ~12 帧
                Thread.Sleep(Math.Max(700, AudioEngine.DefaultFrameMilliseconds * 12));
                TestHarness.Assert(pool.Engine is SyntheticAudioEngine, "设备池使用合成采集源引擎（可注入测试信号）");
                foreach (var source in pool.Synthetic.Sources)
                {
                    TestHarness.Evidence($"合成源统计: Read 调用={source.Reads} 峰值={source.Peak:0}");
                }
                var engineStats = pool.Stats();
                TestHarness.Evidence($"引擎统计: 总帧={engineStats.Frames} 真实数据帧={engineStats.CapturedFrames} 静音帧={engineStats.SilenceFrames} fps={engineStats.FramesPerSecond:0.##} lastError={engineStats.LastError ?? "(none)"}");
                pool.StopEngine();

                var stereoFrames = ExtractFrames(stereo.AudioStreamSnapshot());
                var leftFrames = ExtractFrames(leftServer.AudioStreamSnapshot());
                var rightFrames = ExtractFrames(rightServer.AudioStreamSnapshot());
                TestHarness.Evidence($"帧数: stereo={stereoFrames.Count} left={leftFrames.Count} right={rightFrames.Count}");
                var minFrames = 8;
                TestHarness.Assert(stereoFrames.Count >= minFrames && leftFrames.Count >= minFrames && rightFrames.Count >= minFrames,
                    $"三台设备均持续收到数据帧（stereo={stereoFrames.Count} left={leftFrames.Count} right={rightFrames.Count}，帧长 {AudioEngine.DefaultFrameMilliseconds}ms）");

                // 只分析真实数据帧（按峰值挑选有信号的帧，避免把静音补齐帧当成参考帧）
                var stereoRef = stereoFrames.OrderByDescending(f => PeakOf(f)).First();
                var leftRef = leftFrames.OrderByDescending(f => PeakOf(f)).First();
                var rightRef = rightFrames.OrderByDescending(f => PeakOf(f)).First();
                TestHarness.Evidence($"参考帧峰值: stereo={PeakOf(stereoRef):0} left={PeakOf(leftRef):0} right={PeakOf(rightRef):0}");
                TestHarness.Evidence($"参考帧前 16 字节: stereo={ProtocolVerifier.ToHex(stereoRef, 16)} left={ProtocolVerifier.ToHex(leftRef, 16)}");
                var leftStem = ToneGenerator.ToSamples(leftRef, channel: 0, channels: 1);
                var leftOnLeft = ToneGenerator.GoertzelAmplitude(leftStem, LeftTone, SampleRate);
                var rightOnLeft = ToneGenerator.GoertzelAmplitude(leftStem, RightTone, SampleRate);

                var rightStem = ToneGenerator.ToSamples(rightRef, channel: 0, channels: 1);
                var leftOnRight = ToneGenerator.GoertzelAmplitude(rightStem, LeftTone, SampleRate);
                var rightOnRight = ToneGenerator.GoertzelAmplitude(rightStem, RightTone, SampleRate);

                var stereoLeft = ToneGenerator.ToSamples(stereoRef, channel: 0, channels: 2);
                var stereoRight = ToneGenerator.ToSamples(stereoRef, channel: 1, channels: 2);
                var stereoLeftTone = ToneGenerator.GoertzelAmplitude(stereoLeft, LeftTone, SampleRate);
                var stereoRightTone = ToneGenerator.GoertzelAmplitude(stereoRight, RightTone, SampleRate);

                TestHarness.Evidence($"Left 设备: 1k={leftOnLeft:0} 2k={rightOnLeft:0}");
                TestHarness.Evidence($"Right 设备: 1k={leftOnRight:0} 2k={rightOnRight:0}");
                TestHarness.Evidence($"Stereo 设备: L-1k={stereoLeftTone:0} R-2k={stereoRightTone:0}");

                TestHarness.Assert(leftOnLeft > rightOnLeft * 20, "Left 设备只收到左声道 1kHz");
                TestHarness.Assert(rightOnRight > leftOnRight * 20, "Right 设备只收到右声道 2kHz");
                TestHarness.Assert(stereoLeftTone > 1000 && stereoRightTone > 1000, "Stereo 设备同时收到左右声道");
                TestHarness.AssertEqual(Protocol.FrameBytes(SampleRate), stereoFrames[0].Length, $"Stereo 帧长 {Protocol.FrameBytes(SampleRate)}B");
                TestHarness.AssertEqual(Protocol.FrameBytes(SampleRate) / 2, leftFrames[0].Length, $"Left 帧长 {Protocol.FrameBytes(SampleRate) / 2}B（抽取）");
                TestHarness.AssertEqual(Protocol.FrameBytes(SampleRate) / 2, rightFrames[0].Length, $"Right 帧长 {Protocol.FrameBytes(SampleRate) / 2}B（抽取）");
                TestHarness.Assert(leftFrames.All(f => f.Length == Protocol.FrameBytes(SampleRate) / 2), $"Left 设备所有帧统一为 {Protocol.FrameBytes(SampleRate) / 2}B");
                TestHarness.Assert(rightFrames.All(f => f.Length == Protocol.FrameBytes(SampleRate) / 2), $"Right 设备所有帧统一为 {Protocol.FrameBytes(SampleRate) / 2}B");
            }
            finally
            {
                pool.Dispose();
                stereoSpeaker?.Dispose();
                leftSpeaker?.Dispose();
                rightSpeaker?.Dispose();
                if (stereo != null) stereo.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (leftServer != null) leftServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (rightServer != null) rightServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });

    /// <summary>
    /// 静音保活：用一个"永不产出数据"的采集源真实复现 Linux monitor 静默的场景，
    /// 验证引擎仍按实时速率（100 帧/秒）持续补齐静音帧、连接不断、心跳帧存在。
    /// </summary>
    public static int KeepAlive(TestTcpServer server, Speaker speaker, int seconds)
        => TestHarness.Run($"静音保活：采集源完全静默 {seconds}s，仍按实时速率投递 + 心跳（commit dac02fd）", () =>
        {
            var stalling = new StallingCaptureSource();
            using var engine = new StallingAudioEngine(stalling);
            var frames = 0;
            var silence = 0;
            var real = 0;
            engine.Frame += (_, frame) =>
            {
                Interlocked.Increment(ref frames);
                if (frame.IsSilence) Interlocked.Increment(ref silence);
                else Interlocked.Increment(ref real);
                speaker.OnPcmFrame(frame.Data);
            };
            engine.Start(string.Empty, SampleRate);
            Thread.Sleep(seconds * 1000);
            engine.Stop();

            var stats = engine.Stats();
            var expected = seconds * 1000 / AudioEngine.DefaultFrameMilliseconds;
            TestHarness.Evidence($"引擎统计: 总帧={stats.Frames} 静音帧={stats.SilenceFrames} 真实数据帧={stats.CapturedFrames} fps={stats.FramesPerSecond:0.##} 期望帧数≈{expected}");
            TestHarness.Evidence($"采集源 Read 调用={stalling.Reads} 次（全部返回 0 = 无数据）lastError={stats.LastError ?? "(none)"}");
            TestHarness.Assert(real == 0, "采集源静默期间没有任何真实数据帧");
            TestHarness.AssertInRange(expected * 0.9, expected * 1.1, stats.Frames, "总帧数按实时速率补齐（10ms 一帧）");
            TestHarness.AssertInRange(stats.ExpectedFramesPerSecond * 0.95, stats.ExpectedFramesPerSecond * 1.05, stats.FramesPerSecond, "投递速率（帧/秒）");

            Thread.Sleep(400);
            var received = ExtractFrames(server.AudioStreamSnapshot());
            var dataFrames = received.Where(f => f.Length > 0).Count();
            var heartbeats = received.Count - dataFrames;
            TestHarness.Evidence($"接收端: 数据帧={dataFrames} 心跳帧={heartbeats} 连接状态={speaker.Status} 已发送={speaker.FramesSent} 丢帧={speaker.FramesDropped}");
            TestHarness.Assert(dataFrames >= expected * 0.85, "接收端持续收到数据帧（无断连缺口）");
            // 保活有两条路径，任一成立即证明"流不静默"：
            // 1) 按实时速率补齐的静音数据帧（本场景主路径）；2) 空转 >5s 时补发的长度 0 心跳帧。
            TestHarness.Assert(dataFrames >= expected * 0.85 || heartbeats >= 1,
                $"保活成立：数据帧={dataFrames} 或 心跳帧={heartbeats}（写循环空转={speaker.IdleTicks} 次，心跳已发={speaker.HeartbeatsSent}）");
            TestHarness.Evidence($"空转检测={speaker.IdleTicks} 次，长度 0 心跳={speaker.HeartbeatsSent} 个（静默无数据帧时按 5s 周期补发）");
            TestHarness.AssertEqual(1, server.AudioSessions, "静默期间没有重建连接");
            TestHarness.Assert(speaker.Connected, "静默期间连接保持");
        });

    public static int CaptureSource()
        => TestHarness.Run("parec 采集源参数与 monitor 解析", () =>
        {
            var arguments = ParecCaptureSource.BuildArguments("@DEFAULT_MONITOR@", 48000);
            TestHarness.Evidence($"parec {arguments}");
            TestHarness.Assert(arguments.Contains("--device=@DEFAULT_MONITOR@"), "默认绑定 @DEFAULT_MONITOR@");
            TestHarness.Assert(arguments.Contains("--format=s16le"), "格式 s16le（16bit 小端）");
            TestHarness.Assert(arguments.Contains("--rate=48000"), "采样率 48000");
            TestHarness.Assert(arguments.Contains("--channels=2"), "双声道");
            TestHarness.Assert(arguments.Contains("--raw"), "raw 模式");
            TestHarness.Assert(ProcessRunner.Which("parec") != null, "系统存在 parec");
            TestHarness.Assert(ProcessRunner.Which("pactl") != null, "系统存在 pactl");

            var devices = MonitorEnumerator.EnumerateAsync().GetAwaiter().GetResult();
            TestHarness.Evidence($"枚举到 {devices.Count} 个输出设备:");
            foreach (var device in devices) TestHarness.Evidence($"  {(device.IsDefault ? "[默认] " : string.Empty)}{device.Name} -> {device.MonitorSource}");
            TestHarness.Assert(devices.Count >= 1, "至少枚举到一个输出设备");
            TestHarness.Assert(devices.All(d => !string.IsNullOrWhiteSpace(d.MonitorSource)), "每个设备都有可用 monitor 源");
        });

    public static int AdbSanity()
        => TestHarness.Run("adb 可用性与命令序列（无真机时只验证枚举与日志）", () =>
        {
            var adb = new AdbController();
            TestHarness.Assert(adb.Available, $"系统存在 adb: {adb.AdbPath}");
            var devices = adb.ListDevicesAsync().GetAwaiter().GetResult();
            TestHarness.Evidence($"adb devices -l 解析出 {devices.Count} 台设备");
            foreach (var device in devices) TestHarness.Evidence($"  {device.Serial} state={device.State} model={device.Model}");
            TestHarness.Assert(adb.Log.Count >= 1, "adb 调用已记录命令行");
            foreach (var line in adb.Log) TestHarness.Evidence(line);
            TestHarness.Assert(adb.Log.All(l => l.Contains("exit=")), "每条 adb 记录都带退出码");
            var candidates = AndroidInstaller.CandidateApkPaths();
            TestHarness.Evidence($"APK 候选路径首个: {candidates[0]}");
            TestHarness.Assert(candidates.Any(c => c.EndsWith("AudioShare.apk", StringComparison.Ordinal)), "APK 命名遵循 windows 的 exe 同名规则");
            TestHarness.AssertEqual("1.7.9", Protocol.AndroidVersionName, "版本判定字符串与 android versionName 一致");
            TestHarness.Assert(AndroidInstaller.IsVersionInstalled("versionName=1.7.9"), "版本比对逻辑命中 1.7.9");
            TestHarness.Assert(!AndroidInstaller.IsVersionInstalled("versionName=1.6.0"), "版本比对不误判旧版本");
        });

    private static List<byte[]> ExtractFrames(byte[] stream)
    {
        var verifier = new ProtocolVerifier();
        verifier.Feed(stream);
        var frames = verifier.ReadFrames(0);
        return frames.Where(f => f.Length > 0).ToList();
    }

    private static double PeakOf(byte[] frame) => ToneGenerator.Peak(ToneGenerator.ToSamples(frame));

    private static List<int> FindHeadOffsets(byte[] data)
    {
        var offsets = new List<int>();
        var head = Protocol.HeadBytes;
        for (var i = 0; i + head.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, head.Length).SequenceEqual(head)) offsets.Add(i);
        }
        return offsets;
    }

    /// <summary>把已连接设备接入合成采集引擎驱动分发。</summary>
    private sealed class DemoDevicePool : DevicePool
    {
        private readonly SettingsStore _store;
        private SyntheticAudioEngine? _synthetic;

        public DemoDevicePool(SettingsStore settings) : base(settings, new AdbController())
        {
            _store = settings;
            if (_synthetic == null) throw new InvalidOperationException("synthetic engine must be created by CreateEngine");
        }

        /// <summary>注意：基类构造函数会调用 CreateEngine，因此这里延迟创建（属性初始化器晚于基类构造，不能用）。</summary>
        public SyntheticAudioEngine Synthetic => _synthetic ?? throw new InvalidOperationException("not created");

        protected override AudioEngine CreateEngine() => _synthetic = new SyntheticAudioEngine();

        public void Register(params Speaker[] speakers)
        {
            foreach (var speaker in speakers)
            {
                speaker.SampleRate = _store.SampleRate;
                Speakers.Add(speaker);
            }
        }

        public void StartEngine() => Engine.Start(string.Empty, _store.SampleRate);

        public void StopEngine() => Engine.Stop();
    }

    /// <summary>供 Program 复用的 adb forward 真实探测（可带真机执行）。</summary>
    public static int AdbForwardReal()
    {
        return TestHarness.Run("adb forward 真实转发与端口可达（需要真机）", () =>
        {
            var adb = new AdbController();
            var devices = adb.ListOnlineDevicesAsync().GetAwaiter().GetResult();
            if (devices.Count == 0)
            {
                throw new InvalidOperationException("未检测到已授权真机（adb devices 为空）——该项由用户在接入真机后复测");
            }
            var serial = devices[0].Serial;
            var port = NetworkInterfaceFilter.GetFreePort();
            TestHarness.Assert(adb.CreateForwardAsync(serial, port).GetAwaiter().GetResult(), $"adb forward tcp:{port} 成功");
            TestHarness.Assert(NetworkInterfaceFilter.PortIsOpen("127.0.0.1", port, 1000), $"127.0.0.1:{port} 可达");
            adb.RemoveForwardByRemoteAsync(serial, AdbController.RemoteSocket).GetAwaiter().GetResult();
            TestHarness.Assert(!NetworkInterfaceFilter.PortIsOpen("127.0.0.1", port, 500), "转发移除后端口不可达");
            var ip = NetworkInterfaceFilter.PrimaryPhysicalAddress();
            TestHarness.Evidence($"本机物理网段: {ip}");
        });
    }

    public static IPEndPoint LoopbackEndPoint(int port) => new(IPAddress.Loopback, port);
}
