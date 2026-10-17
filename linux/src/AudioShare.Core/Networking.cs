using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AudioShare;

/// <summary>局域网发现：监听 Android 客户端每 10 秒发出的 UDP 广播（android TcpService.java:582-614）。</summary>
public sealed class UdpDiscovery : IDisposable
{
    private readonly List<UdpClient> _listeners = [];
    private readonly List<int> _boundPorts = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _accepted;
    private long _dropped;
    private long _messages;

    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;

    public IReadOnlyList<int> BoundPorts => _boundPorts;

    public long Accepted => Interlocked.Read(ref _accepted);

    public long Dropped => Interlocked.Read(ref _dropped);

    public long Messages => Interlocked.Read(ref _messages);

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        // 关键差异（见 PROTOCOL_AND_ARCH.md §7.1 R5）：Windows 端在 58261..58270 上循环尝试却只保留
        // 最后一个成功绑定的 socket；Linux 端把 10 个端口全部绑上，兼容发送端任意端口选择。
        for (var port = Protocol.DiscoveryPortFirst; port <= Protocol.DiscoveryPortLast; port++)
        {
            try
            {
                var listener = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                listener.EnableBroadcast = true;
                _listeners.Add(listener);
                _boundPorts.Add(port);
            }
            catch (SocketException ex)
            {
                Logger.Warning($"discovery bind {port} failed: {ex.Message}");
            }
        }
        Logger.Info($"discovery listening on ports: {string.Join(',', _boundPorts)}");
        _loop = Task.WhenAll(_listeners.Select(listener => ReceiveLoopAsync(listener, token)));
    }

    private async Task ReceiveLoopAsync(UdpClient listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await listener.ReceiveAsync(token).ConfigureAwait(false);
                Handle(result.Buffer, result.RemoteEndPoint.Address.ToString());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warning("discovery receive failed: " + ex.Message);
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>解析与过滤，语义对齐 windows/Model.cs:82-94。</summary>
    public void Handle(byte[] buffer, string remoteAddress)
    {
        Interlocked.Increment(ref _messages);
        if (buffer.Length > Protocol.DiscoveryMaxMessageLength)
        {
            Interlocked.Increment(ref _dropped);
            Logger.Info($"discovery drop from {remoteAddress}: length {buffer.Length} > {Protocol.DiscoveryMaxMessageLength}");
            return;
        }

        var message = System.Text.Encoding.UTF8.GetString(buffer);
        var parts = message.Split('@');
        if (parts.Length < 2 || parts[0] != Protocol.BroadcastPrefix)
        {
            Interlocked.Increment(ref _dropped);
            Logger.Info($"discovery drop from {remoteAddress}: malformed '{message}'");
            return;
        }

        if (!int.TryParse(parts[1], out var tcpPort) || tcpPort <= 0 || tcpPort >= 65535)
        {
            Interlocked.Increment(ref _dropped);
            Logger.Info($"discovery drop from {remoteAddress}: bad tcp port '{parts[1]}'");
            return;
        }

        var httpPort = 0;
        if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedHttp) && parsedHttp > 0 && parsedHttp < 65535)
        {
            httpPort = parsedHttp;
        }

        Interlocked.Increment(ref _accepted);
        Logger.Info($"discovery accept {remoteAddress}: tcp={tcpPort} http={httpPort}");
        DeviceDiscovered?.Invoke(this, new DiscoveredDevice(remoteAddress, tcpPort, httpPort));
    }

    /// <summary>仅用于自检：向本机广播地址投递一条与 Android 端格式一致的报文。</summary>
    public static void Send(byte[] payload, int port, bool broadcast = false)
    {
        using var socket = new UdpClient(0) { EnableBroadcast = broadcast };
        var target = broadcast ? IPAddress.Broadcast : IPAddress.Loopback;
        socket.Send(payload, payload.Length, new IPEndPoint(target, port));
    }

    public static byte[] BuildBroadcastPayload(int tcpPort, int httpPort)
        => System.Text.Encoding.UTF8.GetBytes($"{Protocol.BroadcastPrefix}@{tcpPort}@{httpPort}");

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // 忽略停止期间异常
        }
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }
        _listeners.Clear();
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }
}

public sealed record DiscoveredDevice(string Address, int TcpPort, int HttpPort)
{
    public string Id => $"{Address}:{TcpPort}";

    public string HttpUrl => $"http://{Address}:{(HttpPort > 0 ? HttpPort : Protocol.DefaultHttpPort)}";
}

/// <summary>
/// 物理网卡筛选：只在真实局域网网段上做扫描/绑定，过滤 lo、容器与隧道虚拟网卡。
/// 对应 android NetworkUtils.getAllInetAddress()（NetworkUtils.java:115-147）并针对 Linux 收敛。
/// </summary>
public static class NetworkInterfaceFilter
{
    private static readonly string[] VirtualPrefixes =
    [
        "lo", "docker", "veth", "br-", "virbr", "tun", "tap", "tailscale", "wg", "zt", "podman", "cni",
        "flannel", "kube", "vmnet", "pan", "utun", "sit", "gre", "ip6tnl", "dummy", "veth",
    ];

    public static string Reason(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "empty interface name";
        if (name == "lo") return "loopback";
        foreach (var prefix in VirtualPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return prefix is "lo" ? "loopback" : $"virtual container/tunnel interface '{prefix}*'";
            }
        }
        return string.Empty;
    }

    public static bool Accepts(string name) => string.IsNullOrEmpty(Reason(name));

    public static bool IsUsable(NetworkInterface nic)
    {
        if (!Accepts(nic.Name)) return false;
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
        try
        {
            if (!nic.Supports(NetworkInterfaceComponent.IPv4)) return false;
        }
        catch (Exception)
        {
            return false;
        }
        return true;
    }

    public static async Task<string> ReportAsync()
    {
        var lines = new List<string>();
        var accepted = 0;
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        foreach (var nic in interfaces)
        {
            var reason = Reason(nic.Name);
            var ipv4 = string.Join(',', UnicastAddresses(nic)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => $"{a.Address}/{a.PrefixLength}"));
            var up = nic.OperationalStatus == OperationalStatus.Up;
            if (string.IsNullOrEmpty(reason))
            {
                if (!up) reason = "interface down";
                else if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) reason = "loopback";
            }

            var subnet = string.Empty;
            if (string.IsNullOrEmpty(reason))
            {
                accepted++;
                var unicast = UnicastAddresses(nic)
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (unicast != null)
                {
                    subnet = DescribeSubnet(unicast.Address, unicast.PrefixLength);
                }
            }

            var verdict = string.IsNullOrEmpty(reason) ? "ACCEPT" : "REJECT";
            var detail = string.IsNullOrEmpty(reason) ? $"subnet={subnet}" : $"reason={reason}";
            lines.Add($"{verdict} {nic.Name} [{nic.NetworkInterfaceType}] up={up} ipv4={ipv4} {detail}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        lines.Add($"physical interfaces accepted: {accepted}/{interfaces.Length}");
        return string.Join(Environment.NewLine, lines);
    }

    public static string DescribeSubnet(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return $"{address}/{prefixLength}";
        var prefix = Math.Clamp(prefixLength, 0, 32);
        var mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
        // 首字节为最高位，组装成主机序整数；由 ToIp 还原点分字符串，避免 IPAddress(uint) 的字节序歧义
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var network = value & mask;
        var broadcast = network | ~mask;
        var firstHost = network + 1;
        var lastHost = broadcast <= firstHost ? broadcast : broadcast - 1;
        return $"{ToIp(network)}/{prefix} scan={ToIp(firstHost)}-{ToIp(lastHost)}";
    }

    /// <summary>把按主机字节序存放的 IPv4 数值转成点分字符串（不经 IPAddress(uint) 的字节序转换）。</summary>
    private static string ToIp(uint value)
        => $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    /// <summary>本机第一个物理局域网 IPv4，用于"添加 IP 设备"的默认网段（对应 windows/Model.cs:382-399）。</summary>
    public static IPAddress? PrimaryPhysicalAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsable(nic)) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var address = UnicastAddresses(nic)
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (address != null) return address.Address;
        }
        return null;
    }

    /// <summary>相邻网段扫描候选（受"只扫描真实物理局域网网段"约束）。</summary>
    public static IEnumerable<IPAddress> ScanCandidates()
    {
        var primary = PrimaryPhysicalAddress();
        if (primary == null) yield break;
        var bytes = primary.GetAddressBytes();
        for (var host = 2; host < 255; host++)
        {
            yield return new IPAddress([bytes[0], bytes[1], bytes[2], (byte)host]);
        }
    }

    public static async Task<bool> PortIsOpenAsync(string host, int port, int timeoutMs = 1000, CancellationToken token = default)
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(host, port, token).AsTask();
            var completed = await Task.WhenAny(connect, Task.Delay(timeoutMs, token)).ConfigureAwait(false);
            if (completed != connect) return false;
            await connect.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool PortIsOpen(string host, int port, int timeoutMs = 1000)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            if (!connect.Wait(timeoutMs))
            {
                return false;
            }
            return client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>系统分配一个空闲本地端口（对应 windows/Utils.GetFreePort）。</summary>
    public static int GetFreePort(int fallback = Protocol.DefaultTcpPort)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch (Exception)
        {
            for (var port = fallback; port < 65535; port++)
            {
                if (!PortIsOpen("127.0.0.1", port, 50)) return port;
            }
            return fallback;
        }
    }

    public static string DescribeLocalNetworks()
    {
        var entries = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsable(nic)) continue;
            foreach (var address in UnicastAddresses(nic))
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                entries.Add(DescribeSubnet(address.Address, address.PrefixLength));
            }
        }
        return entries.Count == 0 ? "(no physical LAN interface)" : string.Join(Environment.NewLine, entries);
    }

    /// <summary>安全读取网卡单播地址（部分虚拟网卡在断开时会抛异常）。</summary>
    public static List<UnicastIPAddressInformation> UnicastAddresses(NetworkInterface nic)
    {
        try
        {
            var addresses = new List<UnicastIPAddressInformation>();
            var properties = nic.GetIPProperties();
            if (properties == null) return addresses;
            foreach (var address in properties.UnicastAddresses)
            {
                if (address?.Address != null) addresses.Add(address);
            }
            return addresses;
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static string ParecArguments(string source, int sampleRate) => ParecCaptureSource.BuildArguments(source, sampleRate);

    public static string PwCatArguments(int sampleRate) =>
        $"--playback --format=s16 --rate={sampleRate} --channels=2 -";

    public static string FfplayHint(int port) => $"arecord -f S16_LE --device=hw:0 | nc 127.0.0.1 {port}";

    [Conditional("DEBUG")]
    public static void TraceStartup()
    {
        Logger.Debug("network interfaces: " + Environment.NewLine + DescribeLocalNetworks());
    }
}
