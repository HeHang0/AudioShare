using System.Collections.ObjectModel;
using System.ComponentModel;

namespace AudioShare;

/// <summary>
/// 设备池 / 应用主模型。对应 windows/Model.cs：
/// 统一管理采集设备、设备列表（USB 与 Wi-Fi）、发现、心跳、音量下发与设置持久化。
/// </summary>
public class DevicePool : IDisposable
{
    private readonly SettingsStore _settings;
    private UdpDiscovery? _discovery;
    private Timer? _heartbeatTimer;
    private Timer? _volumeTimer;
    private Timer? _volumeSendTimer;
    private int _lastKnownSystemVolume = -1;
    private int _pendingVolume = -1;
    private int _volumeSending;
    private DateTime _lastVolumeSaveUtc = DateTime.MinValue;
    private bool _disposed;
    private readonly SemaphoreSlim _formatGate = new(1, 1);
    private IReadOnlyList<string> _wirelessAdbSerials = [];

    public DevicePool(SettingsStore? settings = null, AdbController? adb = null)
    {
        _settings = settings ?? SettingsStore.Load();
        Adb = adb ?? new AdbController();
        Installer = new AndroidInstaller(Adb);
        Engine = CreateEngine();
        Engine.Frame += OnPcmFrame;
    }

    public SettingsStore Settings => _settings;

    public AdbController Adb { get; }

    public AndroidInstaller Installer { get; }

    public AudioEngine Engine { get; }

    /// <summary>测试可用子类替换采集源。</summary>
    protected virtual AudioEngine CreateEngine() => new();

    public ObservableCollection<Speaker> Speakers { get; } = [];

    public ObservableCollection<OutputDevice> AudioDevices { get; } = [];

    public ObservableCollection<int> SampleRates { get; } =
        [192000, 176400, 96000, 48000, 44100, 16000, 8000];

    public event EventHandler? StateChanged;

    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;

    public event EventHandler<Speaker>? SpeakerDisconnected;

    public int SampleRate
    {
        get => _settings.SampleRate;
        set
        {
            if (_settings.SampleRate == value) return;
            _settings.SampleRate = value;
            foreach (var speaker in Speakers) speaker.SampleRate = value;
            _settings.Save();
            StateChanged?.Invoke(this, EventArgs.Empty);
            _ = ApplyAudioFormatAsync(Speakers.Where(s => s.Connected).ToArray());
        }
    }

    public int Volume
    {
        get => _settings.Volume;
        set
        {
            if (_settings.Volume == value) return;
            _settings.Volume = value;
            // 滑块拖动会高频触发 setter：这里只更新内存并标记待发，
            // 由 _volumeSendTimer（100ms）合并下发最新值，避免为每个像素变化
            // 新建控制连接导致设备端排队、松手后才变声。
            if (!VolumeFollowSystem) Interlocked.Exchange(ref _pendingVolume, value);
        }
    }

    public bool VolumeFollowSystem
    {
        get => _settings.VolumeFollowSystem;
        set
        {
            if (_settings.VolumeFollowSystem == value) return;
            _settings.VolumeFollowSystem = value;
            _settings.Save();
            if (value) _ = SyncVolumeFromSystemAsync();
        }
    }

    public bool IsUSB
    {
        get => _settings.IsUSB;
        set
        {
            if (_settings.IsUSB == value) return;
            ResetSpeakerSettings();
            _settings.IsUSB = value;
            _settings.Save();
            _ = RefreshSpeakersAsync();
        }
    }

    public string AudioId => _settings.AudioId;

    /// <summary>UI 侧显式设置设备声道并持久化（windows/Speaker.ChannelSelected 的持久化等价）。</summary>
    public void SetSpeakerChannel(Speaker speaker, AudioChannel channel)
    {
        speaker.Channel = channel;
        ResetSpeakerSettings();
        _settings.Save();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetSpeakerHttpAddressAsync(Speaker speaker, string? httpAddress)
    {
        speaker.HttpAddress = httpAddress;
        await Task.CompletedTask.ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public int ConnectedCount => Speakers.Count(s => s.Connected);

    public bool AnyConnected => ConnectedCount > 0;

    public UdpDiscovery? Discovery => _discovery;

    /// <summary>最近一次扫描到的无线调试设备序列号（adb over Wi-Fi，不是 USB）。</summary>
    public IReadOnlyList<string> WirelessAdbSerials => _wirelessAdbSerials;

    /// <summary>启动后台任务：UDP 发现、心跳定时器、音量跟随。</summary>
    public void Start()
    {
        Logger.Info("device pool starting");
        _discovery = new UdpDiscovery();
        _discovery.DeviceDiscovered += (_, device) =>
        {
            DeviceDiscovered?.Invoke(this, device);
            AddIPSpeaker(device.Address, device.TcpPort, device.HttpPort, persist: true, connect: false);
        };
        _discovery.Start();

        _heartbeatTimer = new Timer(_ => SendHeartbeats(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _volumeTimer = new Timer(_ => PollSystemVolume(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        _volumeSendTimer = new Timer(_ => FlushPendingVolume(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// 音量下发节流：合并拖动过程中产生的高频修改，每 100ms 至多下发一次最新值。
    /// 设备端（Android TcpService）每条控制指令都要新建会话，高频下发会排队，
    /// 表现为"拖动时不跟手、松手一两秒后突然变声"。
    /// </summary>
    private void FlushPendingVolume()
    {
        var volume = Interlocked.Exchange(ref _pendingVolume, -1);
        if (volume < 0 || VolumeFollowSystem) return;
        if (Interlocked.Exchange(ref _volumeSending, 1) == 1)
        {
            // 上一次仍在发送：保留最新值，下一个 tick 再发
            Interlocked.Exchange(ref _pendingVolume, volume);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await BroadcastVolumeAsync(volume).ConfigureAwait(false);
                // 配置保存同样节流：最多 1 秒写一次磁盘
                if (DateTime.UtcNow - _lastVolumeSaveUtc >= TimeSpan.FromSeconds(1))
                {
                    _lastVolumeSaveUtc = DateTime.UtcNow;
                    _settings.Save();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _volumeSending, 0);
            }
        });
    }

    private void SendHeartbeats()
    {
        foreach (var speaker in Speakers.ToArray())
        {
            if (speaker is { Connected: true }) speaker.SendHeartbeat();
        }
    }

    private void PollSystemVolume()
    {
        if (!VolumeFollowSystem) return;
        _ = SyncVolumeFromSystemAsync();
    }

    private async Task SyncVolumeFromSystemAsync()
    {
        var systemVolume = await MonitorEnumerator.GetDefaultVolumeAsync().ConfigureAwait(false);
        if (systemVolume == null || systemVolume == _lastKnownSystemVolume) return;
        _lastKnownSystemVolume = systemVolume.Value;
        Logger.Info($"system volume changed: {systemVolume.Value}");
        await BroadcastVolumeAsync(systemVolume.Value).ConfigureAwait(false);
    }

    private async Task BroadcastVolumeAsync(int volume)
    {
        foreach (var speaker in Speakers.ToArray())
        {
            if (speaker is { Connected: true })
            {
                await speaker.SetVolumeAsync(volume).ConfigureAwait(false);
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAudioDevicesAsync()
    {
        var devices = await MonitorEnumerator.EnumerateAsync().ConfigureAwait(false);
        AudioDevices.Clear();
        foreach (var device in devices) AudioDevices.Add(device);
        Logger.Info($"audio devices refreshed: {AudioDevices.Count}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SelectAudioDeviceAsync(string monitorSource)
    {
        _settings.AudioId = monitorSource;
        _settings.Save();
        await Task.Run(RestartEngine).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 采样率在握手时下发（设备据此创建 AudioTrack），因此已连接设备必须重新握手；否则采集已是新采样率，
    /// 设备仍按旧采样率播放，音调与速度都会错。整个过程在后台执行，不阻塞界面线程。
    /// </summary>
    private async Task ApplyAudioFormatAsync(Speaker[] connected)
    {
        if (connected.Length == 0) return;
        await _formatGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => Engine.Stop()).ConfigureAwait(false);
            foreach (var speaker in connected)
            {
                if (!speaker.Connected) continue;
                await ConnectAsync(speaker).ConfigureAwait(false);
            }
            Logger.Info($"sample rate {_settings.SampleRate}Hz applied; re-handshaked {connected.Length} device(s)");
        }
        catch (Exception ex)
        {
            Logger.Error($"apply audio format failed: {ex.Message}");
        }
        finally
        {
            _formatGate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>把无线调试设备登记到 Wi-Fi 列表（同 IP 的 8088 端口）。USB 列表不收录它。</summary>
    public void RegisterWirelessAdbDevices(IReadOnlyList<AdbDevice> wireless)
    {
        var targets = wireless.Where(d => d.IsWireless).ToList();
        _wirelessAdbSerials = targets.Select(d => d.Serial).ToList();
        var added = false;
        foreach (var device in targets)
        {
            var endpoint = $"{device.Serial[..device.Serial.LastIndexOf(':')]}:{Protocol.DefaultTcpPort}";
            if (_settings.IPDevices.Any(d => d.Id == endpoint)) continue;
            _settings.IPDevices.Add(new SettingsStore.DeviceEntry(endpoint, AudioChannel.None));
            added = true;
            Logger.Info($"wireless adb {device.Serial} -> Wi-Fi entry {endpoint}");
        }
        if (added) _settings.Save();
    }

    private void RestartEngine()
    {
        Engine.Stop();
        if (Speakers.Any(s => s.Connected))
        {
            Engine.Start(_settings.AudioId, _settings.SampleRate, _settings.FrameMs);
        }
    }

    public void EnsureEngineRunning()
    {
        if (Speakers.Any(s => s.Connected) && !Engine.IsRunning)
        {
            Engine.Start(_settings.AudioId, _settings.SampleRate, _settings.FrameMs);
        }
    }

    private void OnPcmFrame(object? sender, PcmFrameEventArgs e)
    {
        foreach (var speaker in Speakers.ToArray())
        {
            if (speaker != null && speaker.Connected && speaker.Channel != AudioChannel.None)
            {
                speaker.OnPcmFrame(e.Data);
            }
        }
    }

    public async Task RefreshSpeakersAsync(CancellationToken token = default)
    {
        if (_settings.IsUSB)
        {
            if (!Adb.Available)
            {
                Logger.Warning("adb not found; usb mode disabled");
                _settings.IsUSB = false;
                _settings.Save();
                await RefreshSpeakersAsync(token).ConfigureAwait(false);
                return;
            }

            var connected = Speakers.Where(s => s.Connected).ToList();
            await Adb.StartServerAsync(token).ConfigureAwait(false);
            var online = await Adb.ListOnlineDevicesAsync(token).ConfigureAwait(false);
            var (devices, wireless) = AdbController.SplitByTransport(online);
            devices.Sort((a, b) => string.CompareOrdinal(a.Serial, b.Serial));
            RegisterWirelessAdbDevices(wireless);
            Logger.Info($"adb devices online: usb={devices.Count} wireless={wireless.Count}");

            Speakers.Clear();
            foreach (var device in devices)
            {
                var existing = connected.FirstOrDefault(s => s.Id == device.Serial);
                if (existing != null)
                {
                    Speakers.Add(existing);
                    continue;
                }
                var name = await Adb.ResolveDisplayNameAsync(device, token).ConfigureAwait(false);
                var saved = _settings.AdbDevices.FirstOrDefault(d => d.Id == device.Serial);
                var speaker = new Speaker(device.Serial, saved?.Channel ?? AudioChannel.None, isUsb: true, name: name);
                Attach(speaker);
                Speakers.Add(speaker);
            }

            foreach (var stale in connected.Where(s => Speakers.All(m => m.Id != s.Id))) stale.Dispose();
        }
        else
        {
            if (Adb.Available)
            {
                try
                {
                    var online = await Adb.ListOnlineDevicesAsync(token).ConfigureAwait(false);
                    RegisterWirelessAdbDevices(AdbController.SplitByTransport(online).Wireless);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warning($"wireless adb scan failed: {ex.Message}");
                }
            }
            var connected = Speakers.Where(s => s.Connected).ToList();
            Speakers.Clear();
            foreach (var entry in _settings.IPDevices.DistinctBy(d => d.Id))
            {
                var existing = connected.FirstOrDefault(s => s.Id == entry.Id);
                if (existing != null)
                {
                    Speakers.Add(existing);
                    continue;
                }
                var speaker = new Speaker(entry.Id, entry.Channel);
                Attach(speaker);
                Speakers.Add(speaker);
            }
            foreach (var stale in connected.Where(s => Speakers.All(m => m.Id != s.Id))) stale.Dispose();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Attach(Speaker speaker)
    {
        speaker.SampleRate = _settings.SampleRate;
        speaker.FrameMilliseconds = _settings.FrameMs;
        speaker.StatusChanged += OnSpeakerStatusChanged;
        speaker.Disconnected += (_, s) => SpeakerDisconnected?.Invoke(this, s);
        if (speaker.IsUsb) ConfigureUsbForward(speaker);
    }

    /// <summary>为测试/CLI 显式配置 USB 转发（GUI 走 Attach 自动配置）。</summary>
    public void ConfigureUsbForwardFor(Speaker speaker) => ConfigureUsbForward(speaker);

    private void ConfigureUsbForward(Speaker speaker)
    {
        speaker.PortForwardAcquire = async token =>
        {
            var port = NetworkInterfaceFilter.GetFreePort();
            if (!await Adb.CreateForwardAsync(speaker.Id, port, token).ConfigureAwait(false))
            {
                throw new IOException($"adb forward failed for {speaker.Id}");
            }
            speaker.ForwardPort = port;
            Logger.Info($"usb forward ready: {speaker.Id} tcp:{port} -> {Protocol.AndroidRemoteSocket}");
        };
        speaker.PortForwardRelease = async token =>
            await Adb.RemoveForwardByRemoteAsync(speaker.Id, AdbController.RemoteSocket, token).ConfigureAwait(false);
    }

    /// <summary>连接设备。对应 windows/Speaker.Connect：先做 adb 安装/拉起检查，再建音频连接。</summary>
    public async Task<bool> ConnectAsync(Speaker speaker, CancellationToken token = default)
    {
        if (speaker.IsUsb)
        {
            if (!Adb.Available)
            {
                Logger.Error($"device not ready: {speaker.Id} (adb unavailable)");
                return false;
            }
            if (!await Installer.EnsureAsync(speaker.Id, token).ConfigureAwait(false))
            {
                Logger.Error($"device not ready: {speaker.Id} (apk not installed or version mismatch)");
                return false;
            }
        }
        else
        {
            if (!speaker.TryParseAddress(out var ip, out var port))
            {
                Logger.Error($"ip parse error: {speaker.Id}");
                return false;
            }
            var open = await NetworkInterfaceFilter.PortIsOpenAsync(ip, port, 1000, token).ConfigureAwait(false);
            Logger.Info($"wifi target {ip}:{port} port open = {open}");
            await EnsureRemoteDeviceAsync(speaker, ip, port, token).ConfigureAwait(false);
        }

        speaker.SampleRate = _settings.SampleRate;
        speaker.FrameMilliseconds = _settings.FrameMs;
        var connected = await speaker.ConnectAsync(token: token).ConfigureAwait(false);
        if (connected)
        {
            Engine.Start(_settings.AudioId, _settings.SampleRate, _settings.FrameMs);
            if (_settings.VolumeFollowSystem)
            {
                var systemVolume = await MonitorEnumerator.GetDefaultVolumeAsync(token).ConfigureAwait(false);
                if (systemVolume != null)
                {
                    _settings.Volume = systemVolume.Value;
                    _lastKnownSystemVolume = systemVolume.Value;
                }
            }
            await speaker.SetVolumeAsync(_settings.Volume, token).ConfigureAwait(false);
            await SyncTimeAsync(token).ConfigureAwait(false);
            ResetSpeakerSettings();
            _settings.Save();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        return connected;
    }

    /// <summary>
    /// Wi-Fi 侧 adb 回退（windows/Speaker.cs:308-335）：设备已 adb 连接则复用，
    /// 否则在 5555 可达时 adb connect，用完 disconnect。
    /// </summary>
    private async Task EnsureRemoteDeviceAsync(Speaker speaker, string ip, int port, CancellationToken token)
    {
        if (!Adb.Available) return;
        var temporaryConnect = false;
        try
        {
            var devices = await Adb.ListDevicesAsync(token).ConfigureAwait(false);
            var device = devices.FirstOrDefault(d => d.Serial.StartsWith(ip, StringComparison.Ordinal));
            if (device == null)
            {
                if (!await NetworkInterfaceFilter.PortIsOpenAsync(ip, 5555, 800, token).ConfigureAwait(false)) return;
                await Adb.ConnectAsync(ip, 5555, token).ConfigureAwait(false);
                temporaryConnect = true;
                devices = await Adb.ListDevicesAsync(token).ConfigureAwait(false);
                device = devices.FirstOrDefault(d => d.Serial.StartsWith(ip, StringComparison.Ordinal));
            }
            if (device == null) return;
            await Installer.EnsureAsync(device.Serial, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning($"wifi adb fallback failed for {ip}: {ex.Message}");
        }
        finally
        {
            if (temporaryConnect) await Adb.DisconnectAsync(ip, 5555, token).ConfigureAwait(false);
        }
        _ = port;
    }

    public async Task SyncTimeAsync(CancellationToken token = default)
    {
        foreach (var speaker in Speakers.ToArray())
        {
            if (speaker.Connected) await speaker.SyncTimeAsync(token).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync(Speaker speaker, CancellationToken token = default)
    {
        await speaker.DisconnectAsync().ConfigureAwait(false);
        StopEngineIfIdle();
        StateChanged?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void StopEngineIfIdle()
    {
        if (!Speakers.Any(s => s.Connected))
        {
            Engine.Stop();
        }
    }

    public Speaker AddIPSpeaker(string ip, int port, int httpPort = 0, bool persist = true, bool connect = true)
    {
        var id = $"{ip}:{port}";
        var existing = Speakers.FirstOrDefault(s => s.Id == id);
        if (existing != null)
        {
            if (httpPort > 0) existing.HttpPort = httpPort;
            return existing;
        }

        if (persist && _settings.IPDevices.All(d => d.Id != id))
        {
            _settings.IPDevices.Add(new SettingsStore.DeviceEntry(id, AudioChannel.None));
            _settings.Save();
        }

        var speaker = new Speaker(id, _settings.IPDevices.FirstOrDefault(d => d.Id == id)?.Channel ?? AudioChannel.None)
        {
            HttpPort = httpPort,
        };
        Attach(speaker);
        // USB 模式下列表只显示 USB 设备：Wi-Fi 设备仅登记到设置，切换到 Wi-Fi 模式时再显示
        if (!_settings.IsUSB) Speakers.Add(speaker);
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (connect)
        {
            _ = ConnectAsync(speaker);
        }
        return speaker;
    }

    /// <summary>GUI"添加"按钮：默认取本机物理网段（windows 端固定 192.168.1.x，Linux 适配真实网段）。</summary>
    public Speaker? AddNextIPSpeaker()
    {
        var primary = NetworkInterfaceFilter.PrimaryPhysicalAddress();
        var prefix = primary == null
            ? "192.168.1"
            : $"{primary.GetAddressBytes()[0]}.{primary.GetAddressBytes()[1]}.{primary.GetAddressBytes()[2]}";
        for (var host = 2; host < 255; host++)
        {
            var id = $"{prefix}.{host}:{Protocol.DefaultTcpPort}";
            if (!_settings.IPDevices.Any(d => d.Id == id) && Speakers.All(s => s.Id != id))
            {
                return AddIPSpeaker($"{prefix}.{host}", Protocol.DefaultTcpPort, persist: true, connect: false);
            }
        }
        return null;
    }

    public void RemoveSpeaker(Speaker speaker)
    {
        speaker.Dispose();
        Speakers.Remove(speaker);
        ResetSpeakerSettings();
        _settings.Save();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把当前设备列表与声道写回设置（windows/Model.cs:424-442 ResetSpeakerSetting）。</summary>
    public void ResetSpeakerSettings()
    {
        if (_settings.IsUSB)
        {
            _settings.AdbDevices = [.. Speakers.Select(s => new SettingsStore.DeviceEntry(s.Id, s.Channel))];
        }
        else
        {
            _settings.IPDevices = [.. Speakers.Select(s => new SettingsStore.DeviceEntry(s.Id, s.Channel))];
        }
    }

    private void OnSpeakerStatusChanged(object? sender, ConnectStatus status)
    {
        if (status == ConnectStatus.Connected)
        {
            EnsureEngineRunning();
        }
        else if (status == ConnectStatus.UnConnected)
        {
            StopEngineIfIdle();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>启动时自动连接（windows/MainWindow.xaml.cs:204-212）并返回是否有设备连接成功。</summary>
    public async Task<bool> ConnectSavedDevicesAsync(CancellationToken token = default)
    {
        await RefreshSpeakersAsync(token).ConfigureAwait(false);
        var targets = Speakers.Where(s => s.UnConnected && s.Channel != AudioChannel.None).ToList();
        if (targets.Count == 0) return false;
        var results = await Task.WhenAll(targets.Select(s => ConnectAsync(s, token))).ConfigureAwait(false);
        return results.Any(r => r);
    }

    /// <summary>音频帧长（毫秒），修改后立即生效。</summary>
    public int FrameMs
    {
        get => _settings.FrameMs;
        set
        {
            if (_settings.FrameMs == value) return;
            _settings.FrameMs = value;
            foreach (var speaker in Speakers) speaker.FrameMilliseconds = value;
            _settings.Save();
            RestartEngine();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public AudioStats Stats() => Engine.Stats();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeatTimer?.Dispose();
        _volumeTimer?.Dispose();
        _volumeSendTimer?.Dispose();
        _discovery?.Dispose();
        foreach (var speaker in Speakers.ToArray()) speaker?.Dispose();
        Speakers.Clear();
        Engine.Dispose();
        _settings.Save();
    }
}
