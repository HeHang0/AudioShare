namespace AudioShare.Cli;

/// <summary>极简测试装置：无需外部测试框架，保证构建零额外依赖。</summary>
public static class TestHarness
{
    private static readonly List<(string Name, bool Passed, string Evidence)> Results = [];
    private static string _current = string.Empty;
    private static readonly List<string> CurrentEvidence = [];
    private static readonly List<string> Diagnostic = [];

    public static bool Verbose { get; set; }

    public static int Run(string name, Action body)
    {
        Begin(name);
        try
        {
            body();
            End(name, true);
            return 0;
        }
        catch (Exception ex)
        {
            Evidence($"exception: {ex.GetType().Name}: {ex.Message}");
            Evidence($"stack: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            if (Verbose) Evidence(ex.StackTrace ?? string.Empty);
            End(name, false);
            return 1;
        }
    }

    public static int Run(string name, Func<Task> body)
    {
        Begin(name);
        try
        {
            body().GetAwaiter().GetResult();
            End(name, true);
            return 0;
        }
        catch (Exception ex)
        {
            Evidence($"exception: {ex.GetType().Name}: {ex.Message}");
            Evidence($"stack: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            if (Verbose) Evidence(ex.StackTrace ?? string.Empty);
            End(name, false);
            return 1;
        }
    }

    private static void Begin(string name)
    {
        _current = name;
        CurrentEvidence.Clear();
        Console.WriteLine($"── {name}");
    }

    private static void End(string name, bool passed)
    {
        Results.Add((name, passed, string.Join(" | ", CurrentEvidence)));
        Console.WriteLine($"   {(passed ? "PASS" : "FAIL")}  {name}");
        foreach (var evidence in CurrentEvidence) Console.WriteLine($"         · {evidence}");
    }

    public static void Evidence(string text) => CurrentEvidence.Add(text);

    /// <summary>会话级诊断信息：在汇总处统一打印（不隶属于任何单个测试）。</summary>
    public static void Trace(string text)
    {
        Diagnostic.Add(text);
        Console.WriteLine($"   · diag: {text}");
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Evidence($"ok: {message}");
    }

    public static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected={expected} actual={actual}");
        }
        Evidence($"ok: {message} = {actual}");
    }

    public static void AssertNear(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message}: expected≈{expected} actual={actual} tol={tolerance}");
        }
        Evidence($"ok: {message} = {actual:0.###} (期望 {expected:0.###}±{tolerance:0.###})");
    }

    public static void AssertInRange(double min, double max, double actual, string message)
    {
        if (actual < min || actual > max)
        {
            throw new InvalidOperationException($"{message}: actual={actual} 不在 [{min},{max}]");
        }
        Evidence($"ok: {message} = {actual:0.###} (范围 {min:0.###}~{max:0.###})");
    }

    public static void Summary()
    {
        var passed = Results.Count(r => r.Passed);
        Console.WriteLine();
        Console.WriteLine($"═══ 自检汇总: {passed}/{Results.Count} 通过 ═══");
        foreach (var (name, ok, _) in Results)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }
        Console.WriteLine($"_current={_current}");
    }

    public static int FailedCount => Results.Count(r => !r.Passed);
}
