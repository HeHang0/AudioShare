using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Headless;
using Avalonia.Platform;
using AudioShare;

namespace AudioShare.Gui;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 无头渲染模式：加载并渲染 MainView 到 PNG（用于无人值守的 GUI 验证，不创建真实窗口）
        if (Program.ScreenshotMode)
        {
            ScreenshotHost.Render(this);
            return;
        }


        SetupTrayIcon();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (Environment.GetEnvironmentVariable("AUDIOSHARE_START_MINIMIZED") != "1")
            {
                window.Show();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 托盘图标（等价 windows 的 NotifyIcon）：用代码创建，避免 XAML 编译器对 TrayIcon 内容属性的限制。
    /// 单击/菜单"显示窗口"唤出窗口，"退出"结束进程。
    /// </summary>
    private void SetupTrayIcon()
    {
        try
        {
            var tray = new TrayIcon
            {
                ToolTipText = "AudioShare 音频同享",
                IsVisible = true,
            };
            var icon = LoadIcon();
            if (icon != null) tray.Icon = icon;

            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("显示窗口");
            showItem.Click += (_, _) => MainWindow.Current?.ShowFromTray();
            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += async (_, _) =>
            {
                if (MainWindow.Current != null) await MainWindow.Current.ExitAsync().ConfigureAwait(false);
            };
            menu.Items.Add(showItem);
            menu.Items.Add(exitItem);
            tray.Menu = menu;
            tray.Clicked += (_, _) => MainWindow.Current?.ShowFromTray();

            TrayIcon.SetIcons(this, new TrayIcons { tray });
            Logger.Info("tray icon created");
        }
        catch (Exception ex)
        {
            Logger.Warning($"tray icon unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            var uri = new Uri("avares://AudioShare/Assets/audioshare.ico");
            using var stream = AssetLoader.Open(uri);
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Logger.Warning($"tray icon asset load failed: {ex.Message}");
            return null;
        }
    }
}

public static class Program
{
    /// <summary>是否处于无头渲染模式（--screenshot &lt;path&gt; [数据目录]）。</summary>
    public static bool ScreenshotMode { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        Logger.Init(console: true);
        Logger.Info($"AudioShare GUI starting: args={string.Join(' ', args)}");
        ScreenshotMode = args.Contains("--screenshot");
        if (ScreenshotMode)
        {
            // 无头验证不需要托盘/单实例
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // 单实例（等价 windows/App.xaml.cs 的命名互斥 + 命名管道唤醒）
        if (!SingleInstance.TryAcquire(out var listener))
        {
            SingleInstance.SignalExistingInstance();
            Console.WriteLine("AudioShare 已在运行，已唤醒现有窗口。");
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            listener?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().WithInterFont().LogToTrace();
        if (!ScreenshotMode) return builder.UsePlatformDetect();
        // 无头模式需要真实渲染后端（Skia）才能把可视树渲染成 PNG
        return builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
