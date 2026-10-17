using System.Net.Sockets;

namespace AudioShare.Gui;

/// <summary>
/// 单实例控制（等价 windows/App.xaml.cs 的 Mutex + MainWindow.xaml.cs 的 NamedPipeServerStream）：
/// 先尝试绑定 Unix domain socket；若已存在则连上去发一个字节唤醒现有实例。
/// </summary>
public static class SingleInstance
{
    private static Socket? _listener;

    public static bool TryAcquire(out IDisposable? handle)
    {
        handle = null;
        var path = AppPaths.SingleInstanceSocket;
        try
        {
            // 先尝试连接：能连上说明已有实例在运行（注意不能先 unlink —— 删除路径后仍可重复 bind 到同一路径，
            // 那样单实例判定会失效）。连接失败（ECONNREFUSED/ENOENT）才视为陈旧 socket 并清理。
            if (File.Exists(path) && IsAlive(path)) return false;
            if (File.Exists(path)) File.Delete(path);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            socket.Listen(4);
            _listener = socket;
            _ = Task.Run(AcceptLoopAsync);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Info($"single instance acquire failed ({ex.GetType().Name}), assuming another instance runs");
            return false;
        }
    }

    /// <summary>探测已有实例的 socket 是否仍在监听（能连上=有实例）。</summary>
    private static bool IsAlive(string path)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task AcceptLoopAsync()
    {
        var listener = _listener;
        if (listener == null) return;
        while (true)
        {
            try
            {
                var client = await listener.AcceptAsync().ConfigureAwait(false);
                client.Dispose();
                Logger.Info("received wake signal from another instance");
                MainWindow.Current?.ShowFromTray();
            }
            catch (Exception ex)
            {
                Logger.Warning("single instance accept loop ended: " + ex.Message);
                return;
            }
        }
    }

    public static void SignalExistingInstance()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(AppPaths.SingleInstanceSocket));
            socket.Send([0x01]);
        }
        catch (Exception ex)
        {
            // 对端收到信号后会立即关闭连接，因此这里的断开异常属正常现象（首个实例日志中可见 wake signal）
            Logger.Info("signal delivered (peer closed: " + ex.Message + ")");
        }
    }
}

/// <summary>打开外部页面/程序（Linux 用 xdg-open）。</summary>
public static class ExternalLauncher
{
    public static void OpenUrl(string url)
    {
        try
        {
            var opener = ProcessRunner.Which("xdg-open");
            if (opener == null)
            {
                Logger.Warning("xdg-open not found; cannot open " + url);
                return;
            }
            var info = new System.Diagnostics.ProcessStartInfo(opener) { UseShellExecute = false };
            info.ArgumentList.Add(url);
            System.Diagnostics.Process.Start(info);
            Logger.Info("opened " + url);
        }
        catch (Exception ex)
        {
            Logger.Warning($"open {url} failed: {ex.Message}");
        }
    }
}
