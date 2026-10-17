namespace AudioShare.Cli;

/// <summary>
/// 独立实现的协议解析器（验收标准 2）。刻意不复用客户端编码路径：
/// 它按 android/app/src/main/java/com/picapico/audioshare/TcpService.java 的 readHead/readInt/parseInt
/// 语义重新实现一遍，用于回放客户端真实字节流并断言每个字段。
/// </summary>
public sealed class ProtocolVerifier
{
    private readonly List<byte> _stream = [];

    public void Feed(IEnumerable<byte> data) => _stream.AddRange(data);

    /// <summary>把字节转成可安全打印的转义形式（避免控制字符污染终端输出）。</summary>
    public static string Escape(IReadOnlyList<byte> data, int count = 0)
    {
        var take = count <= 0 ? data.Count : Math.Min(count, data.Count);
        var builder = new System.Text.StringBuilder(take * 4);
        for (var i = 0; i < take; i++)
        {
            var value = data[i];
            builder.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
            if (i % 8 == 7) builder.Append(' ');
        }
        return builder.ToString().TrimEnd();
    }

    public static string ToHex(IReadOnlyList<byte> data, int count = 0)
    {
        var take = count <= 0 ? data.Count : Math.Min(count, data.Count);
        return string.Join(' ', System.Linq.Enumerable.Take(data, take).Select(b => b.ToString("X2")));
    }

    /// <summary>TcpService.readHead：精确读 20 字节头部，再读 1 字节命令。</summary>
    public (string Head, byte Command, int Consumed) ReadHead()
    {
        var headLength = Protocol.Head.Length;
        if (_stream.Count < headLength + 1)
        {
            throw new InvalidOperationException($"stream too short for head: {_stream.Count} < {headLength + 1}");
        }
        var head = System.Text.Encoding.ASCII.GetString([.. _stream.Take(headLength)]);
        return (head, _stream[headLength], headLength + 1);
    }

    /// <summary>TcpService.parseInt：小端 4 字节。</summary>
    public int ReadIntAt(int offset)
    {
        if (_stream.Count < offset + 4) throw new InvalidOperationException($"stream too short for int at {offset}");
        return (_stream[offset] & 0xFF)
            | ((_stream[offset + 1] & 0xFF) << 8)
            | ((_stream[offset + 2] & 0xFF) << 16)
            | ((_stream[offset + 3] & 0xFF) << 24);
    }

    /// <summary>把音频流部分解析成帧序列：每帧 [4 字节小端长度][数据]。</summary>
    public List<byte[]> ReadFrames(int offset)
    {
        var frames = new List<byte[]>();
        var cursor = offset;
        while (cursor + 4 <= _stream.Count)
        {
            var length = ReadIntAt(cursor);
            cursor += 4;
            if (length == 0)
            {
                frames.Add([]);
                continue;
            }
            if (length < 0 || cursor + length > _stream.Count) break;
            frames.Add(_stream.GetRange(cursor, length).ToArray());
            cursor += length;
        }
        return frames;
    }

    public int Count => _stream.Count;
}
