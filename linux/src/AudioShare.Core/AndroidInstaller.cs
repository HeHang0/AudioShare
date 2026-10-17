using System.Security.Cryptography;

namespace AudioShare;

/// <summary>
/// APK 定位/安装。对应 windows/Speaker.cs:337-386 的 EnsureDevice 流程：
/// 版本比对 → push → pm uninstall / pm install -r / rm / deviceidle whitelist → 复查 → am start。
/// </summary>
public sealed class AndroidInstaller(AdbController adb)
{
    public static readonly string[] ApkSearchFolders =
    [
        AppPaths.BaseDirectory,
        AppPaths.LinuxSourceDirectory,
        AppPaths.RepositoryRoot,
    ];

    /// <summary>APK 候选路径：环境变量 → &lt;exe目录&gt; → linux/ → 仓库根（等价 windows 的 "exe同名.apk"）。</summary>
    public static IReadOnlyList<string> CandidateApkPaths()
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("AUDIOSHARE_APK");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        foreach (var folder in ApkSearchFolders.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            candidates.Add(Path.Combine(folder, "AudioShare.apk"));
            candidates.Add(Path.Combine(folder, "audioshare.apk"));
            candidates.Add(Path.Combine(folder, "app-release.apk"));
        }
        return candidates;
    }

    public static string? FindApk()
    {
        foreach (var candidate in CandidateApkPaths())
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool IsVersionInstalled(string dumpsysOutput)
        => dumpsysOutput.Contains(Protocol.AndroidVersionName, StringComparison.Ordinal);

    public async Task<bool> IsPackageVersionMatchedAsync(string serial, CancellationToken token = default)
    {
        var output = await adb.ShellAsync(serial, "dumpsys package com.picapico.audioshare|grep versionName", token)
            .ConfigureAwait(false);
        Logger.Info($"installed version probe [{serial}]: {output.Replace('\n', ' ')}");
        return IsVersionInstalled(output);
    }

    public async Task<bool> StartActivityAsync(string serial, CancellationToken token = default)
    {
        var output = await adb.ShellAsync(serial, $"am start -W -n {Protocol.AndroidActivity}", token).ConfigureAwait(false);
        return output.Contains("Status: ok", StringComparison.OrdinalIgnoreCase) || output.Contains("Starting:", StringComparison.Ordinal);
    }

    /// <summary>安装/拉起；返回设备是否已就绪。</summary>
    public async Task<bool> EnsureAsync(string serial, CancellationToken token = default)
    {
        await adb.StartServerAsync(token).ConfigureAwait(false);
        if (await IsPackageVersionMatchedAsync(serial, token).ConfigureAwait(false))
        {
            return await StartActivityAsync(serial, token).ConfigureAwait(false);
        }

        var apk = FindApk();
        if (apk == null)
        {
            Logger.Error($"apk not found, searched: {string.Join(", ", CandidateApkPaths())}");
            return false;
        }

        Logger.Info($"installing apk {apk} -> {Protocol.AndroidApkRemotePath}");
        if (!await adb.PushAsync(serial, apk, Protocol.AndroidApkRemotePath, token).ConfigureAwait(false))
        {
            return false;
        }

        // 与 windows/Speaker.cs:369-373 完全一致的 shell 序列
        var installCommand =
            $"/system/bin/pm uninstall {Protocol.AndroidPackageName};" +
            $"/system/bin/pm install -r {Protocol.AndroidApkRemotePath};" +
            $"rm -f {Protocol.AndroidApkRemotePath};" +
            $"dumpsys deviceidle whitelist +{Protocol.AndroidPackageName};";
        var output = await adb.ShellAsync(serial, installCommand, token).ConfigureAwait(false);
        Logger.Info("install apk output: " + output.Replace('\n', ' '));

        if (await IsPackageVersionMatchedAsync(serial, token).ConfigureAwait(false))
        {
            return await StartActivityAsync(serial, token).ConfigureAwait(false);
        }

        Logger.Error("apk install finished but version mismatch");
        return false;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>APK 下载（release 未直接提供仓库内 apk 时使用；对应 windows 端"下载 apk 至当前目录"提示）。</summary>
public static class ApkDownloader
{
    public const string ReleaseUrl = "https://github.com/HeHang0/AudioShare/releases/latest/download/AudioShare.apk";

    public static async Task<string> DownloadAsync(string? destination = null, CancellationToken token = default)
    {
        var target = destination ?? Path.Combine(AppPaths.BaseDirectory, "AudioShare.apk");
        var curl = ProcessRunner.Which("curl") ?? throw new FileNotFoundException("curl not found");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".part";
        Logger.Info($"downloading {ReleaseUrl} -> {target}");
        var result = await ProcessRunner.RunAsync(curl, ["-fL", "--retry", "3", "-o", temporary, ReleaseUrl],
            TimeSpan.FromMinutes(10), null, token).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new IOException($"download failed (exit={result.ExitCode}): {result.StandardError.Trim()}");
        }
        File.Move(temporary, target, overwrite: true);
        Logger.Info($"apk downloaded: {target} sha256={AndroidInstaller.Sha256(target)}");
        return target;
    }
}

public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory.TrimEnd('/');

    public static string ConfigDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(root, "AudioShare");
        }
    }

    public static string ConfigFile => Path.Combine(ConfigDirectory, "audio.share.config.json");

    public static string StateDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            var root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state")
                : xdg;
            return Path.Combine(root, "AudioShare");
        }
    }

    public static string AutostartFile
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(root, "autostart", "audioshare.desktop");
        }
    }

    public static string RuntimeDirectory
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime)) return runtime;
            return Path.GetTempPath();
        }
    }

    public static string SingleInstanceSocket => Path.Combine(RuntimeDirectory, "audioshare.sock");

    /// <summary>仓库根：从可执行文件位置向上寻找包含 windows/ 与 android/ 的目录。</summary>
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(BaseDirectory);
            for (var i = 0; i < 8 && directory != null; i++)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "windows"))
                    && Directory.Exists(Path.Combine(directory.FullName, "android")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
            return string.Empty;
        }
    }

    public static string LinuxSourceDirectory
    {
        get
        {
            var root = RepositoryRoot;
            if (string.IsNullOrEmpty(root)) return string.Empty;
            return Path.Combine(root, "linux");
        }
    }
}
