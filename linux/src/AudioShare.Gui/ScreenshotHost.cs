using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AudioShare;

namespace AudioShare.Gui;

/// <summary>
/// 无头渲染：把 MainView（与真实窗口同一套 XAML）渲染成 PNG，用于无人值守的 GUI 验证。
/// 用法： AudioShare --screenshot /path/out.png [XDG_CONFIG_HOME]
/// 实现基于 Avalonia 官方无头渲染 API（HeadlessWindowExtensions.CaptureRenderedFrame），
/// 渲染的是真实窗口的可视树，因此截图内容与桌面运行一致。
/// </summary>
public static class ScreenshotHost
{
    public static void Render(Application app)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--screenshot");
        var target = index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Path.Combine(AppPaths.BaseDirectory, "audioshare-window.png");
        var configOverride = index >= 0 && index + 2 < args.Length ? args[index + 2] : null;
        if (!string.IsNullOrEmpty(configOverride))
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configOverride);
        }

        try
        {
            var viewModel = new MainWindowViewModel(initialize: false);
            var window = new Window
            {
                Width = 500,
                Height = 470,
                SystemDecorations = SystemDecorations.None,
                CanResize = false,
                Content = new MainView { DataContext = viewModel },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var textCount = window.GetVisualDescendants().OfType<TextBlock>().Count(b => !string.IsNullOrWhiteSpace(b.Text));
            Logger.Info($"layout: window={window.Bounds.Width}x{window.Bounds.Height} textBlocks={textCount}");
            foreach (var block in window.GetVisualDescendants().OfType<TextBlock>().Where(b => !string.IsNullOrWhiteSpace(b.Text)).Take(6))
            {
                Logger.Info($"  text: {block.Text} @{block.Bounds.X:0},{block.Bounds.Y:0}");
            }
            // 控件坐标诊断：任何控件右/下边界超出窗口即为显示不全（例如被 ScrollViewer 无限测量撑宽）
            foreach (var c in window.GetVisualDescendants().OfType<Control>().Where(x => x is Button or ComboBox or ToggleSwitch))
            {
                var p = c.TranslatePoint(new Avalonia.Point(0, 0), window) ?? default;
                var overflow = p.X + c.Bounds.Width > window.Bounds.Width + 0.5
                               || p.Y + c.Bounds.Height > window.Bounds.Height + 0.5;
                Logger.Info($"  ctrl: {c.GetType().Name} '{(c as Button)?.Content}' @({p.X:0},{p.Y:0}) " +
                            $"size={c.Bounds.Width:0}x{c.Bounds.Height:0}{(overflow ? " <-- OUTSIDE WINDOW" : string.Empty)}");
            }

            var frame = window.CaptureRenderedFrame();
            if (frame == null)
            {
                Logger.Error("screenshot: CaptureRenderedFrame returned null");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            frame.Save(target);
            Logger.Info($"screenshot written: {target} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
            Console.WriteLine($"screenshot: {target}");
        }
        catch (Exception ex)
        {
            Logger.Error($"screenshot failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("screenshot failed: " + ex);
        }
    }
}
