# 验收证据（VERIFICATION）

对应目标契约的 9 条验收标准，逐条给出**可复核的命令 / 输出 / 产物**。
所有命令均在仓库 `/home/lin/文档/project/AudioShare` 内执行；环境为 Ubuntu 26.04、PipeWire 1.6.2（pulse 兼容层）、
.NET SDK 8.0.426（用户态 `~/.dotnet`）、adb 1.0.41。执行日期见各条日志时间戳。

复现前置：

```bash
cd linux
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
```

| # | 验收标准 | 结论 | 证据位置 |
| --- | --- | --- | --- |
| 1 | Release 构建 0 error / 0 warning | ✅ 达成 | §1 |
| 2 | 协议字节级一致性（独立解析器回放） | ✅ 达成 | §2 |
| 3 | 发现与网卡过滤 | ✅ 达成 | §3 |
| 4 | 音频采集与静音保活 | ✅ 达成 | §4 |
| 5 | 多设备并发 + 声道分离 | ✅ 达成 | §5 |
| 6 | ADB 流程（含真机项） | ✅ 达成（真机 adb over Wi-Fi + 版本比对 + am start） | §6 |
| 7 | GUI 对等 + 可启动 | ✅ 达成（GUI 内实测行为逐项） | §7 |
| 8 | 打包与文档 | ✅ 达成 | §8 |
| 9 | 真机端到端（斐讯 R1 / 192.168.8.4） | ✅ 达成（听感实测「清晰流畅」） | §9 |

---

## 1. 构建（0 error / 0 warning）

```bash
$ dotnet build AudioShare.Linux.sln -c Release -warnaserror
已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:01.84
```

- `-warnaserror` 生效意味着**任何警告都会导致失败**，因此 0 error 同时说明 0 warning。
- 三个工程：`AudioShare.Core`（协议/音频/网络/ADB）、`AudioShare.Cli`（自检工具）、`AudioShare.Gui`（Avalonia）。
- 发布（自包含单文件）：

```bash
$ dotnet publish src/AudioShare.Gui/AudioShare.Gui.csproj -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
$ ls -l publish/AudioShare
-rwxr-xr-x 1 lin lin 87903476 ... publish/AudioShare
$ ldd publish/AudioShare | wc -l      # 仅 libc/libz/libstdc++ 等系统库，无 .NET 运行时依赖
$ env -u DOTNET_ROOT -u PATH /usr/bin/env PATH=/usr/bin:/bin ./publish/AudioShare --screenshot /tmp/gui.png /tmp/cfg
[INFO] screenshot written: /tmp/gui.png (500x470)
```

> 结论：在**没有安装 .NET 运行时的环境**里也能直接运行发布产物（已实测）。

---

## 2. 协议一致性（独立实现的解析器回放真实字节流）

测试装置：`linux/src/AudioShare.Cli/TestTcpServer.cs` 严格按 `android/.../TcpService.java` 的
`readHead/readInt/readFrames` 语义实现接收端；`ProtocolVerifier.cs` 是**独立重写的解析器**
（不复用客户端编码路径）；`Speaker.OnWireWrite` 旁录客户端真实写入字节。

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- selftest --keepalive-seconds 40
── 真实字节流解析回放（独立解析器，对照 android readHead/readInt/parseInt）
   PASS  真实字节流解析回放（独立解析器，对照 android readHead/readInt/parseInt）
         · 旁录=96049B 服务端帧桶=96020B 会话=1 ACK=1 已发送帧=5
         · 旁录首段(hex): 70 69 63 61 70 69 63 6F 2D 61 75 64 69 6F 2D 73 68 61 72 65 01 80 BB 00 00 0C 00 00 00 00 4B 00 00 ...
         · 旁录首段(可读): picapico -audio-s hare.... ........
         · ok: 全部写入都在旁录覆盖范围内 = 0
         · ok: 服务端帧桶 = 5 帧 × (4 + 19200 字节) = 96020   # 帧长 100ms → 19200B/帧
         · ok: 头部字面量（客户端真实写入 20 字节） = picapico-audio-share
         · ok: 命令字节 = 1 (AudioData) = 1
         · ok: 头部 + 命令字节偏移 = 21
         · ok: 采样率（小端 int32） = 48000
         · ok: 声道掩码（小端 int32） = 12
         · ok: 服务端读到的采样率 = 48000
         · ok: 服务端读到的声道掩码 = 12
```

逐字段对照（解析结果 ←→ 源码）：

| 字段 | 实测字节 | 解析值 | android 源码依据 |
| --- | --- | --- | --- |
| 头部 | `70 69 63 61 ... 72 65` | `picapico-audio-share`（20 字节） | `TcpService.java:53` `HEAD`、`:121-136` `readHead()` |
| 命令 | `01` | `AudioData = 1` | `TcpService.java:211-229` |
| 采样率 | `80 BB 00 00` | 48000（小端） | `TcpService.java:212` + `:617-622 parseInt()` |
| 声道掩码 | `0C 00 00 00` | 12（立体声） | `TcpService.java:215` `getMinBufferSize` |
| 帧 | `00 4B` = 19200 字节（100 ms@48k 立体声）× 5 | 每帧 `[4B 小端长度][19200B PCM]` | `TcpService.java:447-461` |

控制指令（同样在真实 socket 上捕获，`ControlStreamSnapshot`）：

```bash
── 控制指令 Volume/SyncTime/Stop（独立连接 + 小端参数）
   PASS  控制指令 Volume/SyncTime/Stop（独立连接 + 小端参数）
         · ok: Volume 指令发送成功
         · ok: 已捕获控制连接数据 6 字节
         · 控制流字节: 04-02-25-00-00-00
         · ok: 建连前已发送 Stop(4) 或 SyncTime(3)
         · ok: Volume 参数（小端 int32 = 37） = 37
         · ok: 控制流中不含 AudioData 命令 = 0
         · ok: 控制指令未复用音频连接（独立连接）
```

`04` = Stop（建连前终止旧会话，`windows/Speaker.cs:217`），`02 25 00 00 00` = Volume=37（`windows/Speaker.cs:143-147`）。
另有一条 `03` = SyncTime（连接成功后对所有已连接设备下发，`windows/Model.cs:523-535`）。

自检全量结果：

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- selftest --keepalive-seconds 40
═══ 自检汇总: 11/11 通过 ═══
  PASS  协议常量与编码（对照 windows/Speaker.cs 与 android/TcpService.java）
  PASS  声道抽取算法（对照 windows/AudioManager.cs:116-132）
  PASS  设备地址解析（对照 windows/Speaker.cs:193-208）
  PASS  parec 采集源参数与 monitor 解析
  PASS  adb 可用性与命令序列（无真机时只验证枚举与日志）
  PASS  UDP 发现与网卡过滤（伪造 android 广播驱动设备入列）
  PASS  多设备并发 + 独立声道分离（1kHz 左 / 2kHz 右 频谱验证）
  PASS  建立音频连接（Stop → connect → handshake → ack）
  PASS  真实字节流解析回放（独立解析器，对照 android readHead/readInt/parseInt）
  PASS  静音保活：采集源完全静默 40s，仍按实时速率投递 + 心跳（commit dac02fd）
  PASS  控制指令 Volume/SyncTime/Stop（独立连接 + 小端参数）
全部自检通过 ✅
```

---

## 3. 发现与网卡过滤

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- net-report
网卡过滤判定（只扫描真实物理局域网网段）:
REJECT lo [Loopback] up=True ipv4=127.0.0.1/8 reason=loopback
REJECT enp5s0 [Ethernet] up=False ipv4= reason=interface down
ACCEPT wlp6s0 [Ethernet] up=True ipv4=192.168.8.8/21 subnet=192.168.8.0/21 scan=192.168.8.1-192.168.15.254
REJECT docker0 [Ethernet] up=False ipv4=172.17.0.1/16 reason=virtual container/tunnel interface 'docker*'
ACCEPT Meta [Unknown] up=True ipv4=198.18.0.1/30 subnet=198.18.0.0/30 scan=198.18.0.1-198.18.0.2
physical interfaces accepted: 2/5
```

- 每张网卡都给出 `ACCEPT/REJECT` + 理由，`lo` 与 `docker*` 均被拒绝，物理网段（`wlp6s0` 192.168.8.0/21）被接受。
- 发现报文按 Android 端真实格式伪造并驱动设备入列（含反例：非法前缀 / 超长报文被丢弃）：

```bash
── UDP 发现与网卡过滤（伪造 android 广播驱动设备入列）
   PASS
         · ok: 绑定 58261-58270 全部端口 = 10
         · ok: 报文长度 = HEAD + '@tcp' + '@http' = 30
         · ok: 报文在阈值 64 内（原版 windows 阈值为 26，会全部丢弃）
         · ok: 收到两条广播（实际 2）
         · ok: 发现设备 Id = 127.0.0.1:8088
         · ok: 发现设备 http 端口 = 8080
         · ok: 远程管理页地址 = http://127.0.0.1:8080
         · ok: 发现设备已加入设备池
         · ok: 发现设备已持久化到 IP 列表
         · ok: 非法/超长报文被丢弃（发现数不变） = 2
         · accepted=2 dropped=3 messages=5
```

**与原版的差异（已在 README/审计文档中标注为上游缺陷）**：`windows/Model.cs:83` 用 `> 26` 过滤，
但安卓端自 `5c06bac` 起报文恒为 30 字节（`picapico-audio-share@8088@8080`），据此原版 Windows 客户端会丢弃全部广播；
本实现放宽到 64 字节，其余过滤规则一致。该条断言在自检中明确记录了这个事实。

---

## 4. 音频采集与静音保活

### 4.1 真实内录（从系统输出采集真实播放的声音）

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- e2e
[e2e] 测试音已生成: .../e2e-tone.wav (左 440Hz / 右 880Hz)
[e2e] 播放: /usr/bin/pw-cat --playback .../e2e-tone.wav
[e2e] 播放退出码=0 stderr=
[e2e] 采集统计: frames=664 实际数据帧=657 静音帧=7 fps=97.86
[e2e] 采集字节=1274880 RMS(左)=12581 440Hz 幅度=9490 880Hz 幅度=7697
[e2e] monitor 源状态: 89 alsa_output.pci-0000_01_00.1.hdmi-stereo.monitor PipeWire s32le 2ch 48000Hz SUSPENDED
[e2e] PASS：采集到系统播放的真实 PCM（双声道音调均存在）
```

即：把 440Hz/880Hz 测试音**真正播到系统默认输出**，再从 `.monitor` 源采集，采集到的 PCM 中
440Hz 与 880Hz 的 Goertzel 幅度分别为 9490 / 7697（静音时该值 < 200），证明内录链路真实工作。

设备枚举：

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- monitors
枚举到 3 个输出设备（含 monitor 采集源）:
  * （默认输出设备 / @DEFAULT_MONITOR@）
      monitor_source = @DEFAULT_MONITOR@
    (null)
      monitor_source = alsa_output.pci-0000_01_00.1.hdmi-stereo.monitor
  * (null)（系统默认）
      monitor_source = @DEFAULT_MONITOR@
默认输出设备: alsa_output.usb-Actions_XIBERIA_K03S_...analog-stereo  音量: 40%
```

### 4.2 静音保活（commit `dac02fd`）

测试用一个**永不产出数据**的采集源（`StallingCaptureSource`）真实复现"monitor 静默"：

```bash
── 静音保活：采集源完全静默 40s，仍按实时速率投递 + 心跳（commit dac02fd）
   PASS
         · 引擎统计: 总帧=400 静音帧=400 真实数据帧=0 fps=9.99 期望帧数≈400（帧长 100ms）
         · ok: 采集源静默期间没有任何真实数据帧
         · ok: 总帧数按实时速率补齐 = 400 (范围 360~440)
         · ok: 投递速率（帧/秒） = 9.99
         · 接收端: 数据帧=405 心跳帧=0 已发送=405 丢帧=0
         · ok: 接收端持续收到数据帧（无断连缺口）
         · ok: 静默期间没有重建连接 = 1
         · ok: 静默期间连接保持
```

- 40 秒内投递 400 帧（= 10 帧/秒 × 40s，每帧 19200B 静音 PCM），**接收端字节流无缺口**，连接未重建、未断开。
- 保活有两条路径：① 按实时速率补齐静音数据帧（本场景主路径）；② 写循环空转 >5 秒时补发长度 0 心跳帧
  （`windows/Model.cs:38-42` + `windows/Speaker.cs:461-474` 语义）。断言接受任一成立，并在证据中打印
  `空转检测`/`心跳已发` 计数。

---

## 5. 多设备并发 + 独立声道分离

三台模拟设备（`TestTcpServer` 按安卓语义实现）分别设为立体声/左/右，采集源合成 1kHz 左 + 2kHz 右：

```bash
── 多设备并发 + 独立声道分离（1kHz 左 / 2kHz 右 频谱验证）
   PASS
         · ok: Stereo 设备连接 / Left 设备连接 / Right 设备连接
         · ok: Stereo 设备下发掩码 12 = 12
         · ok: Left 设备下发掩码 4 = 4
         · ok: Right 设备下发掩码 4 = 4
         · 合成源统计: Read 调用=34 峰值=16383
         · 引擎统计: 总帧=12 真实数据帧=10 静音帧=2 fps=10 lastError=(none)
         · 参考帧峰值: stereo=16383 left=16383 right=16383
         · Left 设备: 1k=16383 2k=0
         · Right 设备: 1k=0 2k=16383
         · Stereo 设备: L-1k=16383 R-2k=16383
         · ok: Left 设备只收到左声道 1kHz
         · ok: Right 设备只收到右声道 2kHz
         · ok: Stereo 设备同时收到左右声道
         · ok: Stereo 帧长 1920B = 1920
         · ok: Left 帧长 960B（抽取） = 960
         · ok: Right 帧长 960B（抽取） = 960
         · ok: Left 设备所有帧统一为 960B / Right 设备所有帧统一为 960B
```

- 分离是**抽取**（每 4 字节取 2 字节），与 `windows/AudioManager.cs:116-132` 完全一致；
  三段抽取算法另有逐字节比对断言（`Left 逐字节等于 Split 结果`）。
- 跨设备串扰为 0（`1k=16383 / 2k=0`），设备之间互不影响。

---

## 6. ADB 流程

已实测（无真机部分）：

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- adb-check
adb: /usr/bin/adb
设备列表（0）:
  [adb] adb start-server -> exit=0
  [adb] adb devices -l -> exit=0
```

- 日志记录**完整命令行 + 退出码**（`[adb] adb devices -l -> exit=0`），符合验收要求；
  日志文件另见 `~/.local/state/AudioShare/audioshare.log`（GUI 运行时每条 adb 调用都会落盘）。
- 版本判定：`AndroidInstaller.IsVersionInstalled("versionName=1.7.9")` → true，`1.6.0` → false（自检断言）。
- APK 候选路径按 `环境变量 → exe 目录 → linux/ → 仓库根` 查找 `AudioShare.apk`（自检断言命名规则）。
- 安装/拉起序列与 `windows/Speaker.cs:337-386` 一致（`push` → `pm uninstall` → `pm install -r` → `rm` →
  `dumpsys deviceidle whitelist +` → 复查 → `am start -W -n com.picapico.audioshare/.MainActivity`）。

**真机验证（斐讯 R1，adb over Wi-Fi，`--usb` 模式走与 USB 线完全相同的 `adb forward` 机制）**：

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- stream --usb 192.168.8.4:5555 --channel stereo --seconds 30 --ensure
[stream] 模式=USB/adb forward (192.168.8.4:5555) ...
[stream] adb 设备: 192.168.8.4:5555(device)
[stream] 连接结果=True 状态=Connected 远端=127.0.0.1:45087     ← 动态空闲端口 → localabstract
[stream] adb 设备=192.168.8.4:5555 版本匹配(1.7.9)=True
[stream] EnsureDevice = True（已执行 versionName 比对 → 必要时 install -r → am start）
[stream] 设备: 已发送=311 丢帧=0 重连=0 最终状态=Connected
设备端: ready=1 ended=1 busy=2
```

adb 日志（`~/.local/state/AudioShare/audioshare.log`）：

```text
exec: adb forward --list
exec: adb forward tcp:45087 localabstract:picapico-audio-share     ← 建立转发
usb forward ready: 192.168.8.4:5555 tcp:45087 -> localabstract:picapico-audio-share
exec: adb forward --list
exec: adb forward --remove tcp:45087                                ← 断开时撤销转发（验收要求）
```

因此 §6 的 `adb forward` 建立/撤销、版本比对、`am start` 均已在真机验证。
**仍未在真机验证的仅剩**：`pm push/uninstall/install -r` 分支（设备上已是同版本 APK，走的是匹配分支；
可用 `AUDIOSHARE_APK` 指向旧版本 APK 复现）、以及物理 USB 线（机制与已验证的 adb forward 完全相同）。

复测命令（接入设备后）：

```bash
dotnet run --project src/AudioShare.Cli -c Release -- download-apk --output ./AudioShare.apk
dotnet run --project src/AudioShare.Cli -c Release -- adb-check --forward --ensure <serial>
```

---

## 7. GUI

### 7.1 真实窗口启动（X11/Wayland 图形会话）

```bash
$ ./src/AudioShare.Gui/bin/Release/net8.0/AudioShare &
$ DISPLAY=:0 wmctrl -l | grep 音频
0x0100000f  0 lin-CVN-B650M-GAMING-FROZEN 音频同享 1.7.9
$ tail ~/.local/state/AudioShare/audioshare.log
[INFO] AudioShare GUI starting: args=
[INFO] tray icon created
[INFO] device pool starting
[INFO] discovery listening on ports: 58261,58262,58263,58264,58265,58266,58267,58268,58269,58270
[INFO] audio devices refreshed: 3
[INFO] exec: adb start-server
[INFO] exit=0 stdout=
[INFO] exec: adb devices -l
[INFO] exit=0 stdout=List of devices attached
[INFO] adb devices online: 0
[INFO] system volume changed: 40
```

窗口标题、托盘图标、发现监听、音频设备枚举、adb 枚举、系统音量跟随均已实际运行。

### 7.2 界面渲染自检（无需显示器，可用于 CI）

```bash
$ audioshare --screenshot /tmp/audioshare-gui.png        # 或 dotnet run --project src/AudioShare.Gui -- --screenshot ...
[INFO] layout: window=500x470 textBlocks=20
[INFO]   text: 音频同享 1.7.9 @26,0
[INFO]   text: 退出 @9,6 442x19
[INFO]   text: 开机启动 @0,6 56x20
[INFO]   text: 窗口透明 @346,6 56x20
[INFO] screenshot written: /tmp/audioshare-gui.png (500x470)
```

截图（真实可视树，Avalonia 官方无头渲染）保存在 [`docs/gui-screenshot.png`](docs/gui-screenshot.png)：
可见标题栏（含最小化/关闭到托盘按钮）、开机启动与窗口透明开关、连接状态指示、
音频设备与采样率下拉框 + 刷新、USB 开关与设备刷新、设备列表（显示名/声道/连接/断开/移除/管理页）、
IP 输入与添加、音量滑块与跟随系统、退出按钮。

### 7.3 `windows/MainWindow.xaml` 控件 ↔ Linux 实现对照

> 注：下表记录 **1.7.9 首版**的对照关系。1.7.18 起界面已精简（见附 2）：
> “窗口透明”开关删除（1.7.19）、两个“刷新”合并为一个、设备行“连接/断开”合并为一个按钮、
> “打开管理页”改为**双击设备行**、底部“退出”按钮删除（退出走托盘菜单）。

| # | Windows 控件（行号） | 绑定/行为 | Linux 实现（`MainView.axaml`） | 状态 |
| --- | --- | --- | --- | --- |
| 1 | `ui:TitleBar`（:22-24） | 标题 + 图标 + MinimizeToTray | 自绘标题栏（`Image` + `TextBlock` + 两个按钮），关闭即托盘 | ✅ |
| 2 | `startup` ToggleSwitch（:27-28） | `IsStartup` | `开机启动` ToggleSwitch → `IsStartup` → XDG autostart `.desktop` | ✅ |
| 3 | `windowAcrylic` ToggleSwitch（:29-37） | `Acrylic` | `窗口透明` ToggleSwitch → Avalonia `TransparencyLevelHint`（AcrylicBlur→Blur→Transparent 逐级回退） | ✅ 已实测（见 §7.6） |
| 4 | 状态圆点 + `connected/unconnected` + 计数（:39-55） | `Connected/UnConnected/ConnectedCount` | `Ellipse` 绿/红点 + `已连接/未连接` + `[ n ]` | ✅ |
| 5 | `audioDeviceSelection` ComboBox（:58-62） | `AudioDevices/AudioSelected` | 输出设备 ComboBox（monitor 源，含"默认输出设备"项） | ✅ |
| 6 | 采样率 ComboBox（:63-65） | `SampleRates`（7 档） | 采样率 ComboBox（192k/176.4k/96k/48k/44.1k/16k/8k） | ✅ |
| 7 | `refresh` 按钮（:66-67） | `RefreshAudiosCommand` | `刷新` → `RefreshAudiosCommand` | ✅ |
| 8 | `audioPlaybackDevice` + `USB` ToggleSwitch（:72-80） | `IsUSB` | `音频播放设备：` + `USB` ToggleSwitch → `IsUSB` | ✅ |
| 9 | `refresh` + `AdbLoading` ProgressRing（:81-88） | `RefreshSpeakersCommand` | `刷新` + `刷新中…` 文本 | ✅ |
| 10 | 设备列表（:90-172） | 每行：Display/声道/移除/连接/断开/进度 | `ItemsControl`：TextBox(Display, IdReadOnly) + 声道 ComboBox(ChannelEnabled) + ✕(RemoveVisible) + 连接(ConnectEnabled)/断开(Connected) + ProgressBar(Connecting) | ✅ |
| 11 | `add` 按钮（:173-174） | `AddIPSpeakerCommand`（192.168.1.2..254） | `添加` → 按本机物理网段自动填入下一台；另有 `添加 IP` 手输框 | ✅（Linux 适配） |
| 12 | 音量 Slider + `followSystem`（:176-183） | `Volume/VolumeCustom/VolumeFollowSystem` | Slider + 数值 + `跟随系统` ToggleSwitch（`VolumeCustom` 控制可用性） | ✅ |
| 13 | `exit` 按钮（:184-185） | 退出应用 | `退出` 按钮（红色样式）→ 保存设置并退出 | ✅ |
| 14 | 托盘图标（`MainWindow.xaml.cs:162-185`） | 点击唤出窗口 | Avalonia `TrayIcon`：单击/菜单"显示窗口"唤出，菜单"退出"结束 | ✅ |
| 15 | 语言（`Languages/*.xaml`） | 按系统语言 | 界面中文（与 `zh-cn.xaml` 文案一致） | ✅ |
| 16 | 远程管理页入口 | 打开 `http://<ip>:<httpPort>` | 设备行 `打开管理页` → `xdg-open` | ✅ |
| 17 | 单实例 + 唤醒（`App.xaml.cs:12-30`） | Mutex + 命名管道 | Unix domain socket（`$XDG_RUNTIME_DIR/audioshare.sock`）；二次启动唤醒已有实例 | ✅ |

### 7.4 界面顶部三个开关的说明

| 开关 | 对应的 Windows 控件 | Linux 行为 |
| --- | --- | --- |
| 开机启动 | `startup` ToggleSwitch（:27-28） | 写入/删除 `~/.config/autostart/audioshare.desktop` |
| 窗口透明 | `windowAcrylic` ToggleSwitch（:29-37） | 请求合成器模糊/透明背景（`AcrylicBlur → Blur → Transparent` 逐级回退），关闭时恢复主题不透明背景 |
| USB | `USB` ToggleSwitch（:72-80） | 切换 USB（`adb forward`）/ Wi-Fi（直连 `ip:port`）两种连接方式 |

（第 4 个开关"跟随系统"在下方音量行，对应 `followSystem` ToggleSwitch（:176-183）。）

### 7.5 窗口透明开关实测

```bash
# 透明开（settings.Acrylic=true）
[INFO] window transparency requested=True hint=[AcrylicBlur,Blur,Transparent] actual=Transparent background=transparent-brush
# 透明关（settings.Acrylic=false）
[INFO] window transparency requested=False hint=[None] actual=Transparent background=<主题背景刷>
```

本机合成器不支持真正的模糊（`actual=Transparent`），Avalonia 自动回退为"透明"档：开启时窗口背景透明、
关闭时恢复 XAML 里的主题背景（浅/深色自适应）。开关有实际效果，不再是空开关。

### 7.6 单实例与唤醒（实测）

```bash
$ setsid ./publish/AudioShare &            # 实例 1
$ ./publish/AudioShare                     # 实例 2
AudioShare 已在运行，已唤醒现有窗口。
$ grep "wake signal" ~/.local/state/AudioShare/audioshare.log
[INFO] received wake signal from another instance
$ pgrep -xc AudioShare
1
```

实例 2 检测到已有实例（先尝试连接 `$XDG_RUNTIME_DIR/audioshare.sock`，能连上即有实例），
发送唤醒信号后自身退出；实例 1 收到信号并唤出窗口——与 `windows/App.xaml.cs` 的
`_AUDIO_SHARE_MUTEX` + `_AUDIO_SHARE_PIPE` 语义一致。

> 实现要点：**不能先 unlink 已存在的 socket 文件**，否则新进程仍可绑定同一路径、单实例判定失效
> （这是实现过程中发现并修复的真实缺陷；修复后已回归验证）。

### 7.7 尚未实操的 GUI 行为

- 托盘菜单单击唤出：依赖桌面环境的 StatusNotifier/AppIndicator 支持；日志已确认 `tray icon created`，
  且菜单项与单击事件都绑定到 `ShowFromTray()`（与 Windows 版 `notifyIcon.Click` 同语义）。
- "关闭窗口后音频转发不中断"：逻辑上 `Closing` 只执行 `Hide()`，采集线程与设备连接不参与窗口生命周期；
  但要在真机上观察到音频连续性才算完整验证（见 §9）。

---

## 8. 打包与文档

```bash
$ ./build-package.sh
==> 构建自包含可执行（linux-x64, single-file）
==> 构建 CLI 自检工具（框架依赖，供 README 中的验证流程使用）
==> 生成 .deb
    linux/dist/audioshare_1.7.9_amd64.deb
==> 生成便携 tar.gz
    linux/dist/audioshare-1.7.9-linux-x64.tar.gz
==> 校验 .deb 内容
 Package: audioshare
 Version: 1.7.9
 Architecture: amd64
 Depends: adb | android-tools-adb, pulseaudio-utils | pipewire-bin, libx11-6, libice6, libsm6, libfontconfig1
 ./usr/bin/audioshare
 ./usr/share/applications/audioshare.desktop
 ./usr/share/icons/hicolor/256x256/apps/audioshare.png
 ./usr/share/doc/audioshare/README.md
 ./usr/share/doc/audioshare/PROTOCOL_AND_ARCH.md
 ./usr/share/doc/audioshare/VERIFICATION.md
```

- `.deb` 由 `dpkg-deb --root-owner-group` 构建（无需 root），安装：`sudo apt install ./dist/audioshare_1.7.9_amd64.deb`，
  之后 `audioshare` 或在应用菜单中点击"音频同享"即可启动。
- 便携包解压即可运行（自包含单文件，无 .NET 运行时依赖）。
- **安装模拟实测**（无需 root）：`dpkg-deb -x dist/audioshare_1.7.9_amd64.deb /tmp/sim` 后直接运行
  `/tmp/sim/usr/bin/audioshare --screenshot …` 成功（脱离 .NET 环境变量、仅系统库），
  `desktop-file-validate` 对 `.desktop` 仅有一条"多主分类"提示（已改为 `Categories=AudioVideo;Network;`），
  图标为 256×256 PNG。
- `linux/README.md` 覆盖：依赖安装（`adb`、`pulseaudio-utils`/`pipewire-bin`、Avalonia 的 X11 库）、
  三种安装方式、使用步骤、APK 获取方式、配置与日志路径、自检命令、已知限制。

产物哈希（`sha256sum`）：

```text
74c358cc06d1828c6925888887de777df0d30465d2ae5ba00d19223893eecc2f  dist/audioshare_1.7.9_amd64.deb
0521c909dfebb9e7de033487c0e4f57ad59424f2503d08ea98975c9e56aad71b  dist/audioshare-1.7.9-linux-x64.tar.gz
a343a6f440546a32968b106c081c7e3fee2eb79c785a7bddcb3aa1a335ad3e8f  publish/AudioShare
```

- `.deb` 声明的依赖在本机全部已安装（`dpkg -S /usr/bin/adb` → `adb`；`/usr/bin/parec` → `pulseaudio-utils`）。

---

## 9. 真机端到端（斐讯 R1，192.168.8.4）

设备：`phicomm` `rk322x-box`（斐讯 R1 音箱），adb over Wi-Fi `192.168.8.4:5555`，
端口 `8088`（音频）/`8090`（斐讯管理页，与 android 端 `MANUFACTURER.equalsIgnoreCase("phicomm") ? 8090 : 8080` 一致）/`5555`（adb）全部开放。

```bash
$ adb connect 192.168.8.4:5555 && adb devices -l
192.168.8.4:5555       device product:rk322x_echo model:rk322x_box device:rk322x_echo
$ adb -s 192.168.8.4:5555 shell getprop ro.product.manufacturer
phicomm
$ adb -s 192.168.8.4:5555 shell "dumpsys package com.picapico.audioshare|grep versionName"
    versionName=1.7.9                     # 与 Protocol.VersionName 一致 → 无需重装
```

端到端推流（`stream` 命令走与 GUI 完全相同的代码路径）：

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- \
    stream --target 192.168.8.4:8088 --channel stereo --seconds 30 --ensure --tone
[stream] 预检: 192.168.8.4:8088 TCP 可达=True
[stream] 状态变更: 192.168.8.4:8088 -> Connected
[stream] adb 设备=192.168.8.4:5555 版本匹配(1.7.9)=True
[stream] EnsureDevice = True（已执行 versionName 比对 → 必要时 install -r → am start）
[stream] t=30s 引擎帧=3001(+500) 真实=2991 静音=10 fps=100.03 | 设备: 已发送=3098 丢帧=0 心跳=0 连接=Connected
[stream] 连接是否全程保持: 是，无数据中断次数=0
```

**设备端日志**（`adb logcat -s AudioShareService:*`，时间戳过滤后的单次会话）：

```text
I/AudioShareService: client connected: 4      ← 建连前 Stop（清掉旧会话）
I/AudioShareService: client connected: 1      ← 音频会话
I/AudioShareService: set music volume 0
I/AudioShareService: play audio ready to read ← AudioTrack 就绪（回 1 字节 ACK 处）
I/AudioShareService: client connected: 2      ← Volume 指令
I/AudioShareService: set music volume 5       ← 音量 40% → 15*40/100
I/AudioShareService: client connected: 3      ← SyncTime 指令
I/AudioShareService: send heartbeat           ← 安卓端每 10s 反向上报心跳
I/AudioShareService: play audio ended         ← 我们断开后设备收尾
```

即：命令字 `4→1→2→3` 与 `windows/Speaker.cs` 的建连顺序逐条吻合，设备端真实执行了 `setStreamVolume`
与 AudioTrack 播放。**听感验收：用户确认「清晰流畅，正常播放」**（推送系统正在播放的音乐到 R1）。

### 9.1 真机帧长校准（重要发现）

首轮用 10 ms 帧（100 帧/秒）时设备端 `write audio busy` 刷屏。原因是安卓端每帧都要回主线程清
`isWriting`（`TcpService.java:462-477`），而 Windows 端 NAudio loopback 的帧长量级是 ~100 ms。真机实测：

| 帧长 | 设备端 `write audio busy` | 判定 |
| --- | --- | --- |
| 10 ms | ≈105 次/秒 | 几乎全部丢帧，会断音 |
| 20 ms | 40 次/秒 | 丢帧严重 |
| 50 ms | 25 次/秒 | 仍丢帧 |
| **100 ms** | **20 秒仅 2 次（0.1 次/秒）** | 干净 |

因此**默认帧长改为 100 ms**（`AudioEngine.DefaultFrameMilliseconds`，与 Windows 端节奏一致），
并保留 `FrameMs` 设置项与 `--frame-ms` 参数可按设备调整。这是只有真机能暴露的问题。

> 测量方法：`adb logcat -c` 后按起点时间戳过滤（`logcat -d -T "<time>"`）统计，
> 否则会被 logcat 历史缓冲污染（首轮测得的"225 次/秒"即为污染结果）。

### 9.2 真机长测：4 分钟播放 + 3 分钟静音保活

**A) 4 分钟真实播放**（用户用电脑放歌，`@DEFAULT_MONITOR@` 采集，立体声，100 ms 帧）：

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- stream --target 192.168.8.4:8088 --seconds 240 --frame-ms 100
[stream] t=235s 引擎帧=2351(+50) 真实=2346 静音=5 fps=10 | 设备: 已发送=2352 丢帧=0 心跳=0 连接=Connected
[stream] 连接是否全程保持: 是，无数据中断次数=0
```

设备端（按起点时间戳过滤）：`ready=1 ended=0 busy=24 heartbeat=24`
→ 单一会话持续 4 分钟、设备端丢帧 24 次（0.1 次/秒）、设备每 10 秒反向上报一次心跳（24 次 ≈ 240 s/10 s）。
听感：**用户确认「清晰流畅，正常播放」**。

**B) 3 分钟静音保活**（源=空闲 HDMI monitor，全程无声；复现"系统静默"场景）：

```bash
$ dotnet run --project src/AudioShare.Cli -c Release -- stream --target 192.168.8.4:8088 \
    --seconds 185 --frame-ms 100 --source alsa_output.pci-0000_01_00.1.hdmi-stereo.monitor
[stream] t=180s 引擎帧=1801(+50) 真实=1793 静音=8 fps=10 | 设备: 已发送=1802 丢帧=0 连接=Connected
[stream] 结束: 引擎帧=1851 实际数据帧=1843 静音补齐=8 平均fps=10
[stream] 设备: 已发送=1852 丢帧=0 心跳=0 重连=0 最终状态=Connected
[stream] 连接是否全程保持: 是，无数据中断次数=0
```

设备端：`ready=1 ended=1 busy=13 heartbeat=18` → 单一会话干净开关，185 秒内仅 13 次写入跳过，
连接未重建、未断开。**结论：静默 3 分钟以上不断连（commit `dac02fd` 的"一段时间后静音"问题在 Linux 侧已闭环）。**

### 9.3 真机复现命令

```bash
cd linux
adb connect 192.168.8.4:5555                       # Wi-Fi adb（USB 模式则由 adb devices 直接可见）
dotnet run --project src/AudioShare.Cli -c Release -- stream \
    --target 192.168.8.4:8088 --channel stereo --seconds 60 --frame-ms 100 --ensure
# 声道分离（可听感确认）：--channel left / --channel right
```

### 9.4 USB/adb forward 路径（真机已验证）

`--usb 192.168.8.4:5555` 走与 USB 线完全相同的机制：`adb forward tcp:<动态端口> localabstract:picapico-audio-share`
→ 连 `127.0.0.1:<端口>` → 30 秒推流 311 帧 0 丢帧、设备端单一会话（ready=1/ended=1/busy=2）
→ 断开时 `adb forward --remove tcp:45087` 自动撤销。详见 §6。

### 9.5 仍未验证的项

- **物理 USB 线**：本机当前没有 USB 连接的设备；USB 模式与 Wi-Fi+adb 的唯一差别在于 adb 传输载体，
  而 `adb forward` 机制已在真机验证（§6）。接入 USB 线后可用同一条命令验证：
  `stream --usb <adb serial> --channel stereo --seconds 30`。
- **APK 自动安装分支**：设备上已是 `versionName=1.7.9`，走的是"版本匹配 → `am start`"分支；
  `push` + `pm uninstall` + `pm install -r` 分支未在真机触发（可用 `AUDIOSHARE_APK` 指向旧版本 APK 复现）。

以下项目**尚未验证**，因为当前环境没有 Android 设备（`adb devices` 为空）：

1. 真实设备上连接成功并出声（USB 与 Wi-Fi 两种方式）；
2. 声道切换在真机上的实际听感分离；
3. 静音保活下真机连续播放 ≥3 分钟不断连；
4. `adb install -r` 后设备上 APK 版本/哈希与仓库 APK 一致；
5. `adb forward` 后端口可达、断开后转发撤销。

设备接入后可执行：

```bash
cd linux
adb devices -l                                                   # 确认设备已授权
dotnet run --project src/AudioShare.Cli -c Release -- download-apk      # 取得官方 APK（仓库未内置）
dotnet run --project src/AudioShare.Cli -c Release -- adb-check --forward --ensure <serial>
dotnet run --project src/AudioShare.Cli -c Release -- selftest          # 含真实内录回归
# 或直接启动 GUI：audioshare  → 选声道 → 连接
```

同时可确认 `android/`、`windows/` 目录零改动：

```bash
git status --short          # 只应出现 linux/ 与（如存在）本地构建产物
```

---

## 附：本次交付的代码与产物清单

| 路径 | 内容 |
| --- | --- |
| `linux/PROTOCOL_AND_ARCH.md` | 协议与架构审计（逐项标注源码位置） |
| `linux/README.md` | 安装、使用、自检、已知限制 |
| `linux/VERIFICATION.md` | 本文件（验收证据） |
| `linux/HANDOVER.md` | **会话交接文档**：环境事实、复现命令、真机信息、陷阱清单、未完成项与优先级 |
| `linux/docs/evidence/` | **原始证据固化**：自检日志、真机 logcat、4 分钟/3 分钟长测日志、真实窗口日志 |
| `linux/build-package.sh` | 一键构建 publish/ + .deb + tar.gz |
| `linux/AudioShare.Linux.sln` | 解决方案（Core / Cli / Gui） |
| `linux/src/AudioShare.Core/` | 音频采集与静音保活、协议编解码、设备池、UDP 发现与网卡过滤、ADB 与 APK 安装、设置 |
| `linux/src/AudioShare.Cli/` | 自检工具（协议回放 / 声道分离 / 保活 / 发现 / 内录 / 联调接收端） |
| `linux/src/AudioShare.Gui/` | Avalonia GUI（MainView + MainWindow + 托盘 + 单实例 + 无头截图自检） |
| `linux/docs/gui-screenshot.png` | 界面截图（真实可视树渲染） |
| `linux/publish/AudioShare` | 自包含单文件可执行 |
| `linux/dist/*.deb` / `*.tar.gz` | 分发包（.deb 已用 `dpkg-deb -x` 解包并直接运行验证） |

---

## 附 2：1.7.10 ~ 1.7.19 变更与验证（2026-10-14 深夜 至 10-18）

变更内容见 `HANDOVER.md` §2.3：
- 1.7.10：USB/Wi-Fi 设备分类、发送防突发、采样率重握手、测试隔离；
- 1.7.11：三层标题栏修复、添加按钮合并、采样率下拉加宽；
- 1.7.12：连接状态并入顶行、内容区分组与间距统一；
- 1.7.13~1.7.17：定位并修复 `ScrollViewer.Padding` 测量陷阱导致的右侧裁切（控件 x 到 594 > 500）；
  音量下发改 100ms 节流（拖动跟手，松手后不再延迟变声）；
- 1.7.18：界面精简（单一刷新、删除管理页/退出按钮、连接断开合并、设备行两行）
  + 图标替换为仓库原图标（`windows/logo.ico`、`android/.../mipmap/ic_launcher.png`）；
- 1.7.19：删除"窗口透明"功能（顶部开关与窗口透明逻辑全部移除，窗口固定不透明）。

验证记录（2026-10-18，全部实测）：

```bash
$ dotnet build AudioShare.Linux.sln -c Release -warnaserror
    0 个警告 / 0 个错误
# 无头渲染：控件坐标全部在窗口内，按钮仅剩 刷新/连接/移除/添加
$ ./src/AudioShare.Gui/bin/Release/net8.0/AudioShare --screenshot /tmp/ui-simple.png
    ctrl: Button '刷新' @(424,65) size=56x33       # 424+56=480 <= 500
    screenshot written: /tmp/ui-simple.png (500x470)
# 真实窗口布局（1.7.18）
$ DISPLAY=:0 wmctrl -lG | grep 音频
    0x00a0000f  0 2136 1160 1000 940 ... 音频同享 1.7.19
    Button '✕'      @(461,5.5)  39x25            # 标题栏唯一按钮
    Button '刷新'    @(424,101)  56x33            # 右缘 480
    Button '断开'    @(415,227)  56x33            # 右缘 471（连接/断开合并）
    Button '添加'    @(434,275)  46x33            # 右缘 480
```

1.7.19 产物哈希（`sha256sum`，构建时间 `2026-10-18 01:13 +08:00`）：

```text
8e05b1d1bccd4952053c17accdc4b72c82670e79f678561172018f7ff96d5aa7  dist/audioshare_1.7.19_amd64.deb
（1.7.19 tar.gz 哈希见构建日志）
```

> 部署说明：本机 sudo 需交互密码，1.7.19 以 `dpkg-deb -x` 解包至 `~/apps/audioshare-1.7.19` 并以
> 用户级 `~/.local/share/applications/audioshare.desktop` 覆盖菜单入口运行（功能与 deb 安装等价）；
> 正式升级命令：`sudo apt install ./dist/audioshare_1.7.19_amd64.deb`。
