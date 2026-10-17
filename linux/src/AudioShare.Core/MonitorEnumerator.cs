using System.Text.Json;

namespace AudioShare;

/// <summary>
/// 音频输出设备（及其 .monitor 采集源）。
/// 对应 Windows 的 MMDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render)（windows/Model.cs:449-461），
/// Linux 用 `pactl -f json list sinks` 枚举；默认项映射为 @DEFAULT_MONITOR@（model.AudioId 为空时）。
/// </summary>
public sealed record OutputDevice(string Id, string Name, bool IsDefault)
{
    public string MonitorSource => IsDefault || string.IsNullOrEmpty(Id) ? "@DEFAULT_MONITOR@" : Id;
}

public static class MonitorEnumerator
{
    public static async Task<List<OutputDevice>> EnumerateAsync(CancellationToken token = default)
    {
        var devices = new List<OutputDevice>
        {
            // 与 windows/Model.cs:450-461 一致：找不到已保存设备时回退到第一个设备；此处显式提供"默认输出"项
            new(string.Empty, "（默认输出设备 / @DEFAULT_MONITOR@）", true),
        };

        var defaultSink = await GetDefaultSinkAsync(token).ConfigureAwait(false);
        var result = await ProcessRunner.RunAsync("pactl", TimeSpan.FromSeconds(10), "-f", "json", "list", "sinks")
            .ConfigureAwait(false);
        if (!result.Success) return devices;

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            foreach (var sink in document.RootElement.EnumerateArray())
            {
                var name = sink.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrEmpty(name)) continue;

                var monitor = sink.TryGetProperty("monitor_source", out var monitorElement)
                    ? monitorElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(monitor)) monitor = name + ".monitor";

                var description = sink.TryGetProperty("description", out var descriptionElement)
                    ? descriptionElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(description) && sink.TryGetProperty("properties", out var properties)
                    && properties.TryGetProperty("device.description", out var deviceDescription))
                {
                    description = deviceDescription.GetString();
                }
                if (string.IsNullOrWhiteSpace(description)) description = name;

                var isDefault = !string.IsNullOrEmpty(defaultSink) && string.Equals(name, defaultSink, StringComparison.Ordinal);
                var label = isDefault ? description + "（系统默认）" : description;
                devices.Add(new OutputDevice(monitor!, label, isDefault));
            }
        }
        catch (JsonException ex)
        {
            Logger.Warning("cannot parse pactl sink list: " + ex.Message);
        }

        return devices;
    }

    public static async Task<string?> GetDefaultSinkAsync(CancellationToken token = default)
    {
        var result = await ProcessRunner.RunAsync("pactl", TimeSpan.FromSeconds(5), "get-default-sink").ConfigureAwait(false);
        return result.Success ? result.StandardOutput.Trim() : null;
    }

    /// <summary>读取默认输出音量百分比（对应 windows/Model.cs:163-172 的 AudioEndpointVolume）。</summary>
    public static async Task<int?> GetDefaultVolumeAsync(CancellationToken token = default)
    {
        var result = await ProcessRunner.RunAsync("pactl", TimeSpan.FromSeconds(5), "get-sink-volume", "@DEFAULT_SINK@")
            .ConfigureAwait(false);
        if (!result.Success) return null;
        var percentIndex = result.StandardOutput.IndexOf('%');
        if (percentIndex <= 0) return null;
        var start = result.StandardOutput.LastIndexOf(' ', percentIndex, percentIndex);
        var text = result.StandardOutput[(start + 1)..percentIndex].Trim();
        return int.TryParse(text, out var percent) ? percent : null;
    }

    public static async Task SetDefaultVolumeAsync(int volume, CancellationToken token = default)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        await ProcessRunner.RunAsync("pactl", TimeSpan.FromSeconds(5), "set-sink-volume", "@DEFAULT_SINK@", $"{clamped}%")
            .ConfigureAwait(false);
    }
}
