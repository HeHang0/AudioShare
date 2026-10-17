namespace AudioShare;

/// <summary>
/// 线路协议常量与编码器。所有取值来自 Step 1 审计（见 PROTOCOL_AND_ARCH.md 第 4 节）：
/// - windows/Speaker.cs:35 头部字面量；:23-30 命令枚举；:226-229 建连参数；:486-487 帧长度前缀。
/// - android/app/src/main/java/com/picapico/audioshare/TcpService.java:121-150 解析端语义。
/// 多字节整数一律小端（TcpService.java:617-622 parseInt）。
/// </summary>
public static class Protocol
{
    /// <summary>"picapico-audio-share"（18 字节），同时是 Android 端 LocalServerSocket 的抽象命名。</summary>
    public const string Head = "picapico-audio-share";

    /// <summary>UDP 发现报文前缀，与 Head 相同（TcpService.java:603）。</summary>
    public const string BroadcastPrefix = Head;

    public const int DefaultTcpPort = 8088;
    public const int DefaultHttpPort = 8080;
    public const int PhicommHttpPort = 8090;
    public const int DiscoveryPortFirst = 58261;
    public const int DiscoveryPortLast = 58270;
    /// <summary>
    /// 发现报文长度上限。windows/Model.cs:83 用的是 `&gt; 26`，但 android 端自 5c06bac（2024-03-16）
    /// 起报文恒为 "picapico-audio-share@&lt;port&gt;@&lt;httpPort&gt;"，@8088/@8080 即 30 字节 —— 原版 Windows
    /// 客户端会因此丢弃全部广播（潜在缺陷）。为与真实 Android 客户端互通，这里放宽到 64 字节。
    /// </summary>
    public const int DiscoveryMaxMessageLength = 64;
    public const string AndroidPackageName = "com.picapico.audioshare";
    public const string AndroidActivity = "com.picapico.audioshare/.MainActivity";
    public const string AndroidRemoteSocket = "localabstract:picapico-audio-share";
    public const string AndroidApkRemotePath = "/data/local/tmp/audioshare.apk";

    /// <summary>
    /// 安卓端 APK 的 versionName（android/app/build.gradle:14）。仅用于判断设备上的 App 是否需要重装，
    /// 必须与 APK 保持一致，不随桌面包版本变化。
    /// </summary>
    public const string AndroidVersionName = "1.7.9";

    /// <summary>桌面端（Linux 包）版本号；与 AudioShare.Gui.csproj 的 Version、build-package.sh 保持一致。</summary>
    public const string AppVersion = "1.7.19";

    public static readonly byte[] HeadBytes = System.Text.Encoding.ASCII.GetBytes(Head);

    /// <summary>心跳帧：4 个 0x00（长度 0），windows/Speaker.cs:462。</summary>
    public static readonly byte[] HeartbeatBytes = [0x00, 0x00, 0x00, 0x00];

    public static byte[] CommandByte(Command command) => [(byte)command];

    public static byte[] Int32LittleEndian(int value) => BitConverter.GetBytes(value);

    /// <summary>
    /// 下发声道掩码。严格对齐 windows/Speaker.cs:228 的 `_channel == Stereo ? 12 : 4`：
    /// 非 Stereo 模式（None/Left/Right）均下发 4 单声道掩码，实际声道内容由客户端的抽取逻辑决定。
    /// </summary>
    public static int ChannelMask(AudioChannel channel) => channel == AudioChannel.Stereo ? 12 : 4;

    /// <summary>把 PCM 帧编码为线路格式：[4 字节小端长度][数据]。</summary>
    public static byte[] EncodeFrame(ReadOnlySpan<byte> pcm)
    {
        var buffer = new byte[4 + pcm.Length];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), pcm.Length);
        pcm.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    /// <summary>帧数据长度必须是 4 字节（16bit × 2ch）的整数倍，否则 Android 端会出现半采样错位。</summary>
    public static bool IsValidFrameLength(int length) => length > 0 && length % 4 == 0;

    public static int FrameBytes(int sampleRateHz, int frameMilliseconds = AudioEngine.DefaultFrameMilliseconds)
        => sampleRateHz * frameMilliseconds / 1000 * AudioEngine.Channels * AudioEngine.BytesPerSample;
}

/// <summary>windows/Speaker.cs:23-30 的 Command 枚举。</summary>
public enum Command : byte
{
    None = 0,
    AudioData = 1,
    Volume = 2,
    SyncTime = 3,
    Stop = 4,
}

/// <summary>windows/AudioManager.cs:116-132 的声道抽取算法（抽取，非混音）。</summary>
public static class ChannelSplitter
{
    public static void Split(
        ReadOnlySpan<byte> interleaved,
        Span<byte> left,
        Span<byte> right)
    {
        // 与 Windows 实现一致：左 = 每 4 字节中的前 2 字节，右 = 后 2 字节。
        var half = interleaved.Length / 2;
        if (left.Length < half || right.Length < half)
        {
            throw new ArgumentException("output buffers too small");
        }

        for (int i = 0, j = 0; j < half; i += 4, j += 2)
        {
            left[j] = interleaved[i];
            left[j + 1] = interleaved[i + 1];
            right[j] = interleaved[i + 2];
            right[j + 1] = interleaved[i + 3];
        }
    }

    /// <summary>按模式取出该设备应发送的数据；Stereo 直接返回原始帧。</summary>
    public static byte[] ForChannel(ReadOnlySpan<byte> interleaved, AudioChannel channel)
    {
        if (channel == AudioChannel.Stereo)
        {
            return interleaved.ToArray();
        }

        var half = interleaved.Length / 2;
        var left = new byte[half];
        var right = new byte[half];
        Split(interleaved, left, right);
        return channel == AudioChannel.Right ? right : left;
    }
}
