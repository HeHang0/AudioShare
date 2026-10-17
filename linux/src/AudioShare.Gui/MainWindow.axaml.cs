using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AudioShare;

namespace AudioShare.Gui;

/// <summary>
/// 主窗口。窗口级行为对应 windows/MainWindow.xaml.cs：
/// 关闭即最小化到托盘（Closing → Hide）、托盘/单实例唤醒显示、启动时刷新设备并自动连接已保存设备。
/// </summary>
public sealed partial class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }

    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Current = this;
        ViewModel = new MainWindowViewModel();
        DataContext = ViewModel;
        Closing += (_, e) =>
        {
            // 对应 windows/MainWindow_Closing：取消关闭并最小化到托盘，音频转发继续
            e.Cancel = true;
            MinimizeToTray();
        };
    }

    /// <summary>布局设计尺寸（DIP）：窗口按此尺寸设计，低于它内容会被压缩/截断。</summary>
    private const double DesignWidth = 500;
    private const double DesignHeight = 470;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        LogScalingDiagnostics();
        AdjustWindowSizeForScaling();
        // 部分 Wayland 合成器在首帧前 ClientSize 仍为 0，Loaded 时再确认一次
        Dispatcher.UIThread.Post(AdjustWindowSizeForScaling, DispatcherPriority.Loaded);
        // 布局完成后输出一次控件坐标（排查错位/截断；DEBUG 级日志，正常不打扰）
        var dumpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        dumpTimer.Tick += (_, _) =>
        {
            dumpTimer.Stop();
            DumpLayoutDiagnostics();
        };
        dumpTimer.Start();

        if (Environment.GetEnvironmentVariable("AUDIOSHARE_START_MINIMIZED") == "1") MinimizeToTray();
        _ = ViewModel.InitializeAsync();
    }

    /// <summary>
    /// Wayland 分数缩放兼容：部分合成器（如 125%）会把窗口的 DIP 尺寸按物理像素处理后
    /// 再按缩放比换算逻辑空间（500 DIP 设计宽只剩 400 DIP 可用），导致控件被压缩、
    /// 按钮文字显示不全。这里检测实际逻辑尺寸并按比例补偿窗口大小，
    /// 保证布局始终获得设计宽度；平台已正确处理 DIP 时不做任何改动。
    /// </summary>
    private void AdjustWindowSizeForScaling()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var scaleX = DesignWidth / ClientSize.Width;
        var scaleY = DesignHeight / ClientSize.Height;
        if (scaleX <= 1.02 && scaleY <= 1.02) return; // 逻辑尺寸已达设计值（或更大）
        Width = Math.Round(DesignWidth * Math.Max(scaleX, 1.0));
        Height = Math.Round(DesignHeight * Math.Max(scaleY, 1.0));
        Logger.Info($"window size compensated for fractional scaling: " +
                    $"client={ClientSize.Width:0.#}x{ClientSize.Height:0.#} -> {Width:0}x{Height:0}");
    }

    /// <summary>
    /// 布局诊断：输出所有按钮/下拉框/输入框的实际坐标与期望尺寸。
    /// 若某控件 size &lt; desired，说明被容器压缩（显示不全）；坐标重叠则说明错位。
    /// </summary>
    private void DumpLayoutDiagnostics()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"window client={ClientSize.Width:0.#}x{ClientSize.Height:0.#} render={RenderScaling:0.##} pos={Position}");
            foreach (var c in this.GetVisualDescendants().OfType<Control>())
            {
                if (c is not (Button or ComboBox or Slider or ToggleSwitch or TextBox)) continue;
                var content = c switch
                {
                    Button b => b.Content?.ToString(),
                    TextBox tb => tb.Text,
                    _ => null,
                };
                var p = c.TranslatePoint(new Point(0, 0), this) ?? default;
                var flag = c.Bounds.Width + 0.5 < c.DesiredSize.Width || c.Bounds.Height + 0.5 < c.DesiredSize.Height
                    ? " <-- COMPRESSED" : string.Empty;
                sb.AppendLine($"  {c.GetType().Name} '{content}' at ({p.X:0.#},{p.Y:0.#}) " +
                              $"size={c.Bounds.Width:0.#}x{c.Bounds.Height:0.#} desired={c.DesiredSize.Width:0.#}x{c.DesiredSize.Height:0.#}{flag}");
            }
            Logger.Info("layout dump:\n" + sb);
        }
        catch (Exception ex)
        {
            Logger.Warning("layout dump failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 高 DPI 诊断：记录平台缩放、屏幕缩放、渲染缩放与窗口尺寸。
    /// 用于排查 Wayland 分数缩放（125%/150%）下窗口尺寸与被压缩布局的问题。
    /// </summary>
    private void LogScalingDiagnostics()
    {
        try
        {
            var screen = Screens.Primary;
            Logger.Info($"scaling diag: session={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")} " +
                        $"screen={screen?.Scaling:0.###} render={RenderScaling:0.###} " +
                        $"client={ClientSize.Width:0.#}x{ClientSize.Height:0.#} bounds={Bounds.Width:0.#}x{Bounds.Height:0.#} " +
                        $"pixels={ClientSize.Width * RenderScaling:0}x{ClientSize.Height * RenderScaling:0} " +
                        $"screenPixels={screen?.Bounds.Width:0}x{screen?.Bounds.Height:0}");
        }
        catch (Exception ex)
        {
            Logger.Warning("scaling diag failed: " + ex.Message);
        }
    }

    public void ShowFromTray()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void MinimizeToTray()
    {
        Hide();
        Logger.Info("window minimized to tray");
    }

    /// <summary>标题栏拖动窗口（SystemDecorations=None 后需自绘拖动，对应系统标题栏的移动行为）。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        try
        {
            BeginMoveDrag(e);
        }
        catch (InvalidOperationException)
        {
            // 平台/无头模式下不支持拖动时忽略
        }
    }

    private void OnMinimizeToTray(object? sender, RoutedEventArgs e) => MinimizeToTray();

    public async Task ExitAsync()
    {
        Logger.Info("exiting application");
        await ViewModel.ShutdownAsync().ConfigureAwait(false);
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
