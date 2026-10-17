using System.Collections.ObjectModel;
using Avalonia.Threading;
using AudioShare;

namespace AudioShare.Gui;

/// <summary>
/// 主窗口视图模型。字段与行为对应 windows/Model.cs：
/// AudioDevices/AudioSelected/SampleRate/Speakers/IsUSB/Volume/VolumeFollowSystem/IsStartup，
/// 以及 RefreshAudios / RefreshSpeakers / AddIPSpeaker / 自动连接等命令与流程。
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DevicePool _pool = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _initialized;
    private bool _disposed;

    public MainWindowViewModel(bool initialize = true)
    {
        if (!initialize)
        {
            Speakers = [];
            AudioDevices = [];
            SampleRates = [.. _pool.SampleRates.Select(rate => new SampleRateChoice(rate, FormatRate(rate)))];
            RefreshAudiosCommand = new RelayCommand(_ => { });
            RefreshSpeakersCommand = new RelayCommand(_ => { });
            RefreshAllCommand = new RelayCommand(_ => { });
            AddIPSpeakerCommand = new RelayCommand(_ => { });
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            Volume = _pool.Volume;
            IsStartup = Autostart.IsEnabled();
            SelectedSampleRate = _pool.SampleRate;
            StatusLine = "（无头渲染预览，未初始化后台任务）";
            return;
        }

        _pool.Start();
        _pool.StateChanged += (_, _) => Dispatcher.UIThread.Post(SyncFromPool);
        _pool.SpeakerDisconnected += (_, speaker) => Dispatcher.UIThread.Post(() => StatusLine = $"设备已断开：{speaker.Display}（可在列表中重新连接）");
        _pool.DeviceDiscovered += (_, device) => Dispatcher.UIThread.Post(() => StatusLine = $"发现设备 {device.Id}（管理页 {device.HttpUrl}）");

        Speakers = [];
        AudioDevices = [];
        SampleRates = [.. _pool.SampleRates.Select(rate => new SampleRateChoice(rate, FormatRate(rate)))];

        RefreshAudiosCommand = new RelayCommand(_ => _ = RefreshAudiosAsync());
        RefreshSpeakersCommand = new RelayCommand(_ => _ = RefreshSpeakersAsync());
        RefreshAllCommand = new RelayCommand(_ => _ = RefreshAllAsync());
        AddIPSpeakerCommand = new RelayCommand(_ => AddIpSpeaker(), _ => IsIP);

        Volume = _pool.Volume;
        VolumeFollowSystem = _pool.VolumeFollowSystem;
        IsUSB = _pool.IsUSB;
        IsStartup = Autostart.IsEnabled();
        SelectedSampleRate = _pool.SampleRate;
        StatusLine = "正在初始化…";

        // 每秒刷新行内的统计信息（帧数/丢帧/HTTP 地址），避免绑定静态快照
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshRows();
        _refreshTimer.Start();

        SyncFromPool();
    }

    public ObservableCollection<SpeakerRow> Speakers { get; }

    public ObservableCollection<OutputDevice> AudioDevices { get; }

    public ObservableCollection<SampleRateChoice> SampleRates { get; }

    public RelayCommand RefreshAudiosCommand { get; }

    public RelayCommand RefreshSpeakersCommand { get; }

    /// <summary>单一"刷新"按钮：一次刷新音频采集设备与播放设备列表。</summary>
    public RelayCommand RefreshAllCommand { get; }

    public RelayCommand AddIPSpeakerCommand { get; }

    public bool AnyConnected => _pool.ConnectedCount > 0;

    public bool NoneConnected => !AnyConnected;

    public string ConnectedCountText => $"[ {_pool.ConnectedCount} ]";

    public bool AdbLoading { get; private set; }

    public bool IsUSB
    {
        get => _pool.IsUSB;
        set
        {
            if (_pool.IsUSB == value) return;
            _pool.IsUSB = value;
            Raise();
            Raise(nameof(IsIP));
            Raise(nameof(AddIPSpeakerVisible));
            SyncFromPool();
        }
    }

    public bool IsIP => !IsUSB;

    public bool AddIPSpeakerVisible => !IsUSB;

    public OutputDevice? AudioSelected
    {
        get => AudioDevices.FirstOrDefault(d => d.MonitorSource == _pool.AudioId || (string.IsNullOrEmpty(_pool.AudioId) && d.IsDefault))
            ?? AudioDevices.FirstOrDefault(d => d.IsDefault);
        set
        {
            if (value == null) return;
            _ = SelectAudioAsync(value);
        }
    }

    private async Task SelectAudioAsync(OutputDevice device)
    {
        await _pool.SelectAudioDeviceAsync(device.MonitorSource).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            Raise(nameof(AudioSelected));
            StatusLine = $"采集设备已切换：{device.Name}（monitor={device.MonitorSource}）";
        });
    }

    public int SelectedSampleRate
    {
        get => _pool.SampleRate;
        set
        {
            if (_pool.SampleRate == value) return;
            _pool.SampleRate = value;
            Raise();
            var connectedCount = _pool.ConnectedCount;
            StatusLine = connectedCount > 0
                ? $"采样率已切换为 {value} Hz，正在对 {connectedCount} 台已连接设备重新握手"
                : $"采样率已切换为 {value} Hz";
            RefreshRows();
        }
    }

    public SampleRateChoice? SampleRateChoice
    {
        get => SampleRates.FirstOrDefault(r => r.Rate == SelectedSampleRate);
        set
        {
            if (value == null) return;
            SelectedSampleRate = value.Rate;
            Raise();
        }
    }

    private int _volume;
    public int Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value)) return;
            _pool.Volume = value;
        }
    }

    public bool VolumeFollowSystem
    {
        get => _pool.VolumeFollowSystem;
        set
        {
            if (_pool.VolumeFollowSystem == value) return;
            _pool.VolumeFollowSystem = value;
            Raise();
            Raise(nameof(VolumeCustom));
            StatusLine = value ? "音量跟随系统（读取默认输出的音量）" : "音量手动设置";
        }
    }

    public bool VolumeCustom => !VolumeFollowSystem;

    private bool _isStartup;
    public bool IsStartup
    {
        get => _isStartup;
        set
        {
            if (!Set(ref _isStartup, value)) return;
            Autostart.Set(value);
            StatusLine = value ? $"已启用开机启动（{AppPaths.AutostartFile}）" : "已关闭开机启动";
        }
    }

    private string _newIp = string.Empty;
    public string NewIp
    {
        get => _newIp;
        set => Set(ref _newIp, value);
    }

    private string _statusLine = string.Empty;
    public string StatusLine
    {
        get => _statusLine;
        set => Set(ref _statusLine, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var profile = await Task.Run(() => Profile(string.IsNullOrEmpty(_pool.AudioId) ? "@DEFAULT_MONITOR@" : _pool.AudioId)).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => StatusLine = $"采集源：{profile}");
            await RefreshAudiosAsync().ConfigureAwait(false);
            var connected = await _pool.ConnectSavedDevicesAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                StatusLine = connected
                    ? $"已自动连接 {_pool.ConnectedCount} 台设备"
                    : "等待连接设备（USB 下拉刷新或 Wi-Fi 自动发现）";
                if (connected)
                {
                    Logger.Info("auto connect succeeded; keep window visible for user confirmation");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("initialize failed: " + ex.Message);
            Dispatcher.UIThread.Post(() => StatusLine = "初始化失败: " + ex.Message);
        }
    }

    public async Task RefreshAudiosAsync()
    {
        await _pool.RefreshAudioDevicesAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            AudioDevices.Clear();
            foreach (var device in _pool.AudioDevices) AudioDevices.Add(device);
            Raise(nameof(AudioSelected));
            StatusLine = $"已枚举 {AudioDevices.Count} 个输出设备（monitor 采集源）";
        });
    }

    /// <summary>
    /// 合并刷新：音频采集设备 + 播放设备列表。
    /// 界面上只保留一个"刷新"按钮，用户无需区分两种刷新的差别。
    /// </summary>
    public async Task RefreshAllAsync()
    {
        await RefreshAudiosAsync().ConfigureAwait(false);
        await RefreshSpeakersAsync().ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
            StatusLine = $"已刷新：{AudioDevices.Count} 个输出设备 / {Speakers.Count} 台播放设备");
    }

    public async Task RefreshSpeakersAsync()
    {
        Dispatcher.UIThread.Post(() =>
        {
            AdbLoading = true;
            Raise(nameof(AdbLoading));
        });
        try
        {
            await _pool.RefreshSpeakersAsync().ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                AdbLoading = false;
                Raise(nameof(AdbLoading));
                SyncFromPool();
                StatusLine = !_pool.Adb.Available
                    ? "未找到 adb，USB 模式不可用（请安装 android-tools-adb）"
                    : !IsUSB
                        ? $"Wi-Fi 设备列表已刷新（{Speakers.Count} 台）"
                        : _pool.WirelessAdbSerials.Count > 0
                            ? $"USB 设备 {Speakers.Count} 台。无线调试设备 {string.Join("、", _pool.WirelessAdbSerials)} 不是 USB，已加入 Wi-Fi 列表（关闭 USB 开关后连接）"
                            : $"USB 设备列表已刷新（{Speakers.Count} 台）";
            });
        }
    }

    public async Task ConnectAsync(SpeakerRow row)
    {
        StatusLine = $"正在连接 {row.Display} …";
        var ok = await _pool.ConnectAsync(row.Model).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            row.Refresh();
            SyncFromPool();
            StatusLine = ok ? $"{row.Display} 已连接" : $"{row.Display} 连接失败：{row.Model.LastError ?? "未知错误"}";
        });
    }

    public async Task DisconnectAsync(SpeakerRow row)
    {
        await _pool.DisconnectAsync(row.Model).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            row.Refresh();
            SyncFromPool();
            StatusLine = $"{row.Display} 已断开";
        });
    }

    public void RemoveSpeaker(SpeakerRow row)
    {
        var display = row.Display;
        _pool.RemoveSpeaker(row.Model);
        SyncFromPool();
        StatusLine = $"已移除 {display}";
    }

    /// <summary>添加 IP 设备（GUI 显式输入，支持 IP 或 IP:端口）。</summary>
    public void AddIpSpeaker()
    {
        var text = (NewIp ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
        {
            StatusLine = "请输入设备 IP（默认端口 8088）";
            return;
        }
        var probe = new Speaker(text);
        if (!probe.TryParseAddress(out var ip, out var port))
        {
            StatusLine = $"IP 地址解析错误：{text}（对应 windows 的 ipParseError 提示）";
            return;
        }
        var speaker = _pool.AddIPSpeaker(ip, port, Protocol.DefaultHttpPort, persist: true, connect: false);
        NewIp = string.Empty;
        SyncFromPool();
        StatusLine = $"已添加 {speaker.Id}（选择声道后点击连接）";
    }

    /// <summary>按本机物理网段自动填入下一台设备（windows 添加按钮的 Linux 版）。</summary>
    public void AddNextIpSpeaker()
    {
        var speaker = _pool.AddNextIPSpeaker();
        SyncFromPool();
        StatusLine = speaker == null
            ? "本网段地址已用尽"
            : $"已添加 {speaker.Id}（按本机物理网段 {NetworkInterfaceFilter.PrimaryPhysicalAddress()} 生成）";
    }

    private void SyncFromPool()
    {
        // 只增删差集，保持已有行的引用（避免 ComboBox 选择状态被重置）
        var poolIds = _pool.Speakers.Select(s => s.Id).ToHashSet();
        for (var i = Speakers.Count - 1; i >= 0; i--)
        {
            if (!poolIds.Contains(Speakers[i].Id)) Speakers.RemoveAt(i);
        }
        foreach (var speaker in _pool.Speakers)
        {
            var row = Speakers.FirstOrDefault(r => r.Id == speaker.Id);
            if (row == null)
            {
                Speakers.Add(new SpeakerRow(speaker));
            }
            else
            {
                row.Refresh();
            }
        }

        // 手动模式下音量以用户拖动值为准（避免后台状态同步把滑块拉回旧值，造成"不跟手"）；
        // 跟随系统时由系统音量驱动显示。
        if (VolumeFollowSystem && _volume != _pool.Volume)
        {
            _volume = _pool.Volume;
            Raise(nameof(Volume));
        }
        Raise(nameof(VolumeFollowSystem));
        Raise(nameof(VolumeCustom));
        Raise(nameof(IsUSB));
        Raise(nameof(IsIP));
        Raise(nameof(AddIPSpeakerVisible));
        Raise(nameof(SelectedSampleRate));
        Raise(nameof(SampleRateChoice));
        Raise(nameof(AnyConnected));
        Raise(nameof(NoneConnected));
        Raise(nameof(ConnectedCountText));

        if (!string.IsNullOrEmpty(_pool.AudioId))
        {
            var selected = AudioDevices.FirstOrDefault(d => d.MonitorSource == _pool.AudioId);
            StatusLine = selected == null ? $"采集源：{_pool.AudioId}" : $"采集源：{selected.Name}（{selected.MonitorSource}）";
        }
    }

    private void RefreshRows()
    {
        foreach (var row in Speakers) row.Refresh();
        Raise(nameof(ConnectedCountText));
        Raise(nameof(AnyConnected));
        Raise(nameof(NoneConnected));
    }

    private static string Profile(string monitor)
    {
        var state = AudioEngine.QuerySourceStateAsync(monitor).GetAwaiter().GetResult();
        return $"{monitor} · {state}";
    }

    private static string FormatRate(int rate) => rate switch
    {
        176400 => "176.4kHz",
        44100 => "44.1kHz",
        _ => $"{rate / 1000}kHz",
    };

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _pool.ResetSpeakerSettings();
        _pool.Settings.Save();
        await Task.Run(() => _pool.Dispose()).ConfigureAwait(false);
        Logger.Info("view model shut down");
    }

    public void Dispose() => ShutdownAsync().GetAwaiter().GetResult();
}
