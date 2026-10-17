namespace AudioShare;

/// <summary>
/// 声道模式。取值必须与 windows/AudioChannel.cs 完全一致：
/// None=0, Left=4, Right=8, Stereo=12（作为 Android AudioTrack 的 channelMask 下发）。
/// </summary>
public enum AudioChannel
{
    None = 0,
    Left = 4,
    Right = 8,
    Stereo = 12,
}

public enum ConnectStatus
{
    Connected,
    UnConnected,
    Connecting,
}
