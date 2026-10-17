using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioShare;

/// <summary>
/// 设置持久化。字段与 windows/Settings.cs:9-31 一一对应，路径改为 XDG 约定。
/// </summary>
public sealed class SettingsStore
{
    public sealed class DeviceEntry
    {
        public string Id { get; set; } = string.Empty;
        public AudioChannel Channel { get; set; } = AudioChannel.None;

        public DeviceEntry() { }

        public DeviceEntry(string id, AudioChannel channel)
        {
            Id = id;
            Channel = channel;
        }
    }

    public string AudioId { get; set; } = string.Empty;

    public List<DeviceEntry> AdbDevices { get; set; } = [];

    public List<DeviceEntry> IPDevices { get; set; } = [];

    public int SampleRate { get; set; } = 48000;

    /// <summary>音频帧长（毫秒）。默认 100ms，与 Windows 端 NAudio loopback 节奏一致；真机可调 10/20/50/100。</summary>
    public int FrameMs { get; set; } = AudioEngine.DefaultFrameMilliseconds;

    public int Volume { get; set; } = 50;

    public bool IsUSB { get; set; } = true;

    public bool VolumeFollowSystem { get; set; } = true;

    public bool Acrylic { get; set; } = true;

    /// <summary>配置文件路径（不参与序列化）：Load 时记录，Save 时沿用；测试据此避免写入用户真实配置。</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static SettingsStore Load(string? path = null)
    {
        var file = path ?? AppPaths.ConfigFile;
        try
        {
            if (File.Exists(file))
            {
                var text = File.ReadAllText(file);
                var loaded = JsonSerializer.Deserialize<SettingsStore>(text, Options);
                if (loaded != null)
                {
                    loaded.FilePath = file;
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"cannot load settings from {file}: {ex.Message}");
        }
        return new SettingsStore { FilePath = file };
    }

    public void Save(string? path = null)
    {
        var file = path ?? FilePath ?? AppPaths.ConfigFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Logger.Warning($"cannot save settings to {file}: {ex.Message}");
        }
    }
}

/// <summary>开机启动：Windows 用启动目录快捷方式（windows/Model.cs:464-488），Linux 用 XDG autostart。</summary>
public static class Autostart
{
    public static bool IsEnabled() => File.Exists(AppPaths.AutostartFile);

    public static void Set(bool enabled)
    {
        var file = AppPaths.AutostartFile;
        if (!enabled)
        {
            if (File.Exists(file)) File.Delete(file);
            Logger.Info("autostart disabled");
            return;
        }

        var executable = Environment.ProcessPath ?? "audioshare";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var content = $"""
            [Desktop Entry]
            Type=Application
            Name=AudioShare
            Comment=Share this computer's audio to Android devices
            Exec={executable}
            Terminal=false
            X-GNOME-Autostart-enabled=true
            Categories=AudioVideo;Audio;
            """ + Environment.NewLine;
        File.WriteAllText(file, content);
        Logger.Info($"autostart enabled -> {file}");
    }
}

/// <summary>单声道/立体声测试信号生成器（用于采集与声道分离的自检）。</summary>
public static class ToneGenerator
{
    /// <summary>生成一段立体声 16bit PCM：左声道 freqLeft、右声道 freqRight。</summary>
    public static byte[] Stereo(double freqLeft, double freqRight, int sampleRate, int milliseconds, double amplitude = 0.5)
    {
        var samples = sampleRate * milliseconds / 1000;
        var buffer = new byte[samples * 4];
        for (var i = 0; i < samples; i++)
        {
            var t = i / (double)sampleRate;
            var left = (short)(Math.Sin(2 * Math.PI * freqLeft * t) * amplitude * short.MaxValue);
            var right = (short)(Math.Sin(2 * Math.PI * freqRight * t) * amplitude * short.MaxValue);
            BitConverter.TryWriteBytes(buffer.AsSpan(i * 4, 2), left);
            BitConverter.TryWriteBytes(buffer.AsSpan(i * 4 + 2, 2), right);
        }
        return buffer;
    }

    /// <summary>把立体声 PCM 包装成 RIFF/WAVE 字节流（供 pw-cat / aplay 播放）。</summary>
    public static byte[] ToWave(byte[] pcm, int sampleRate, int channels = 2, int bitsPerSample = 16)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write((short)bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return memory.ToArray();
    }

    /// <summary>把采集到的 16bit 小端样本展开为 double 序列（可按声道抽取）。</summary>
    public static double[] ToSamples(ReadOnlySpan<byte> pcm, int channel = -1, int channels = 2)
    {
        var frames = pcm.Length / (2 * channels);
        var samples = new List<double>(frames);
        for (var frame = 0; frame < frames; frame++)
        {
            var index = frame * channels * 2;
            if (channel >= 0)
            {
                samples.Add(BitConverter.ToInt16(pcm[(index + channel * 2)..]));
                continue;
            }
            for (var ch = 0; ch < channels; ch++)
            {
                samples.Add(BitConverter.ToInt16(pcm[(index + ch * 2)..]));
            }
        }
        return [.. samples];
    }

    public static double Rms(ReadOnlySpan<double> samples)
    {
        if (samples.Length == 0) return 0;
        var sum = 0.0;
        foreach (var sample in samples) sum += sample * sample;
        return Math.Sqrt(sum / samples.Length);
    }

    public static double Peak(ReadOnlySpan<double> samples)
    {
        var peak = 0.0;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    /// <summary>
    /// 简易 Goertzel 幅度估计：用于验证某设备收到的是 1kHz（左）还是 2kHz（右）信号。
    /// </summary>
    public static double GoertzelAmplitude(ReadOnlySpan<double> samples, double frequency, int sampleRate)
    {
        if (samples.Length < 64) return 0;
        var k = Math.Round(samples.Length * frequency / sampleRate);
        var omega = 2 * Math.PI * k / samples.Length;
        var cosine = Math.Cos(omega);
        var coefficient = 2 * cosine;
        double s0 = 0, s1 = 0, s2 = 0;
        foreach (var sample in samples)
        {
            s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var real = s1 - s2 * cosine;
        var imaginary = s2 * Math.Sin(omega);
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
    }

    public static double DominantFrequency(ReadOnlySpan<double> samples, int sampleRate, double from = 200, double to = 4000, double step = 25)
    {
        var best = 0.0;
        var bestAmplitude = 0.0;
        for (var frequency = from; frequency <= to; frequency += step)
        {
            var amplitude = GoertzelAmplitude(samples, frequency, sampleRate);
            if (amplitude > bestAmplitude)
            {
                bestAmplitude = amplitude;
                best = frequency;
            }
        }
        return best;
    }
}
