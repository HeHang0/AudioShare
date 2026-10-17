using System.Text;

namespace AudioShare;

/// <summary>
/// 轻量日志器：同时写标准错误与文件（$XDG_STATE_HOME/AudioShare/audioshare.log）。
/// 对应 windows/Logger.cs（Windows 端仅在 DEBUG 下写 Trace，这里保留文件日志以便验收取证）。
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static bool _initialized;
    private static bool _console = true;

    public static string FilePath { get; private set; } = string.Empty;

    public static void Init(bool console = true, string? filePath = null)
    {
        lock (Gate)
        {
            _console = console;
            var path = filePath ?? DefaultPath();
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer?.Dispose();
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };
                FilePath = path;
            }
            catch (Exception ex)
            {
                _writer = null;
                if (_console) Console.Error.WriteLine($"[logger] cannot open log file {path}: {ex.Message}");
            }
            _initialized = true;
        }
    }

    public static string DefaultPath()
    {
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(state))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            state = Path.Combine(home, ".local", "state");
        }
        return Path.Combine(state, "AudioShare", "audioshare.log");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        lock (Gate)
        {
            if (!_initialized) Init();
            if (_console) Console.Error.WriteLine(line);
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // 日志失败不得影响主流程
            }
        }
    }
}

/// <summary>流读取扩展：带超时地把字节写入环形缓冲，供采集源使用。</summary>
public static class StreamReadExtensions
{
    public static int ReadWithTimeout(this Stream stream, RingBuffer ring, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var chunk = new byte[32768];
            var read = stream.ReadAsync(chunk, cts.Token).AsTask().GetAwaiter().GetResult();
            if (read <= 0) throw new InvalidOperationException("capture stream ended");
            ring.Write(chunk.AsSpan(0, read));
            return read;
        }
        catch (OperationCanceledException)
        {
            return 0; // 超时：monitor 无数据（静默）
        }
    }
}

/// <summary>进程执行封装：记录完整命令行、退出码与输出（验收标准 6 要求）。</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Success => ExitCode == 0;
    }

    public static async Task<Result> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput != null,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        if (!process.Start()) throw new InvalidOperationException($"cannot start process: {fileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput != null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        var exitCode = -1;
        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 忽略终止失败
            }
            exitCode = timeoutCts.IsCancellationRequested ? 124 : 130;
        }

        return new Result(exitCode, stdout.ToString(), stderr.ToString());
    }

    public static async Task<Result> RunAsync(string fileName, TimeSpan timeout, params string[] arguments)
        => await RunAsync(fileName, arguments, timeout).ConfigureAwait(false);

    /// <summary>定位可执行文件：先查 PATH，再查常见目录（对应 windows/Utils.FindAdbPath 的 Linux 版本）。</summary>
    public static string? Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        foreach (var directory in new[] { "/usr/bin", "/usr/local/bin", "/bin", "/opt/android-sdk/platform-tools" })
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
