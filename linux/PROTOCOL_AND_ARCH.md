# AudioShare 协议与架构审计（Step 1）

本文件由 `linux/` 客户端开发过程中的源码审计产出，逐项标注**源码位置**，作为 `linux/` 实现与验收的唯一协议依据。
审计对象：本仓库 HEAD = `dac02fd`（"Fix mute after a period of time"），Windows 客户端 `windows/` v1.7.9，Android 客户端 `android/` versionCode 179 / versionName 1.7.9。

约定：`文件:行号` 指本仓库内该提交的行号。凡未能从源码确证的结论均显式标注 **未确证**。

---

## 1. 总览与数据流

```
┌────────────────────── Linux 客户端 (linux/) ───────────────────────┐
│  PulseAudio/PipeWire 输出设备 .monitor 源                          │
│        │ parec --raw / libpulse                                    │
│        ▼                                                           │
│  PcmCapture(16bit/2ch/N Hz) ──► 声道拆分(与 AudioManager 一致)      │
│        │                              │                            │
│        │                              ├─► Speaker A (Stereo) ─┐    │
│        │                              ├─► Speaker B (Left)   ─┤    │
│        │                              └─► Speaker C (Right)  ─┤    │
│  静音保活(补齐静音帧) ─────────────────────────────────────────┤    │
│  UDP 58261-58270 广播发现 ◄───────────────────────────────────┘    │
│  adb forward tcp:<free> localabstract:picapico-audio-share         │
└───────────────────────────────────────────────────────────────────┘
                                    │ TCP
                                    ▼
┌──────────────── Android 客户端 (android/, 不修改) ────────────────┐
│  TcpService.startServer() → ServerSocket(getFreePort()=8088 起)   │
│  TcpService.startLocalServer() → LocalServerSocket("picapico-audio-share")
│  readHead() → 命令分发 → AudioTrack(MODE_STREAM) 播放 PCM         │
│  每 10s 向 255.255.255.255:58261..58270 广播自身端口               │
│  HttpServer(8080，斐讯 8090) 远程管理页                             │
└───────────────────────────────────────────────────────────────────┘
```

Windows 端对应实现：`windows/Speaker.cs`（每设备一条 TCP 连接 + 控制指令）、`windows/Model.cs`（发现、设备池、设置持久化）、`windows/AudioManager.cs`（WASAPI loopback 采集与声道拆分）。

---

## 2. 音频格式（必须逐字节一致）

| 参数 | 取值 | 依据 |
| --- | --- | --- |
| 采样格式 | 16 bit PCM，小端、有符号 | `windows/AudioManager.cs:58` `new WaveFormat(sampleRate, 16, 2)`；`android/.../TcpService.java:214` `AudioFormat.ENCODING_PCM_16BIT` |
| 声道数 | 固定 2（立体声交错采样） | `windows/AudioManager.cs:58`（NAudio 第三参 2 = 2 声道） |
| 默认采样率 | 48000 | `windows/Settings.cs:25` `SampleRate = 48000` |
| 可选采样率 | 192000 / 176400 / 96000 / 48000 / 44100 / 16000 / 8000 | `windows/Model.cs:106-115` |
| 采样率下发时机 | 每次建连时作为 TCP 头后的第一个 int32 下发 | `windows/Speaker.cs:226`；`android/.../TcpService.java:212` |
| 单帧大小 | 采集回调缓冲区大小（NAudio WASAPI loopback 默认 100 ms 量级），**未确证**具体值 | 未在源码中固定；Linux 端自行决定分帧大小，只需保证 `长度 = 4 + N*4` 字节、且 `N` 为偶数采样数 |

> Android 依据 `sampleRate` 与 `channelMask` 计算 `AudioTrack.getMinBufferSize(...)` 并作为 Socket 接收缓冲区（`android/.../TcpService.java:215-219`），因此**分帧不能过大**：Windows 端默认每 100 ms 左右发送一帧。

Linux 侧对应：PipeWire/PulseAudio 输出设备的 `.monitor` 源，默认 `@DEFAULT_MONITOR@`。本机采样为 48 kHz（与默认一致），非 48 kHz 采样率需重采样后再下发。

---

## 3. 双模传输

### 3.1 Wi-Fi（TCP 直连）

- 设备标识：`"<ip>:<port>"`，例如 `192.168.1.10:8088`（`windows/Speaker.cs:403`、`windows/Model.cs:387,403`）。
- 地址解析正则（支持 IPv4、`[IPv6]`、可选端口；无端口时默认 **80**）：
  `^[\[[]?(?<ip>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}|([0-9A-Fa-f]{1,4}:){1,7}([0-9A-Fa-f]{1,4}|:))[\]~]?(:(?<port>\d+))?$`（`windows/Speaker.cs:193-208`）。
- 目标端口由 Android 端 `NetworkUtils.getFreePort()` 决定：**从 8088 起向上找到第一个空闲端口**（`android/.../NetworkUtils.java:35-46`），因此"默认 8088"成立的前提是 8088 空闲。

### 3.2 USB（adb forward）

- Windows：`adbClient.RemoveRemoteForward(device, "localabstract:picapico-audio-share")` → `adbClient.CreateForward(device, "tcp:<freePort>", "localabstract:picapico-audio-share", true)`，随后连 `127.0.0.1:<freePort>`（`windows/Speaker.cs:36,184-189`）。
- `<freePort>` 来自 `Utils.GetFreePort()`：先让 OS 分配一个空闲端口（`TcpListener(Loopback,0)`），失败时从 8088 向上探测（`windows/Utils.cs:59-78`）。
- Android 端对应 `LocalServerSocket(HEAD)`，抽象命名空间 = 头部字面量 `"picapico-audio-share"`（`android/.../TcpService.java:239`）。
- Linux 等价命令：`adb -s <serial> forward tcp:<freePort> localabstract:picapico-audio-share`（`adb forward --remove` 清理）。

---

## 4. 报文协议（TCP，端口 8088 起）

### 4.1 头部

```
偏移 0..19 : "picapico-audio-share"   （20 字节，UTF-8/ASCII 单字节）
偏移 20    : 命令字节
```
- 头部字面量：`windows/Speaker.cs:35`（`Encoding.Default.GetBytes`）与 `android/.../TcpService.java:53`；长度为 **20 字节**（`"picapico-audio-share"`）。
- Android 解析时按 20 字节**精确读满**，再读 1 字节命令；头部不匹配返回 0（忽略）。

### 4.2 命令字

| 值 | 名称 | 方向 | 负载 | 依据 |
| --- | --- | --- | --- | --- |
| 0 | None | — | 占位/无效 | `windows/Speaker.cs:23-30` |
| 1 | AudioData | PC → Android（新连接） | `int32 sampleRate` + `int32 channelMask`，随后进入音频流 | `windows/Speaker.cs:225-229`；`android/.../TcpService.java:211-229` |
| 2 | Volume | PC → Android | `int32 volume`（0-100） | `windows/Speaker.cs:143-147`；`android/.../TcpService.java:154-161` |
| 3 | SyncTime | PC → Android | 无 | `windows/Speaker.cs:149-152`；`android/.../TcpService.java:162-163` |
| 4 | Stop | PC → Android | 无（建连前先发一次，终止既有会话） | `windows/Speaker.cs:217`；`android/.../TcpService.java:164-179` |

- **控制指令使用独立 TCP 连接**：写头 + 命令 + 负载后，等待服务端回 1 字节，然后关闭（`windows/Speaker.cs:505-538`）。Android 侧对非音频命令处理完即 `socket.close()`（`android/.../TcpService.java:230-233`），音频流则回 1 字节 ACK（`:442-443`）。
- 所有多字节整数均为**小端**：`android/.../TcpService.java:617-622` `parseInt()` 按 `[0] | [1]<<8 | [2]<<16 | [3]<<24` 解析；Windows 端用 `BitConverter.GetBytes`（x86 小端）。

### 4.3 音频流帧格式（命令 1 之后）

```
[4 字节小端 uint32 长度 N][N 字节 PCM 数据]
```
- Android 读取：`stream.readFully(buffer,0,4)` → `parseInt` 得 `N`；`N == 0` 视为心跳（continue，不写 AudioTrack）；`N > buffer.length` 时扩容；再 `readFully(buffer,0,N)`（`android/.../TcpService.java:447-461`）。
- **`N` 必须是 4 的整数倍**（16 bit × 2 声道）否则会产生半采样错位。
- **心跳帧 = 4 个 0x00 字节**（长度 0）；Windows 侧常量 `_heartBeatBytes = {0,0,0,0}`（`windows/Speaker.cs:462`），发送周期 5 秒（`windows/Model.cs:38-42,45-54`，`Speaker.SendHeartbeat` `:461-474`，仅当距上次发送 >5 s）。
- Android 端**自身也发心跳**：播放中每 10 秒向音频 socket 写 1 个 0 字节（`android/.../TcpService.java:582-599`），并顺带在系统音量为 0 时恢复 `lastPCVolume`（`:592-594`）。PC 侧必须持续读 socket（`windows/Speaker.cs:260-274`）否则 Android 写心跳可能阻塞；Linux 端必须同样保持读取循环。
- 反压语义：PC 端 `SendAudioData` 用 `isBusy` 标志跳帧（丢帧而非阻塞采集，`windows/Speaker.cs:281-306`）；Android 端 `isWriting` 忙时**跳过**该帧数据但仍继续读（`:462-486`）。
- 写失败重试：第一次失败自动重连一次（`_retried`），再失败则断开并弹通知（`windows/Speaker.cs:290-301,108-135`）。

### 4.4 音量与同步

- 连接成功后 PC 会立刻下发当前音量（`windows/Model.cs:523-535` → `Speaker.SetVolume`），并对所有已连接设备下发 `SyncTime`（命令 3）。
- Android：`lastPCVolume = maxAudioVolume * volume / 100` 并 `setStreamVolume(STREAM_MUSIC, ...)`（`android/.../TcpService.java:154-161,182-193`）。
- PC 侧音量跟随系统时，由 WASAPI 音量通知驱动（`windows/AudioManager.cs:97-105`，`windows/Model.cs:98-101`）。

---

## 5. 声道切分算法（必须严格一致）

Windows 侧单一采集流 + 三路事件（`windows/AudioManager.cs:107-140`）：

```
StereoAvailable  ← 原始交错缓冲（整帧）
LeftAvailable    ← 每 4 字节采样对中取前 2 字节（左声道）
RightAvailable   ← 每 4 字节采样对中取后 2 字节（右声道）
```

拆分循环（`windows/AudioManager.cs:116-132`）：

```csharp
byte[] bufferLeft  = new byte[e.BytesRecorded / 2];
byte[] bufferRight = new byte[e.BytesRecorded / 2];
for (int i = 0, j = 0; j < e.BytesRecorded / 2; i += 4, j += 2)
{
    bufferLeft[j]     = e.Buffer[i];      bufferLeft[j + 1] = e.Buffer[i + 1]; // L
    bufferRight[j]    = e.Buffer[i + 2];  bufferRight[j + 1] = e.Buffer[i + 3]; // R
}
```

- 即"抽取"：单声道输出帧长 = 输入帧长 / 2，**不做混音**。
- 声道模式作为 `channelMask` 下发：`Stereo ? 12 : 4`（`windows/Speaker.cs:228`）；枚举值 `None=0, Left=4, Right=8, Stereo=12`（`windows/AudioChannel.cs`）。
  - 注意：NonStereo 分支**恒发 4**（`AudioChannel.Left`）；`Right=8` 与 `Stereo=12` 的取值虽在枚举中，但下发路径只区分 `Stereo` 与"非 Stereo"。重连时以 `ChannelSelected` 决定订阅哪个事件（`:236-245`）。
- 事件分发经 Dispatcher（UI 线程）执行，天然串行化左右两路。
- **Linux 端等价实现**：单次 `parec --raw` 读取整帧后按同一循环拆分；Left/Right 设备分别订阅抽取结果，Stereo 设备直接使用整帧。多设备共用一次采集。

---

## 6. 静音保活（commit dac02fd 语义）

原 commit 变更集中在 Android 侧 + 心跳/广播周期，PC 侧配合如下：

1. **PC → Android 心跳**：每 5 秒（`windows/Model.cs:38-42`）对每个已连接 Speaker 调 `SendHeartbeat()`；距上次写 >5 s 才真正发送 4 字节长度 0 帧（`windows/Speaker.cs:461-474`）。
2. **建连握手期的读循环**：`ReadAsync` 持续消费服务端字节（含 Android 每 10 秒的 1 字节心跳），可及时发现断连（`windows/Speaker.cs:260-274`）。
3. **Android 侧保活**：`lastPCVolume` 记住 PC 下发的音量，若系统音量被外部置 0（如熄屏省电），每 10 秒自动恢复（`android/.../TcpService.java:592-594`）；`AudioTrack` 上 `write` 后 `flush()`（`:475-477`）。

**Linux 特有风险（本项目的核心适配点）**：`parec`/monitor 源在系统输出静音或挂起时**会停止产出数据**（本机探测：monitor 源状态 `SUSPENDED`，且 `pactl list short sources` 显示无活动流），导致 TCP 流长时间静默。Android 端 `AudioTrack` 长时间无数据写入会进入欠载/停工状态，即使之后恢复发送也可能长期无声——即"一段时间后静音"。

因此 Linux 端实现必须：**按实时速率补齐静音 PCM 帧**（0x00），保证帧节拍不断（每帧时间戳对齐 48 kHz 采样时钟），并保留 5 秒长度 0 心跳帧。两种保活数据都不得改变 `N` 为 4 的倍数这一约束。

---

## 7. 局域网发现（UDP 广播）

### 7.1 报文

```
"picapico-audio-share@<listenPort>@<httpPort>"   （UTF-8/ASCII；@8088/@8080 时 = 30 字节）
```
- 生产端：`android/.../TcpService.java:601-612`，每 **10 秒**向 `255.255.255.255` 的 `58261..58270`（10 个端口）各发一份；`BROADCAST_ADDRESS = "255.255.255.255"`（`android/.../NetworkUtils.java:24`），另有 IPv6 常量 `FF02::1`（`:25`，**未在发送路径使用**）。
- 页面地址：`<ip>:<httpPort>`，`httpPort != 80` 时带端口（`android/.../MainActivity.java:141-164`）。HTTP 端口：`getFreePort(斐讯 ? 8090 : 8080)`（`android/.../TcpService.java:272`），斐讯 = `Build.MANUFACTURER.equalsIgnoreCase("phicomm")`；可用性受设置项 `http-server` 控制（`:78-81,369-391`）。
- 接收端（Windows）：在 `58261..58270` 上逐个尝试 `UdpClient(i)` + `EnableBroadcast`，**实际只保留最后一次成功绑定**的 socket（`windows/Model.cs:65-77`），随后循环 `ReceiveAsync`；过滤条件：报文长度 ≤ 26（`:83`，见下方缺陷说明）、按 `@` 分段、`[0] == "picapico-audio-share"`、`int.TryParse([1])` 且 `0 < port < 65535`（`:84-94`）。
  - ⚠️ **未确证**：若发送端只投递到某一个固定端口，而 Windows 端仅保留了其循环中最后成功绑定的端口（通常为 58270），则发现可能仅在端口重合时成功。Linux 端实现将**同时绑定 10 个端口**以覆盖发送端任意端口选择，兼容性更稳（不影响协议字节）。
  - ⚠️ **原版缺陷**：Windows 端阈值 `> 26` 与 Android 端实际报文长度冲突。自 `5c06bac`（2024-03-16）起 Android 报文恒为 `HEAD@port@httpPort`，@8088/@8080 即 **30 字节**，因此原版 Windows 客户端会丢弃**全部**广播。Linux 端为与真实客户端互通，把阈值放宽到 64 字节（见 `Protocol.DiscoveryMaxMessageLength` 的注释）。
- 发现到的设备以 `ip:port` 形式加入设备列表并写入设置（`windows/Model.cs:92,401-417`）。

### 7.2 Linux 网卡过滤（本项目新增约束）

Android 侧对应逻辑在 `android/.../NetworkUtils.java:115-147`：跳过无 MAC 且名字非 `eth0`/`wlan0` 前缀的接口、跳过 `dummy*`、跳过 loopback/未启用接口、跳过长链路本地地址。

Linux 端必须额外过滤容器/虚拟网络，仅对真实物理局域网网卡所在网段进行扫描/绑定：

```
过滤（按接口名）：lo, docker*, veth*, br-*, virbr*, tun*, tap*, tailscale*, wg*, zt*, podman*, cni*, flannel*, kube*, vmnet*, pan*, utun*, sit*, gre*, ip6tnl*, dummy*
过滤（按其它特征）：loopback、!IFF_UP、无 IPv4 非链路本地地址、MAC 为全零/本地管理位、/32 或 /31 前缀
```

### 7.3 端口语义小结

| 端口 | 用途 | 依据 |
| --- | --- | --- |
| 8088（起） | Android TCP 音频/控制服务；文档所称"默认端口" | `android/.../NetworkUtils.java:35-46`、README |
| 8080 / 8090 | Android HTTP 远程管理页（斐讯 8090） | `android/.../TcpService.java:272` |
| 58261-58270 | UDP 发现广播 | `android/.../TcpService.java:605`；`windows/Model.cs:67` |
| 5555 | Wi-Fi adb 连接（回退路径） | `windows/Speaker.cs:322-333` |
| 任意空闲 | USB 模式本地转发端口 | `windows/Utils.cs:59-78`、`windows/Speaker.cs:184-189` |

---

## 8. ADB 交互序列

### 8.1 USB 模式（`windows/Speaker.cs:337-386`、`windows/Model.cs:308-350`）

```
1) adb start-server                     （Utils.EnsureAdb / Model.RefreshSpeakers）
2) adb devices                          （枚举；仅取 State == Online；按 Name 排序）
3) 读取设备展示名：
   adb shell getprop ro.config.marketing_name
   ↑为空则 → adb shell getprop ro.product.manufacturer + " " + device.Model
4) 连接时（Speaker.Connect, _isUSB=true）：
   adb forward --remove tcp:<port> ...（等价 RemoveRemoteForward 按 remote 匹配）
   adb forward tcp:<freePort> localabstract:picapico-audio-share
5) 安装/拉起检查（EnsureDevice(device)）：
   adb shell "dumpsys package com.picapico.audioshare|grep versionName"
   命中 VersionName(=1.7.9) → adb shell "am start -W -n com.picapico.audioshare/.MainActivity"
   未命中：
     adb push <exe同目录>/<exeBaseName>.apk /data/local/tmp/audioshare.apk
     adb shell "/system/bin/pm uninstall com.picapico.audioshare;
                /system/bin/pm install -r /data/local/tmp/audioshare.apk;
                rm -f /data/local/tmp/audioshare.apk;
                dumpsys deviceidle whitelist +com.picapico.audioshare;"
     复查 dumpsys → 命中则 am start，否则提示"app未安装或版本不匹配"
```
- APK 路径推导：**与可执行文件同目录、与可执行文件同基名 + `.apk`**（`windows/Speaker.cs:356-359`）。Windows 发布产物为 `AudioShare.exe` → 期望 `AudioShare.apk`；Linux 端必须保证该命名约定（`AudioShare` → `AudioShare.apk`，并在 `linux/` 内提供回退查找顺序）。
- `Utils.VersionName` = 程序集 `Major.Minor.Build` = `1.7.9`（`windows/Utils.cs:23-31`），与 Android `versionName "1.7.9"`（`android/app/build.gradle:14`）一致性是安装判定的关键。

### 8.2 Wi-Fi 模式（`windows/Speaker.cs:308-335`）

```
1) Utils.PortIsOpen(host, port)（1 s 超时）—— 若已开放，adb 步骤失败也不影响直连
2) adb devices 中查找 Serial 以 host 开头的设备（即 ip:5555）
3) 未找到：若 PortIsOpen(host,5555) 则 adb connect <host>:5555，再查一次
4) 若查到设备 → 走 8.1 的 EnsureDevice(device)（安装/拉起）
5) 结束后若本次是临时 connect 的，disconnect <host>:5555
```

### 8.3 Linux 侧差异

- `adb` 直接用系统 PATH 中的 `adb`（不使用 `adb.exe`；`windows/Utils.cs:154-173` 的 `FindAdbPath` 是 Windows 逻辑，Linux 端改为 `which adb` + 常见路径探测）。
- 所有 adb 命令需 `-s <serial>` 指定设备；`adb shell` 命令中的 `|`、`;` 必须整条作为单个参数传递（等价于 SharpAdbClient 的 `ExecuteRemoteCommandAsync`）。
- 日志中必须记录完整命令行与退出码（验收标准 6）。

---

## 9. 设置持久化与运行期行为

| 项 | Windows | Linux 对应 |
| --- | --- | --- |
| 配置文件 | `%AppData%\AudioShare\audio.share.config.json`（`windows/Settings.cs:36-49`） | `$XDG_CONFIG_HOME/AudioShare/audio.share.config.json`（默认 `~/.config/AudioShare/`） |
| 字段 | `AudioId, AdbDevices[], IPDevices[], SampleRate=48000, Volume=50, IsUSB=true, VolumeFollowSystem=true, Acrylic=true`（`windows/Settings.cs:16-31`） | 同名同义（`Acrylic` 字段保留以兼容旧配置；**1.7.19 起窗口透明功能已删除**，无对应 UI） |
| 设备项 | `{ Id, Channel }`（`windows/Settings.cs:9-19`） | 相同 |
| 启动自动连接 | `MainWindow_Loaded`：对 `Channel != None` 的未连接设备自动连接；全部成功则隐藏窗口（`windows/MainWindow.xaml.cs:194-226`） | 相同 |
| 单实例 | 命名互斥 `_AUDIO_SHARE_MUTEX` + 命名管道 `_AUDIO_SHARE_PIPE` 唤醒已有实例（`windows/App.xaml.cs:12-30`、`windows/MainWindow.xaml.cs:111-127`） | Unix domain socket（`$XDG_RUNTIME_DIR/audioshare.sock`）+ 文件锁，语义相同 |
| 关闭行为 | 取消关闭 → 最小化并隐藏到托盘（`windows/MainWindow.xaml.cs:187-192`） | 相同（`ShutdownMode=OnExplicitShutdown` + 托盘） |
| 断开通知 | Windows Toast + "重新连接"按钮（`windows/Speaker.cs:108-135`、`windows/Model.cs:56-63`） | 托盘气泡通知 + 点击重连（避免引入 Windows Toast 依赖） |
| 日志 | 仅 DEBUG 下 `Trace.WriteLine`（`windows/Logger.cs`） | 写入 `~/.local/state/AudioShare/audioshare.log` + 标准错误 |

---

## 10. Windows UI 元素 → Linux GUI 对照

> 注：下表为协议审计时（1.7.9）的对照。1.7.18/1.7.19 之后界面有精简：
> 窗口透明开关删除、刷新合并、连接/断开合并、管理页改为双击设备行、底部退出按钮删除（托盘退出）。
> 协议与功能语义不变。

| # | `windows/MainWindow.xaml` 元素（行号） | 绑定/行为 | `linux/` 实现 |
| --- | --- | --- | --- |
| 1 | `ui:TitleBar`（:22-24）标题 `{Binding Title}`、图标 | 标题 = 语言文本 + 版本号 | Avalonia 自绘标题栏 + 标题/版本 |
| 2 | `startup` ToggleSwitch（:27-28） | `IsStartup`（开机启动 .lnk） | XDG autostart `.desktop`（`~/.config/autostart/`） |
| 3 | `windowAcrylic` ToggleSwitch（:29-37，仅老版本 Windows 可见） | `AcrylicVisible/ Acrylic` | ~~Linux 映射为窗口透明开关~~（**1.7.19 已删除该功能**，窗口固定不透明） |
| 4 | 状态圆点 + `connected/unconnected + ConnectedCount`（:39-55） | 绿/红点 + 计数 | 同布局同语义 |
| 5 | `audioDeviceSelection` ComboBox `AudioDevices/AudioSelected`（:58-62） | 采集设备枚举 | 输出设备 → `.monitor` 源列表（`pactl`/native 枚举） |
| 6 | 采样率 ComboBox `SampleRates/SampleRate`（:63-65） | 7 档 | 相同 7 档；非 48k 用重采样 |
| 7 | `refresh` 按钮 `RefreshAudiosCommand`（:66-67） | 刷新音频设备 | 相同 |
| 8 | `audioPlaybackDevice` + `USB` ToggleSwitch `IsUSB`（:72-80） | USB/IP 模式切换 | 相同（Linux 下用系统 adb） |
| 9 | `refresh` 按钮 `RefreshSpeakersCommand` + `AdbLoading` ProgressRing（:81-88） | 刷新设备列表 | 相同 |
| 10 | 设备列表 `ListView Speakers`（:90-172） | 每行：ID 文本框（`Display`，`IdReadOnly`）、声道 ComboBox（`Channels/ChannelSelected`，`ChannelEnabled`）、移除按钮（`RemoveCommand`，`RemoveVisible`）、连接（`ConnectCommand`，`ConnectEnabled`）+ 连接中 ProgressRing（`Connecting`）、断开（`DisConnectCommand`，`Connected`） | 相同控件与启用条件 |
| 11 | `add` 按钮 `AddIPSpeakerCommand`（:173-174） | 添加 IP 设备（`192.168.1.2..254:8088` 逐个试填；用尽则隐藏） | 相同逻辑，但**默认值取本机物理网段**（Linux 适配，仍保留 8088 端口） |
| 12 | 音量 Slider + 数值 + `followSystem` ToggleSwitch（:176-183） | `Volume/VolumeCustom/VolumeFollowSystem` | 相同（系统音量跟随改为读取 PipeWire 默认 sink 音量） |
| 13 | `exit` 按钮（:184-185） | 退出应用 | 相同 |
| 14 | 托盘图标（`MainWindow.xaml.cs:162-185`） | 点击唤出窗口 | Avalonia TrayIcon |
| 15 | 语言：`Languages/zh-cn.xaml`、`en-us.xaml`（19 个 key） | 按系统语言选择 | 相同 key 集；按 `LANG/LC_ALL` 选择 |
| 16 | 远程管理页入口（README 描述；Windows 端由 android 广播提供 httpPort） | 打开 `http://<ip>:<httpPort>` | 设备行内"打开管理页"按钮（8080/8090 快捷） |

---

## 11. 关键兼容性风险与 Linux 适配结论

| # | 风险 | 结论/对策 | 依据 |
| --- | --- | --- | --- |
| R1 | monitor 源静默时不产出数据 → Android 长时间无数据而"静音/断连" | **必须按实时速率补齐静音帧** + 5 s 心跳帧；不得仅在静默时停止发送 | §6；commit `dac02fd` |
| R2 | 帧长必须 4 字节对齐 | 静音帧与抽取帧均按 4 字节倍数生成 | `android/.../TcpService.java:449-458` |
| R3 | Android 侧 buffer 扩容逻辑 | 单帧 ≤ `getMinBufferSize` 量级（保守取 10-40 ms） | `android/.../TcpService.java:215-219,455-457` |
| R4 | Android 每 10 s 写 1 字节心跳 | Linux 端保持读循环，避免对端写阻塞 | `android/.../TcpService.java:582-599` |
| R5 | 发现端口不确定（Windows 端只监听最后一个成功端口） | Linux 端**同时绑定 58261-58270**；报文长度阈值**放宽到 64 字节**（安卓自 `5c06bac` 起报文恒为 30 字节，原版 `>26` 会丢弃全部广播） | §7.1；`windows/Model.cs:65-94` |
| R6 | 非 48 kHz 采样率 | 需重采样（PulseAudio 侧可直接请求目标采样率，由服务端重采样） | §2 |
| R7 | 采集设备切换/拔出 | 监听 monitor 源变化，失败时重建流并按原设置重连 | `windows/Model.cs:156-177` |
| R8 | 版本判定字符串 | APK 判定比较 `1.7.9`（`Major.Minor.Build`） | `windows/Utils.cs:23-31`、`android/app/build.gradle:14` |
| R9 | APK 文件名约定 | Linux 侧查找顺序：`<exe目录>/AudioShare.apk` → `linux/AudioShare.apk` → `仓库根/AudioShare.apk` | `windows/Speaker.cs:356-359` |

---

## 12. 未确证项（明确标注）

1. `AudioManager` 采集回调的实际帧大小（NAudio 默认值随设备缓冲变化）——Linux 端**默认定 100 ms 帧（19200 B）**，由真机校准确定：10/20/50 ms 会在斐讯 R1 上大量触发 `write audio busy` 丢帧，100 ms 时约 0.1 次/秒（见 `HANDOVER.md` §6 陷阱 3）。
2. Windows 发现监听只保留单一端口（`windows/Model.cs:65-77`）与发送端多端口投递之间的实际匹配关系，未经真机验证；Linux 端采用更稳的多端口绑定。
3. Android 端 `AudioTrack.write` 的 `bufferSizeInBytes` 与 PC 帧长的最优匹配关系未做真机压测。
4. `BROADCAST_ADDRESS_V6 = "FF02::1"` 是否在其它分支使用：当前源码未见发送路径使用 IPv6 广播。
