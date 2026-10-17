using System.ComponentModel;
using System.Runtime.CompilerServices;
using AudioShare;

namespace AudioShare.Gui;

/// <summary>极简命令实现（等价 windows/RelayCommand.cs）。</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed record ChannelChoice(AudioChannel Channel, string Name);

public sealed record SampleRateChoice(int Rate, string Name);

/// <summary>
/// 单台设备的 UI 行模型。属性与 windows/Speaker.cs 的绑定项一一对应：
/// Display / IdReadOnly / RemoveVisible / ChannelEnabled / ConnectEnabled / Connected / Connecting / UnConnected。
/// </summary>
public sealed class SpeakerRow : ObservableObject
{
    private readonly Speaker _speaker;

    public SpeakerRow(Speaker speaker)
    {
        _speaker = speaker;
        speaker.StatusChanged += (_, _) => Refresh();
        Refresh();
    }

    public Speaker Model => _speaker;

    public string Id => _speaker.Id;

    public string Display
    {
        get => _speaker.Display;
        set
        {
            if (_speaker.Display == value) return;
            _speaker.SetDisplayForUi(value);
            Raise();
        }
    }

    public bool IdReadOnly => _speaker.IsUsb || !_speaker.UnConnected;

    public bool RemoveVisible => !_speaker.IsUsb;

    public bool ChannelEnabled => _speaker.UnConnected;

    public bool ConnectEnabled => _speaker.Channel != AudioChannel.None;

    public bool Connected => _speaker.Connected;

    public bool Connecting => _speaker.Connecting;

    public bool UnConnected => _speaker.UnConnected;

    /// <summary>可选声道（等价 windows/Speaker.cs 的静态 Channels 集合：立体声/左/右/禁用）。</summary>
    public static ChannelChoice[] Channels { get; } =
    [
        new(AudioChannel.Stereo, "立体声"),
        new(AudioChannel.Left, "左声道"),
        new(AudioChannel.Right, "右声道"),
        new(AudioChannel.None, "禁用"),
    ];

    /// <summary>ComboBox 选中项（等价 windows 的 NamePair 选择，含 Key=AudioChannel）。</summary>
    public ChannelChoice ChannelChoice
    {
        get => Channels.FirstOrDefault(c => c.Channel == _speaker.Channel) ?? Channels[^1];
        set
        {
            if (value == null || _speaker.Channel == value.Channel) return;
            _speaker.Channel = value.Channel;
            Raise();
            Raise(nameof(ChannelChoice));
            Raise(nameof(ConnectEnabled));
        }
    }

    public AudioChannel Channel
    {
        get => _speaker.Channel;
        set
        {
            if (_speaker.Channel == value) return;
            _speaker.Channel = value;
            Raise();
            Raise(nameof(ChannelChoice));
            Raise(nameof(ConnectEnabled));
        }
    }

    public string StatusText => _speaker.Status switch
    {
        ConnectStatus.Connected => "已连接",
        ConnectStatus.Connecting => "连接中",
        _ => "未连接",
    };

    /// <summary>设备行状态文本（状态 + 统计合并为一行，替代原来的状态行/详情行两行）。</summary>
    public string StatusDetail
    {
        get
        {
            if (_speaker.Connecting) return "连接中…";
            if (_speaker.Connected) return $"已连接 · 发送 {_speaker.FramesSent} 帧 / 丢帧 {_speaker.FramesDropped}";
            return _speaker.LastError == null ? "未连接" : $"未连接 · {_speaker.LastError}";
        }
    }

    /// <summary>连接/断开合并为一个按钮：按当前状态显示对应动作。</summary>
    public string ActionText => _speaker.Connected ? "断开" : "连接";

    /// <summary>连接中显示进度条而不是按钮。</summary>
    public bool ShowActionButton => !_speaker.Connecting && (_speaker.Connected || _speaker.UnConnected);

    public string OpenPageText => string.IsNullOrEmpty(_speaker.HttpAddress) ? "打开管理页" : $"打开 {_speaker.HttpAddress}";

    public bool CanOpenPage => !string.IsNullOrEmpty(_speaker.HttpAddress);

    public void Refresh()
    {
        Raise(nameof(Display));
        Raise(nameof(IdReadOnly));
        Raise(nameof(RemoveVisible));
        Raise(nameof(ChannelEnabled));
        Raise(nameof(ConnectEnabled));
        Raise(nameof(Connected));
        Raise(nameof(Connecting));
        Raise(nameof(UnConnected));
        Raise(nameof(ChannelChoice));
        Raise(nameof(StatusText));
        Raise(nameof(StatusDetail));
        Raise(nameof(ActionText));
        Raise(nameof(ShowActionButton));
        Raise(nameof(OpenPageText));
        Raise(nameof(CanOpenPage));
    }
}
