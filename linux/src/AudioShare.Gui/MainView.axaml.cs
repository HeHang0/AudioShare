using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AudioShare;

namespace AudioShare.Gui;

/// <summary>
/// 主界面内容（与 MainWindow 分离，便于无头渲染截图验证与复用）。
/// 精简版布局：单一刷新、连接/断开合并按钮、双击设备行打开管理页。
/// </summary>
public sealed partial class MainView : UserControl
{
    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>连接/断开合并按钮：按当前连接状态切换动作。</summary>
    private void OnToggleConnect(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || ViewModel == null) return;
        if (row.Connected) _ = ViewModel.DisconnectAsync(row);
        else _ = ViewModel.ConnectAsync(row);
    }

    private void OnRemoveSpeaker(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ViewModel?.RemoveSpeaker(row);
    }

    /// <summary>设备行双击：打开安卓端远程管理页（隐藏入口，替代原来的按钮）。</summary>
    private void OnOpenRemotePage(object? sender, TappedEventArgs e)
    {
        if (RowOf(sender) is { } row && !string.IsNullOrEmpty(row.Model.HttpAddress))
        {
            ExternalLauncher.OpenUrl(row.Model.HttpAddress!);
        }
    }

    /// <summary>添加设备：输入框有 IP 时添加该 IP；留空时按本机物理网段自动添加下一台。</summary>
    private void OnAddSpeaker(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        if (!string.IsNullOrWhiteSpace(ViewModel.NewIp)) ViewModel.AddIpSpeaker();
        else ViewModel.AddNextIpSpeaker();
    }

    private static SpeakerRow? RowOf(object? sender) => (sender as Control)?.DataContext as SpeakerRow;
}
