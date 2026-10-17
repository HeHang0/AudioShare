using AudioShare;

namespace AudioShare.Cli;

public static class Program
{
    private static readonly string Scratch = Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? Path.GetTempPath();
    private static byte[]? _captureSink;
    private static readonly object CaptureGate = new();
    private static int _captureLength;

    public static async Task<int> Main(string[] args)
    {
        Logger.Init(console: false, filePath: Path.Combine(Scratch, "audioshare-cli.log"));
        TestHarness.Verbose = args.Contains("--verbose");

        var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "help";
        try
        {
            return command switch
            {
                "selftest" => await SelfTestAsync(args).ConfigureAwait(false),
                "monitors" => await MonitorsAsync().ConfigureAwait(false),
                "capture" => await CaptureAsync(args).ConfigureAwait(false),
                "serve" => await ServeAsync(args).ConfigureAwait(false),
                "discover" => await DiscoverAsync(args).ConfigureAwait(false),
                "adb-check" => await AdbCheckAsync(args).ConfigureAwait(false),
                "e2e" => await E2EAsync(args).ConfigureAwait(false),
                "download-apk" => await DownloadApkAsync(args).ConfigureAwait(false),
                "stream" => await StreamAsync(args).ConfigureAwait(false),
                "net-report" => await NetReportAsync().ConfigureAwait(false),
                "regress" => RegressAsync(),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.GetType().Name}: {ex.Message}");
            if (TestHarness.Verbose) Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            audioshare-cli — AudioShare Linux 客户端的自检与验证工具（无 GUI）

            用法: audioshare-cli <命令> [选项]

            命令:
              selftest [--keepalive-seconds N] [--skip-real-audio]
                        协议字节级一致性、声道抽取、静音保活、多设备分发、发现与网卡过滤的完整自检
              regress   回归测试（不接真机、不播放声音）：USB/Wi-Fi 分类、采样率重新握手、发送与采集节拍防突发
              monitors  枚举输出设备与其 .monitor 采集源（含默认设备标记）
              capture   [--seconds N] [--source S] [--rate N] [--wav PATH]
                        采集系统正在播放的声音并统计（RMS/频率/实时速率），可导出 WAV 供离线分析
              serve     [--port N] [--synthetic-tone] [--seconds N]
                        运行一个模拟 Android 端协议的接收端（默认 8088 端口），用于设备侧联调
              discover  [--seconds N] 监听 UDP 58261-58270 的 Android 广播并打印设备
              adb-check [--forward] [--ensure <serial>]  adb 枚举 / forward 验证 / APK 安装检查
              e2e       [--seconds N] 通过 pw-cat 播放测试音并从 monitor 采集，验证端到端采集链路
              net-report  打印本机所有网卡及其过滤判定（ACCEPT/REJECT 原因）
              download-apk [--output PATH]  下载官方 AudioShare.apk 到指定路径
              stream    --target <ip[:port]> [--channel stereo|left|right] [--seconds N]
                        [--ensure] [--tone] [--source MONITOR]
                        作为真实客户端连接一台设备并持续推流（端到端实测），打印实时统计
            """);
        return 0;
    }

    private static async Task<int> MonitorsAsync()
    {
        var devices = await MonitorEnumerator.EnumerateAsync().ConfigureAwait(false);
        Console.WriteLine($"枚举到 {devices.Count} 个输出设备（含 monitor 采集源）:");
        foreach (var device in devices)
        {
            Console.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.Name}\n      monitor_source = {device.MonitorSource}");
        }
        var volume = await MonitorEnumerator.GetDefaultVolumeAsync().ConfigureAwait(false);
        var sink = await MonitorEnumerator.GetDefaultSinkAsync().ConfigureAwait(false);
        Console.WriteLine($"默认输出设备: {sink}  音量: {volume}%");
        return 0;
    }

    private static async Task<int> NetReportAsync()
    {
        Console.WriteLine("网卡过滤判定（只扫描真实物理局域网网段）:");
        Console.WriteLine(await NetworkInterfaceFilter.ReportAsync().ConfigureAwait(false));
        Console.WriteLine();
        Console.WriteLine("候选扫描网段:");
        Console.WriteLine(NetworkInterfaceFilter.DescribeLocalNetworks());
        return 0;
    }

    private static async Task<int> CaptureAsync(string[] args)
    {
        var seconds = ParseIntArg(args, "--seconds", 5);
        var rate = ParseIntArg(args, "--rate", 48000);
        var source = ParseStringArg(args, "--source") ?? string.Empty;
        var wav = ParseStringArg(args, "--wav");

        using var engine = new AudioEngine();
        lock (CaptureGate)
        {
            _captureSink = new byte[rate * 4 * (seconds + 2)];
            _captureLength = 0;
        }
        engine.Frame += (_, frame) => AppendCapture(frame.Data);
        engine.Start(source, rate);
        Console.WriteLine($"开始采集 {seconds}s: source={(string.IsNullOrEmpty(source) ? "@DEFAULT_MONITOR@" : source)} rate={rate} frame={engine.FrameBytes}B");
        var marker = 0L;
        for (var elapsed = 0; elapsed < seconds; elapsed++)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            var stats = engine.Stats();
            if (stats.Frames == marker) continue;
            marker = stats.Frames;
            Console.WriteLine($"  t={stats.Seconds:0.0}s frames={stats.Frames} real={stats.CapturedFrames} silence={stats.SilenceFrames} fps={stats.FramesPerSecond:0.##}");
        }
        engine.Stop();

        var stats2 = engine.Stats();
        Console.WriteLine($"采集结束: frames={stats2.Frames} 实际数据帧={stats2.CapturedFrames} 静音补齐帧={stats2.SilenceFrames}");
        Console.WriteLine($"  投递速率={stats2.FramesPerSecond:0.###} 帧/秒（期望≈{1000.0 / AudioEngine.DefaultFrameMilliseconds:0}） 实时比={stats2.RealTimeRatio:0.####}");
        Console.WriteLine($"  monitor 源状态: {await AudioEngine.QuerySourceStateAsync(source).ConfigureAwait(false)}");
        if (stats2.LastError != null) Console.WriteLine($"  lastError={stats2.LastError}");

        byte[] pcm;
        lock (CaptureGate)
        {
            pcm = _captureSink == null ? [] : _captureSink[..Math.Min(_captureLength, _captureSink.Length)];
        }
        var samples = ToneGenerator.ToSamples(pcm, channel: -1, channels: 2);
        Console.WriteLine($"  采集字节={pcm.Length} RMS={ToneGenerator.Rms(samples):0} 峰值={ToneGenerator.Peak(samples):0}");

        if (!string.IsNullOrEmpty(wav))
        {
            await File.WriteAllBytesAsync(wav, ToneGenerator.ToWave(pcm, rate)).ConfigureAwait(false);
            Console.WriteLine($"  已导出 WAV: {wav} ({pcm.Length} 字节 PCM)");
        }
        return 0;
    }

    private static void AppendCapture(byte[] data)
    {
        lock (CaptureGate)
        {
            if (_captureSink == null) return;
            if (_captureLength + data.Length > _captureSink.Length) return;
            Buffer.BlockCopy(data, 0, _captureSink, _captureLength, data.Length);
            _captureLength += data.Length;
        }
    }

    private static async Task<int> ServeAsync(string[] args)
    {
        var seconds = ParseIntArg(args, "--seconds", 0);
        var synthetic = args.Contains("--synthetic-tone");
        var port = ParseIntArg(args, "--port", Protocol.DefaultTcpPort);

        using var pool = synthetic ? new SyntheticServePool(SettingsStore.Load()) : new DevicePool(SettingsStore.Load());
        if (!synthetic)
        {
            pool.Settings.IsUSB = false;
        }
        // 发现设备自动入列
        var server = new PortForwardedTestServer(port);
        await server.StartAsync().ConfigureAwait(false);
        Console.WriteLine($"[serve] 模拟 Android 接收端已监听 0.0.0.0:{port}");
        if (synthetic)
        {
            Console.WriteLine("[serve] 使用合成采集源（1kHz 左 / 2kHz 右），可验证声道分发");
        }
        pool.Start();
        var speaker = new Speaker($"127.0.0.1:{port}", AudioChannel.Stereo) { SampleRate = pool.SampleRate };
        pool.Speakers.Add(speaker);
        var connected = await speaker.ConnectAsync().ConfigureAwait(false);
        Console.WriteLine($"[serve] 客户端连接结果: {connected} ({speaker.LastError})");
        pool.Engine.Start(pool.Settings.AudioId, pool.SampleRate);

        var deadline = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            var stats = pool.Stats();
            Console.WriteLine($"[serve] frames={stats.Frames} fps={stats.FramesPerSecond:0.##} 数据帧={stats.CapturedFrames} 静音帧={stats.SilenceFrames} 已发送={speaker.FramesSent} 丢帧={speaker.FramesDropped} 接收端帧={server.FramesReceived}");
        }
        pool.Engine.Stop();
        await speaker.DisconnectAsync().ConfigureAwait(false);
        await server.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> DiscoverAsync(string[] args)
    {
        var seconds = ParseIntArg(args, "--seconds", 15);
        using var discovery = new UdpDiscovery();
        var found = new List<DiscoveredDevice>();
        discovery.DeviceDiscovered += (_, device) =>
        {
            found.Add(device);
            Console.WriteLine($"[discover] {device.Id} http={device.HttpUrl}");
        };
        discovery.Start();
        Console.WriteLine($"[discover] 监听 58261-58270 共 {seconds}s（Android 端每 10s 广播一次）");
        await Task.Delay(seconds * 1000).ConfigureAwait(false);
        Console.WriteLine($"[discover] 收到 {discovery.Messages} 条报文，接受 {discovery.Accepted}，丢弃 {discovery.Dropped}，设备 {found.Count} 台");
        return found.Count > 0 ? 0 : 1;
    }

    private static async Task<int> AdbCheckAsync(string[] args)
    {
        var adb = new AdbController();
        if (!adb.Available)
        {
            Console.Error.WriteLine("未找到 adb，请安装 android-tools-adb 或 platform-tools");
            return 2;
        }
        Console.WriteLine($"adb: {adb.AdbPath}");
        await adb.StartServerAsync().ConfigureAwait(false);
        var devices = await adb.ListDevicesAsync().ConfigureAwait(false);
        Console.WriteLine($"设备列表（{devices.Count}）:");
        foreach (var device in devices)
        {
            Console.WriteLine($"  {device.Serial}  state={device.State}  model={device.Model}");
            if (!string.Equals(device.State, "device", StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"    展示名: {await adb.ResolveDisplayNameAsync(device).ConfigureAwait(false)}");
        }
        if (args.Contains("--forward"))
        {
            var online = devices.Where(d => d.State == "device").ToList();
            if (online.Count == 0)
            {
                Console.Error.WriteLine("没有已授权的在线设备，无法验证 adb forward（请接入真机）");
                return 1;
            }
            var port = NetworkInterfaceFilter.GetFreePort();
            var ok = await adb.CreateForwardAsync(online[0].Serial, port).ConfigureAwait(false);
            var reachable = NetworkInterfaceFilter.PortIsOpen("127.0.0.1", port, 1000);
            Console.WriteLine($"adb forward tcp:{port} -> localabstract:picapico-audio-share: {ok}, 端口可达={reachable}");
            await adb.RemoveForwardByRemoteAsync(online[0].Serial, AdbController.RemoteSocket).ConfigureAwait(false);
        }
        var ensure = ParseStringArg(args, "--ensure");
        if (ensure != null)
        {
            var installer = new AndroidInstaller(adb);
            Console.WriteLine($"APK 路径: {AndroidInstaller.FindApk() ?? "(未找到，请先 download-apk)"}");
            var ready = await installer.EnsureAsync(ensure).ConfigureAwait(false);
            Console.WriteLine($"EnsureDevice({ensure}) = {ready}");
        }
        foreach (var line in adb.Log) Console.WriteLine($"  [adb] {line}");
        return 0;
    }

    private static async Task<int> E2EAsync(string[] args)
    {
        var seconds = ParseIntArg(args, "--seconds", 6);
        var rate = ParseIntArg(args, "--rate", 48000);
        var source = ParseStringArg(args, "--source") ?? string.Empty;
        var toneWav = Path.Combine(Scratch, "e2e-tone.wav");
        var pcm = ToneGenerator.Stereo(440, 880, rate, seconds * 1000 + 500);
        await File.WriteAllBytesAsync(toneWav, ToneGenerator.ToWave(pcm, rate)).ConfigureAwait(false);
        Console.WriteLine($"[e2e] 测试音已生成: {toneWav} (左 440Hz / 右 880Hz)");

        using var engine = new AudioEngine();
        lock (CaptureGate)
        {
            _captureSink = new byte[rate * 4 * (seconds + 3)];
            _captureLength = 0;
        }
        engine.Frame += (_, frame) => AppendCapture(frame.Data);
        engine.Start(source, rate);
        await Task.Delay(400).ConfigureAwait(false);

        var player = ProcessRunner.Which("pw-cat") ?? ProcessRunner.Which("paplay") ?? ProcessRunner.Which("aplay");
        if (player == null) throw new FileNotFoundException("需要 pw-cat / paplay / aplay 之一用于播放测试音");
        var playerArgs = Path.GetFileName(player) switch
        {
            "pw-cat" => new[] { "--playback", toneWav },
            "paplay" => [toneWav],
            _ => [toneWav],
        };
        Console.WriteLine($"[e2e] 播放: {player} {string.Join(' ', playerArgs)}");
        var playback = ProcessRunner.RunAsync(player, playerArgs, TimeSpan.FromSeconds(seconds + 5)).ConfigureAwait(false);
        var result = await playback;
        Console.WriteLine($"[e2e] 播放退出码={result.ExitCode} stderr={result.StandardError.Trim()}");
        await Task.Delay(700).ConfigureAwait(false);
        engine.Stop();

        byte[] captured;
        lock (CaptureGate)
        {
            captured = _captureSink == null ? [] : _captureSink[..Math.Min(_captureLength, _captureSink.Length)];
        }
        var stats = engine.Stats();
        var left = ToneGenerator.ToSamples(captured, channel: 0, channels: 2);
        var right = ToneGenerator.ToSamples(captured, channel: 1, channels: 2);
        var leftTone = ToneGenerator.GoertzelAmplitude(left, 440, rate);
        var rightTone = ToneGenerator.GoertzelAmplitude(right, 880, rate);
        var rms = ToneGenerator.Rms(left);
        Console.WriteLine($"[e2e] 采集统计: frames={stats.Frames} 实际数据帧={stats.CapturedFrames} 静音帧={stats.SilenceFrames} fps={stats.FramesPerSecond:0.##} 实时比={stats.RealTimeRatio:0.####}");
        Console.WriteLine($"[e2e] 采集字节={captured.Length} RMS(左)={rms:0} 440Hz 幅度={leftTone:0} 880Hz 幅度={rightTone:0}");
        Console.WriteLine($"[e2e] monitor 源状态: {await AudioEngine.QuerySourceStateAsync(source).ConfigureAwait(false)}");

        var ok = stats.CapturedFrames > 0 && leftTone > 500 && rightTone > 500;
        Console.WriteLine(ok
            ? "[e2e] PASS：采集到系统播放的真实 PCM（双声道音调均存在）"
            : "[e2e] FAIL：未采集到预期音调（检查音频路由/静音状态）");
        if (!string.IsNullOrEmpty(ParseStringArg(args, "--wav")))
        {
            var wav = ParseStringArg(args, "--wav")!;
            await File.WriteAllBytesAsync(wav, ToneGenerator.ToWave(captured, rate)).ConfigureAwait(false);
            Console.WriteLine($"[e2e] 已导出采集 WAV: {wav}");
        }
        return ok ? 0 : 1;
    }


    /// <summary>
    /// 端到端实测：作为真实客户端连到一台 Android 设备并持续推流。
    /// 走与 GUI 完全相同的代码路径（DevicePool.ConnectAsync → adb 回退 → Speaker → AudioEngine）。
    /// </summary>
    private static async Task<int> StreamAsync(string[] args)
    {
        var target = ParseStringArg(args, "--target");
        var usbArg = ParseStringArg(args, "--usb");
        if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(usbArg))
        {
            Console.Error.WriteLine("用法: audioshare-cli stream --target <ip[:port]> | --usb <serial> [--channel stereo|left|right] [--seconds 180] [--frame-ms 100] [--ensure] [--tone] [--source MONITOR]");
            return 2;
        }
        target ??= usbArg;
        var seconds = ParseIntArg(args, "--seconds", 60);
        var channel = (ParseStringArg(args, "--channel") ?? "stereo").ToLowerInvariant() switch
        {
            "left" => AudioChannel.Left,
            "right" => AudioChannel.Right,
            "none" or "disable" => AudioChannel.None,
            _ => AudioChannel.Stereo,
        };
        var ensure = args.Contains("--ensure");
        var tone = args.Contains("--tone");
        var source = ParseStringArg(args, "--source") ?? string.Empty;
        var frameMs = ParseIntArg(args, "--frame-ms", AudioEngine.DefaultFrameMilliseconds);
        // --usb: 走 USB/adb 通道（adb forward tcp:<port> localabstract:picapico-audio-share），
        // 与 GUI 的 USB 模式完全相同：RefreshSpeakers → 安装/拉起检查 → adb forward → 连接 127.0.0.1:<port>
        var usbSerial = usbArg;

        var settings = SettingsStore.Load();
        settings.FrameMs = frameMs;
        using var pool = new DevicePool(settings, new AdbController());
        var deviceId = usbSerial ?? target ?? throw new InvalidOperationException("device id missing");
        var speaker = usbSerial != null
            ? new Speaker(deviceId, channel, isUsb: true) { SampleRate = settings.SampleRate }
            : new Speaker(deviceId, channel) { SampleRate = settings.SampleRate };
        speaker.StatusChanged += (_, status) => Console.WriteLine($"[stream] 状态变更: {speaker.Display} -> {status}");
        speaker.Disconnected += (_, s) => Console.WriteLine($"[stream] 设备断开: {s.Display} ({s.LastError})");
        pool.Speakers.Add(speaker);
        if (usbSerial != null) pool.ConfigureUsbForwardFor(speaker);

        Console.WriteLine($"[stream] 模式={(usbSerial != null ? $"USB/adb forward ({usbSerial})" : "Wi-Fi")} 目标={target} 声道={channel} 采样率={settings.SampleRate} 帧长={frameMs}ms(={Protocol.FrameBytes(settings.SampleRate, frameMs)}B) 时长={seconds}s ensure={ensure} tone={tone}");
        var parsed = new Speaker(deviceId).TryParseAddress(out var ip, out var pnum);
        var port = parsed ? pnum : 8088;
        if (usbSerial == null)
        {
            Console.WriteLine($"[stream] 预检: {ip}:{port} TCP 可达={await NetworkInterfaceFilter.PortIsOpenAsync(ip, port, 1500).ConfigureAwait(false)}");
        }
        else
        {
            await pool.Adb.StartServerAsync().ConfigureAwait(false);
            var adbDevices = await pool.Adb.ListDevicesAsync().ConfigureAwait(false);
            Console.WriteLine($"[stream] adb 设备: {string.Join(", ", adbDevices.Select(d => $"{d.Serial}({d.State})"))}");
        }

        var ok = await pool.ConnectAsync(speaker).ConfigureAwait(false);
        Console.WriteLine($"[stream] 连接结果={ok} 状态={speaker.Status} 远端={speaker.RemoteAddress}:{speaker.RemotePort} 错误={speaker.LastError ?? "(none)"}");
        if (!ok || !speaker.Connected)
        {
            Console.Error.WriteLine("[stream] 连接失败，终止");
            return 1;
        }

        // 需要 ensure 时，走 adb 安装/拉起流程（与 windows/Speaker.cs:337-386 同序列）
        if (ensure)
        {
            var serial = await ResolveSerialAsync(ip).ConfigureAwait(false);
            if (serial == null)
            {
                Console.Error.WriteLine($"[stream] adb 中找不到与 {ip} 匹配的设备（可先 adb connect {ip}:5555）");
            }
            else
            {
                var installer = new AndroidInstaller(pool.Adb);
                var matched = await installer.IsPackageVersionMatchedAsync(serial).ConfigureAwait(false);
                Console.WriteLine($"[stream] adb 设备={serial} 版本匹配({Protocol.AndroidVersionName})={matched}");
                var ready = await installer.EnsureAsync(serial).ConfigureAwait(false);
                Console.WriteLine($"[stream] EnsureDevice = {ready}（已执行 versionName 比对 → 必要时 install -r → am start）");
            }
        }

        ProcessRunner.Result? playback = null;
        if (tone)
        {
            var wav = Path.Combine(Scratch, "stream-tone.wav");
            var pcm = ToneGenerator.Stereo(440, 880, settings.SampleRate, (seconds + 5) * 1000);
            await File.WriteAllBytesAsync(wav, ToneGenerator.ToWave(pcm, settings.SampleRate)).ConfigureAwait(false);
            var player = ProcessRunner.Which("pw-cat") ?? ProcessRunner.Which("paplay");
            if (player != null)
            {
                Console.WriteLine($"[stream] 播放测试音（左 440Hz/右 880Hz）: {player} {wav}");
                _ = ProcessRunner.RunAsync(player, ["--playback", wav], TimeSpan.FromSeconds(seconds + 10));
            }
            _ = playback;
        }

        pool.Engine.Start(source, settings.SampleRate, settings.FrameMs);
        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(seconds);
        var lastFrames = 0L;
        var stalls = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(5000).ConfigureAwait(false);
            var stats = pool.Stats();
            var delta = stats.Frames - lastFrames;
            lastFrames = stats.Frames;
            if (delta == 0) stalls++;
            Console.WriteLine($"[stream] t={stats.Seconds:0}s 引擎帧={stats.Frames}(+{delta}) 真实={stats.CapturedFrames} 静音={stats.SilenceFrames} fps={stats.FramesPerSecond:0.##} | 设备: 已发送={speaker.FramesSent} 丢帧={speaker.FramesDropped} 心跳={speaker.HeartbeatsSent} 连接={speaker.Status} | 无进展次数={stalls}");
        }
        pool.Engine.Stop();
        var finalStats = pool.Stats();
        Console.WriteLine($"[stream] 结束: 引擎帧={finalStats.Frames} 实际数据帧={finalStats.CapturedFrames} 静音补齐={finalStats.SilenceFrames} 平均fps={finalStats.FramesPerSecond:0.##}");
        Console.WriteLine($"[stream] 设备: 已发送={speaker.FramesSent} 丢帧={speaker.FramesDropped} 心跳={speaker.HeartbeatsSent} 重连={speaker.ReconnectCount} 最终状态={speaker.Status} 错误={speaker.LastError ?? "(none)"}");
        Console.WriteLine($"[stream] 连接是否全程保持: {(speaker.Connected ? "是" : "否")}，无数据中断次数={stalls}");

        await speaker.DisconnectAsync().ConfigureAwait(false);
        Console.WriteLine("[stream] 已断开");
        return speaker.Status == ConnectStatus.UnConnected ? 0 : 1;
    }

    private static async Task<string?> ResolveSerialAsync(string host)
    {
        var adb = new AdbController();
        if (!adb.Available) return null;
        await adb.ConnectAsync(host, 5555).ConfigureAwait(false);
        var devices = await adb.ListOnlineDevicesAsync().ConfigureAwait(false);
        return devices.FirstOrDefault(d => d.Serial.StartsWith(host, StringComparison.Ordinal))?.Serial;
    }

    private static async Task<int> DownloadApkAsync(string[] args)
    {
        var output = ParseStringArg(args, "--output");
        var path = await ApkDownloader.DownloadAsync(output).ConfigureAwait(false);
        Console.WriteLine($"APK: {path}");
        Console.WriteLine($"SHA256: {AndroidInstaller.Sha256(path)}");
        Console.WriteLine($"大小: {new FileInfo(path).Length} 字节");
        return 0;
    }

    private static async Task<int> SelfTestAsync(string[] args)
    {
        var keepAliveSeconds = ParseIntArg(args, "--keepalive-seconds", 13);
        var results = new List<int>
        {
            SelfTests.ProtocolConstants(),
            SelfTests.ChannelSplit(),
            SelfTests.AddressParsing(),
            SelfTests.CaptureSource(),
            SelfTests.AdbSanity(),
            SelfTests.Discovery(),
            SelfTests.MultiDeviceAndChannels(),
            RegressionTests.RunAll(),
        };

        // 真实进程内 TCP 会话：协议回放 + 保活 + 控制指令
        var server = new TestTcpServer("selftest");
        var speaker = new Speaker($"127.0.0.1:{server.Port}", AudioChannel.Stereo) { SampleRate = 48000 };
        var clientTap = new List<byte>();
        var firstWrites = new List<string>();
        var tapSequence = 0;
        speaker.OnWireWrite = (data, tag) =>
        {
            if (tag is not (0 or 3)) return;
            lock (clientTap)
            {
                var seq = ++tapSequence;
                var offset = clientTap.Count;
                clientTap.AddRange(data);
                if (offset > 0 && seq > 4)
                {
                    firstWrites.Add($"#{seq} tag={tag} offset={offset} len={data.Length}");
                }
                else if (seq <= 4)
                {
                    firstWrites.Add($"#{seq} tag={tag} offset={offset} len={data.Length} {ProtocolVerifier.ToHex(data, 24)}");
                }
            }
        };
        try
        {
            var connected = await speaker.ConnectAsync().ConfigureAwait(false);
            TestHarness.Run("建立音频连接（Stop → connect → handshake → ack）", () =>
            {
                TestHarness.Assert(connected, "音频连接已建立");
                TestHarness.Assert(speaker.Connected, "连接状态 = Connected");
            });
            results.Add(SelfTests.ProtocolOnWire(server, speaker, clientTap, firstWrites));
            results.Add(SelfTests.KeepAlive(server, speaker, keepAliveSeconds));
            results.Add(SelfTests.ControlCommands(server, speaker));
        }
        finally
        {
            await speaker.DisconnectAsync().ConfigureAwait(false);
            speaker.Dispose();
            await server.DisposeAsync().ConfigureAwait(false);
        }

        if (!args.Contains("--skip-real-audio"))
        {
            results.Add(E2EAsync(["--seconds", "5"]).GetAwaiter().GetResult());
        }

        TestHarness.Summary();
        var failed = results.Sum(r => r);
        Console.WriteLine(failed == 0 ? "全部自检通过 ✅" : $"存在 {failed} 项失败 ❌");
        return failed == 0 ? 0 : 1;
    }

    private static int RegressAsync()
    {
        var failed = RegressionTests.RunAll();
        TestHarness.Summary();
        return failed == 0 ? 0 : 1;
    }

    private static int ParseIntArg(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], out var value)) return value;
        }
        return fallback;
    }

    private static string? ParseStringArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }

    private sealed class SyntheticServePool(SettingsStore settings) : DevicePool(settings, new AdbController())
    {
        protected override AudioEngine CreateEngine() => new SyntheticServeEngine();
    }

    private sealed class SyntheticServeEngine : AudioEngine
    {
        public override ICaptureSource CreateCaptureSource(string source, int sampleRate) => new SyntheticServeSource(sampleRate);
    }

    private sealed class SyntheticServeSource(int sampleRate) : ICaptureSource
    {
        private long _frames;

        public void Start() { }

        public int Read(RingBuffer ring, int timeoutMs)
        {
            Thread.Sleep(Math.Max(timeoutMs / 4, 1));
            const int chunkBytes = 8192;
            var chunk = new byte[chunkBytes];
            var frames = chunkBytes / 4;
            for (var i = 0; i < frames; i++)
            {
                var index = _frames + i;
                var t = index / (double)sampleRate;
                var left = (short)(Math.Sin(2 * Math.PI * 1000 * t) * 0.5 * short.MaxValue);
                var right = (short)(Math.Sin(2 * Math.PI * 2000 * t) * 0.5 * short.MaxValue);
                BitConverter.TryWriteBytes(chunk.AsSpan(i * 4, 2), left);
                BitConverter.TryWriteBytes(chunk.AsSpan(i * 4 + 2, 2), right);
            }
            _frames += frames;
            ring.Write(chunk);
            return chunkBytes;
        }

        public void Dispose() { }
    }

    /// <summary>serve 命令用的接收端：直接监听固定端口，统计收到的帧。</summary>
    private sealed class PortForwardedTestServer(int port) : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Any, port);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _tasks = [];
        private int _framesReceived;

        public int FramesReceived => Volatile.Read(ref _framesReceived);

        public Task StartAsync()
        {
            _listener.Start();
            _tasks.Add(AcceptLoopAsync());
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleAsync(client), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        private async Task HandleAsync(System.Net.Sockets.TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var head = new byte[Protocol.Head.Length];
                await stream.ReadExactlyAsync(head, _cts.Token).ConfigureAwait(false);
                Console.WriteLine($"[serve] 收到头部: {System.Text.Encoding.ASCII.GetString(head)}");
                var command = new byte[1];
                await stream.ReadExactlyAsync(command, _cts.Token).ConfigureAwait(false);
                if (command[0] == (byte)Command.AudioData)
                {
                    var parameters = new byte[8];
                    await stream.ReadExactlyAsync(parameters, _cts.Token).ConfigureAwait(false);
                    Console.WriteLine($"[serve] 握手: rate={BitConverter.ToInt32(parameters, 0)} mask={BitConverter.ToInt32(parameters, 4)}");
                    await stream.WriteAsync(new byte[1], _cts.Token).ConfigureAwait(false);
                    var lengthBuffer = new byte[4];
                    while (!_cts.IsCancellationRequested)
                    {
                        await stream.ReadExactlyAsync(lengthBuffer, _cts.Token).ConfigureAwait(false);
                        var length = BitConverter.ToInt32(lengthBuffer, 0);
                        if (length == 0) continue;
                        var payload = new byte[length];
                        await stream.ReadExactlyAsync(payload, _cts.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref _framesReceived);
                    }
                }
                else
                {
                    if (command[0] == (byte)Command.Volume)
                    {
                        var payload = new byte[4];
                        await stream.ReadExactlyAsync(payload, _cts.Token).ConfigureAwait(false);
                        Console.WriteLine($"[serve] 音量指令: {BitConverter.ToInt32(payload, 0)}");
                    }
                    else
                    {
                        Console.WriteLine($"[serve] 控制指令: {command[0]}");
                    }
                    await stream.WriteAsync(new byte[1], _cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[serve] 连接结束: {ex.GetType().Name}");
            }
            finally
            {
                client.Dispose();
            }
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
}
