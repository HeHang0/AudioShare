namespace AudioShare;

public sealed record AdbDevice(string Serial, string State, string Model, string Product)
{
    public string Display => string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model} [{Serial}]";

    /// <summary>无线调试（adb over Wi-Fi，序列号形如 192.168.8.4:5555）。USB 设备的序列号不含冒号。</summary>
    public bool IsWireless => Serial.Contains(':', StringComparison.Ordinal);
}

/// <summary>
/// 系统原生 adb 调用封装。对应 windows/Speaker.cs + windows/Model.cs + windows/Utils.cs 中
/// SharpAdbClient 的使用（Linux 侧改为调用系统 adb 命令行，命令序列保持一致）。
/// 所有调用都会把完整命令行与退出码写入日志（验收标准 6）。
/// </summary>
public sealed class AdbController
{
    public const string RemoteSocket = "localabstract:picapico-audio-share";

    /// <summary>adb 路径：优先环境变量 AUDIOSHARE_ADB，其次 PATH，最后常见位置。</summary>
    public string? AdbPath { get; } = ProcessRunner.Which("adb");

    public bool Available => AdbPath != null;

    public AdbController(string? adbPath = null)
    {
        var configured = adbPath ?? Environment.GetEnvironmentVariable("AUDIOSHARE_ADB");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            AdbPath = configured;
        }
    }

    public List<string> Log { get; } = [];

    private async Task<ProcessRunner.Result> InvokeAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        var adb = AdbPath ?? throw new FileNotFoundException("adb not found; install android-tools-adb or android-sdk platform-tools");
        var commandLine = $"adb {string.Join(' ', arguments)}";
        Logger.Info($"exec: {commandLine}");
        var result = await ProcessRunner.RunAsync(adb, arguments, timeout, null, token).ConfigureAwait(false);
        Log.Add($"{commandLine} -> exit={result.ExitCode}");
        Logger.Info($"exit={result.ExitCode} stdout={(result.StandardOutput.Trim().Length > 4096 ? result.StandardOutput.Trim()[..4096] + "..." : result.StandardOutput.Trim())}");
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            Logger.Warning($"stderr={result.StandardError.Trim()}");
        }
        return result;
    }

    public Task<ProcessRunner.Result> StartServerAsync(CancellationToken token = default)
        => InvokeAsync(["start-server"], TimeSpan.FromSeconds(20), token);

    public async Task<List<AdbDevice>> ListDevicesAsync(CancellationToken token = default)
    {
        var result = await InvokeAsync(["devices", "-l"], TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        return ParseDevices(result.StandardOutput);
    }

    /// <summary>解析 `adb devices -l` 输出（纯函数，便于不接真机做单元测试）。</summary>
    public static List<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var serial = parts[0];
            var state = parts[1];
            var model = string.Empty;
            var product = string.Empty;
            foreach (var part in parts.Skip(2))
            {
                if (part.StartsWith("model:", StringComparison.Ordinal)) model = part[6..];
                else if (part.StartsWith("product:", StringComparison.Ordinal)) product = part[8..];
            }
            devices.Add(new AdbDevice(serial, state, model, product));
        }
        return devices;
    }

    /// <summary>按连接方式分组：无线调试不是 USB，不能出现在 USB 列表里。</summary>
    public static (List<AdbDevice> Usb, List<AdbDevice> Wireless) SplitByTransport(IEnumerable<AdbDevice> devices)
    {
        var usb = new List<AdbDevice>();
        var wireless = new List<AdbDevice>();
        foreach (var device in devices)
        {
            (device.IsWireless ? wireless : usb).Add(device);
        }
        return (usb, wireless);
    }

    public async Task<List<AdbDevice>> ListOnlineDevicesAsync(CancellationToken token = default)
    {
        var devices = await ListDevicesAsync(token).ConfigureAwait(false);
        return devices.Where(d => string.Equals(d.State, "device", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<string> ShellAsync(string serial, string command, CancellationToken token = default)
    {
        var result = await InvokeAsync(["-s", serial, "shell", command], TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        return (result.StandardOutput + result.StandardError).Trim();
    }

    public async Task<string> GetPropAsync(string serial, string property, CancellationToken token = default)
        => (await ShellAsync(serial, $"getprop {property}", token).ConfigureAwait(false)).Trim();

    /// <summary>展示名优先 marketing_name，其次 manufacturer + model（windows/Model.cs:336-341）。</summary>
    public async Task<string> ResolveDisplayNameAsync(AdbDevice device, CancellationToken token = default)
    {
        var marketing = await GetPropAsync(device.Serial, "ro.config.marketing_name", token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(marketing)) return marketing;
        var manufacturer = await GetPropAsync(device.Serial, "ro.product.manufacturer", token).ConfigureAwait(false);
        return $"{manufacturer} {device.Model}".Trim();
    }

    /// <summary>先摘掉同 remote socket 的旧转发（windows/Utils.RemoveRemoteForward），再建立新转发。</summary>
    public async Task<bool> CreateForwardAsync(string serial, int localPort, CancellationToken token = default)
    {
        await RemoveForwardByRemoteAsync(serial, RemoteSocket, token).ConfigureAwait(false);
        var result = await InvokeAsync(["forward", $"tcp:{localPort}", RemoteSocket], TimeSpan.FromSeconds(15), token)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            // 部分 adb 版本要求 -s 指定设备
            result = await InvokeAsync(["-s", serial, "forward", $"tcp:{localPort}", RemoteSocket], TimeSpan.FromSeconds(15), token)
                .ConfigureAwait(false);
        }
        return result.Success;
    }

    public async Task RemoveForwardByRemoteAsync(string serial, string remote, CancellationToken token = default)
    {
        var result = await InvokeAsync(["forward", "--list"], TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (!string.Equals(parts[0], serial, StringComparison.Ordinal)) continue;
            if (!string.Equals(parts[2], remote, StringComparison.Ordinal)) continue;
            var local = parts[1];
            var port = local.StartsWith("tcp:", StringComparison.Ordinal) ? local[4..] : local;
            await InvokeAsync(["forward", "--remove", $"tcp:{port}"], TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }
    }

    public async Task<bool> PushAsync(string serial, string localPath, string remotePath, CancellationToken token = default)
    {
        var result = await InvokeAsync(["-s", serial, "push", localPath, remotePath], TimeSpan.FromMinutes(5), token)
            .ConfigureAwait(false);
        return result.Success;
    }

    public async Task<bool> ConnectAsync(string host, int port = 5555, CancellationToken token = default)
    {
        var result = await InvokeAsync(["connect", $"{host}:{port}"], TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        return result.Success;
    }

    public async Task DisconnectAsync(string host, int port = 5555, CancellationToken token = default)
        => await InvokeAsync(["disconnect", $"{host}:{port}"], TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
}
