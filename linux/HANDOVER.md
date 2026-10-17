# 会话交接文档（AudioShare Linux 客户端）

> 面向接手的下一位工程师/AI。本文只写**可验证的事实**与**踩过的坑**，避免你重复我花掉的时间。
> 相关文档：`linux/PROTOCOL_AND_ARCH.md`（协议审计）、`linux/README.md`（用户向安装使用）、`linux/VERIFICATION.md`（验收证据）。

---

## 0. 一句话状态

`linux/` 目录下是一个**纯原生（无 Wine）的 AudioShare Linux 客户端**（C#/.NET 8 + Avalonia 11），
与仓库内 `android/` 客户端（`AudioShare.apk` 1.7.9）真机互通。当前版本 **1.7.19**：
构建 0 error/0 warning、自检 **20/20** + 回归 **9/9** 通过、`.deb`/便携包已产出、
并在**斐讯 R1（192.168.8.4）**上完成 Wi-Fi 与 adb-forward 两种通道的端到端实测。
1.7.9 → 1.7.19 的变更与修复记录见 §2.3；剩余未验证项见 §7。

---

## 1. 环境事实（接手必读，否则会白折腾）

| 项 | 值 |
| --- | --- |
| 工作目录 | `/home/lin/文档/project/AudioShare`（= `github.com/HeHang0/AudioShare`，HEAD `dac02fd` "Fix mute after a period of time"） |
| 操作系统 | Ubuntu 26.04 LTS，会话为 **Wayland**（`XDG_SESSION_TYPE=wayland`，`WAYLAND_DISPLAY=wayland-0`，同时有 `DISPLAY=:0`） |
| 音频栈 | PipeWire 1.6.2 + pipewire-pulse（`pactl info` 显示 `PulseAudio (on PipeWire 1.6.2)`）；`parec`/`pactl` 可用；默认采样规格 float32le 2ch 48000Hz |
| .NET SDK | **系统未安装 dotnet**，SDK 在用户态 `~/.dotnet`（8.0.426）。**每条命令前必须**：`export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH` |
| 权限 | 无 sudo/root；不能 `apt install`；打包用 `dpkg-deb --root-owner-group`（不需要 root） |
| adb | `/usr/bin/adb`（Debian 包名是 `adb`，不是 `android-tools-adb`）；`parec` 属 `pulseaudio-utils` |
| 桌面合成器 | 不支持 `wlr-screencopy`（`grim` 不可用）、X11 截图是黑的 → **GUI 取证请用 `--screenshot`（Avalonia 无头渲染），不要用 grim/xwd** |
| 真机 | **斐讯 R1**：`192.168.8.4`，adb over Wi-Fi `192.168.8.4:5555`，`getprop ro.product.manufacturer=phicomm`、`ro.product.model=rk322x-box`；已装 APK `versionName=1.7.9`；端口 8088（音频）/8090（斐讯管理页）/5555（adb）开放 |
| 本机 | `wlp6s0` = `192.168.8.8/21`（与设备同网段） |
| 显示与缩放 | 显示器 2560x1440@200Hz，GNOME 显示缩放 **1.25**（`monitors.xml` `<scale>1.25</scale>`）。Avalonia 11.3 **没有 Wayland 后端**，实际运行在 **X11/XWayland**；`Xft.dpi=192` 使 `RenderScaling=2.0`，X 屏幕为 **4096x2304**（XWayland 整数缩放坐标），窗口 500 DIP = 1000 X 像素。`Window.Position` 在 XWayland 下语义与物理像素不一致（会被 ×RenderScaling），**不要手动定位窗口**，交给合成器。可用环境变量 `AVALONIA_GLOBAL_SCALE_FACTOR` / `AVALONIA_SCREEN_SCALE_FACTORS` 覆盖缩放（未启用，Avalonia.X11 支持） |

### 1.1 用户偏好（重要，别踩）

- **不要擅自播放测试音**。用户明确说过"好吵别放了"；约定是：需要真实音频时**先叫用户放歌**。
- 需要"静音"场景时用**空闲的 monitor 源**（本机为 `alsa_output.pci-0000_01_00.1.hdmi-stereo.monitor`），全程无声。
- 用户设备（R1）就在他身边，听感验证要请他确认。

---

## 2. 交付物清单

### 2.1 源码布局（`linux/`）

| 路径 | 职责 | 关键点 |
| --- | --- | --- |
| `AudioShare.Linux.sln` | 解决方案（3 工程） | Core / Cli / Gui |
| `build-package.sh` | 一键：publish/ + `.deb` + `.tar.gz` | 需要 `DOTNET_ROOT`；产物在 `dist/`（已 gitignore） |
| `.gitignore` | 忽略 `bin/ obj/ publish/ dist/` | 构建产物不入库 |
| `src/AudioShare.Core/Protocol.cs` | 线路协议常量与编码 | `Head`(20B)、`Command`、`ChannelMask`、`EncodeFrame`、`DiscoveryMaxMessageLength=64` |
| `src/AudioShare.Core/AudioEngine.cs` | 采集 + 节拍投递 + 静音保活 | `DefaultFrameMilliseconds=100`；生产者线程 + `RingBuffer`；`ParecCaptureSource`；`ICaptureSource.Read(RingBuffer,int)` |
| `src/AudioShare.Core/Speaker.cs` | 单设备连接（音频流/控制/心跳/重连） | `SendControlAsync`（独立连接）；写循环内建静默 >5s 补心跳；`ConfigureUsbForward` 注入点 |
| `src/AudioShare.Core/DevicePool.cs` | 设备池/主模型 | `ConnectAsync` 内做 adb 安装拉起 + Wi-Fi 回退；`FrameMs` 设置；`StateChanged` 事件 |
| `src/AudioShare.Core/Networking.cs` | UDP 发现 + 网卡过滤 + 端口工具 | `UdpDiscovery` 绑 10 个端口；`NetworkInterfaceFilter`（虚拟网卡 denylist、子网打印） |
| `src/AudioShare.Core/AdbController.cs` | 系统 adb 封装 | 每条命令记录**完整命令行+退出码**；`CreateForwardAsync`/`RemoveForwardByRemoteAsync` |
| `src/AudioShare.Core/AndroidInstaller.cs` | APK 定位/安装/拉起、下载、路径 | `FindApk` 查找顺序；install 序列与 `windows/Speaker.cs:337-386` 一致；`AppPaths` |
| `src/AudioShare.Core/SettingsStore.cs` | 设置持久化 + autostart + 音调/分析工具 | XDG 路径；`FrameMs`；`Autostart`(XDG)；`ToneGenerator`(Goertzel/RMS) |
| `src/AudioShare.Core/Logger.cs` | 日志 / 进程执行 / 流读取扩展 | 日志落 `~/.local/state/AudioShare/audioshare.log` |
| `src/AudioShare.Cli/` | 自检与诊断 CLI | `Program.cs`（命令分发）、`SelfTests.cs`（11 项断言）、`TestTcpServer.cs`（**按安卓语义**的模拟接收端）、`ProtocolVerifier.cs`（**独立重写**的解析器）、`TestHarness.cs` |
| `src/AudioShare.Gui/` | Avalonia GUI | `MainWindow.axaml(.cs)` 窗口/唯一自绘标题栏/拖动/托盘/单实例/DPI 诊断；`MainView.axaml(.cs)` 精简布局（单一刷新、连接断开合并、双击设备行开管理页）；`MainWindowViewModel.cs`；`ViewModel.cs`（RelayCommand/SpeakerRow）；`SingleInstance.cs`；`ScreenshotHost.cs`（无头截图自检） |
| `docs/gui-screenshot.png` | 界面截图（真实可视树渲染） | 由 `--screenshot` 生成 |
| `docs/evidence/` | **原始证据固化**（见 §10） | 自检日志、真机 logcat、4 分钟/3 分钟长测日志 |

### 2.2 产物与校验

```text
linux/publish/AudioShare                        88 MB  自包含单文件（零 .NET 运行时依赖）
linux/dist/audioshare_1.7.19_amd64.deb           31 MB  sudo apt install ./dist/audioshare_1.7.19_amd64.deb
linux/dist/audioshare-1.7.19-linux-x64.tar.gz    38 MB  解压即用
linux/dist/cli/                                 框架依赖版 CLI（自检工具）
```

sha256（构建时间 `2026-10-18 01:13 +08:00`；`publish/` 与 `dist/` 已被 `.gitignore`，**不入库**；
换机器请重跑 `./build-package.sh` 并以现况 `sha256sum dist/*.deb dist/*.tar.gz publish/AudioShare` 为准）：

```text
8e05b1d1bccd4952053c17accdc4b72c82670e79f678561172018f7ff96d5aa7  dist/audioshare_1.7.19_amd64.deb
（1.7.19 tar.gz 哈希见构建日志 dist/audioshare-1.7.19-linux-x64.tar.gz）
（publish/AudioShare 哈希以 1.7.19 构建产物为准，见构建日志）
```

> dist/ 内同时保留了 1.7.9 / 1.7.10 的历史产物，安装时注意文件名，**别装错版本**。

### 2.3 版本变更史（1.7.9 → 1.7.10 → 1.7.11）

- **1.7.9**：首次交付（见 `VERIFICATION.md` 与 `docs/evidence/` 的原始证据）。
- **1.7.10**（2026-10-14 深夜会话）：修复用户实测反馈的四个问题——
  1. USB 列表混入 adb-over-Wi-Fi 设备（`ip:5555`）：现在无线调试端点登记到 Wi-Fi 列表（`ip:8088`）；
  2. 关 USB 后搜不到音箱：Wi-Fi 刷新时自动扫描 adb 无线设备入列（绕开"广播到不了电脑"的环境问题）；
  3. 播放断续卡顿（PC 端部分）：发送队列 75帧→3帧、相邻帧按帧长间隔、预缓冲 ~200ms、超时回收（代价 +100ms 延迟）；
  4. 采样率切换后不重握手（音调错误）：现在自动重新握手。
  另：自检/测试改写临时目录（不再覆盖真实配置）、`Protocol.AndroidVersionName` 与桌面版本解耦、
  新增 `RegressionTests.cs`（`regress` 命令，9 项）、自检扩到 20 项。
  同会话查明：**卡顿的根因在 Android 端**（`TcpService.java` 每帧回主线程清 `isWriting`，busy 即丢帧），
  用户决定**不改 `android/`**（与项目硬边界一致），PC 端已做缓解。
- **1.7.11**（2026-10-15）：修复 GUI 三层标题栏（系统装饰 + MainWindow/MainView 各一条自绘 → 只保留一条，
  `SystemDecorations="None"` + 标题栏拖动）、合并重复的"添加 IP/添加"按钮为一个（有输入添加 IP，留空自动网段）、
  采样率下拉加宽（112px，防 `176.4kHz` 截断）、版本号相关处统一 1.7.11。
- **1.7.12**（2026-10-15）：按用户反馈继续消除界面歧义——设备行的第二个"刷新"改名 **"刷新设备"**
  （ToolTip"重新扫描 USB / Wi-Fi 设备列表"），音频行的刷新加 ToolTip"刷新音频采集设备列表"；
  "● 已连接 [n]"状态从独立行并入顶部开关行右侧（省一行、与 Windows 版状态位置一致）；
  内容区按"顶部开关 / 采集设备 / 播放设备（标题+列表+添加）/ 音量"分组并统一间距，顶部分组加分隔线。
- **1.7.13 ~ 1.7.17**（2026-10-18，真机 GUI 回归修复）：
  - 1.7.13：加入 `scaling diag`（缩放/尺寸）与 `layout dump`（控件坐标）诊断日志，定位到
    "UI 变小 + 按钮显示不全"的根因——XWayland 整数缩放（X 屏幕 4096x2304、RenderScaling=2）下
    `ScrollViewer.Padding` 在垂直滚动条出现时不参与测量，内容按窗口全宽排布（控件延伸到 x=594），
    右侧被窗口边缘裁掉；改为**把内边距放到内容 `Margin`** 后修复（所有控件右缘 ≤480）。
  - 1.7.14/1.7.15：音量下发改为 **100ms 节流合并**（拖动中最多 10 次/秒、松手 ≤100ms 生效），
    配置保存节流 1 秒；`SyncFromPool` 不再把手动模式的滑块值拉回旧值（消除"松手后突然变声"）。
  - 1.7.16/1.7.17：移除不可靠的手动窗口定位（XWayland 下 `Window.Position` 语义与物理坐标不一致），
    窗口位置交由合成器管理。
- **1.7.18**（2026-10-18，界面精简 + 原图标）：
  1. 两个"刷新"**合并为一个**（同时刷新音频设备与播放设备列表）；
  2. 删除每设备"打开管理页"按钮（改为**双击设备行**打开远程页，隐藏入口）；
  3. 删除底部"退出"大按钮（退出走托盘菜单）；
  4. 每设备"连接/断开"**合并为一个按状态切换的按钮**；
  5. 设备行从 3 行压缩为 2 行（状态与统计合并显示）；
  6. 标题栏只保留 ✕（删除行为重复的 —）；
  7. 图标替换为仓库原图标：`windows/logo.ico` + `android/.../mipmap/ic_launcher.png`（与 Windows/Android 一致）。
- **1.7.19**（2026-10-18）：按用户要求**删除"窗口透明"功能**——顶部开关、`MainWindowViewModel.Acrylic`、
  `MainWindow` 的 `ApplyTransparency`（AcrylicBlur/Blur/Transparent 回退）全部移除，窗口固定为主题不透明背景；
  `SettingsStore.Acrylic` 字段保留仅为兼容旧配置（无 UI 引用）。

---

## 3. 复现流程（命令级）

```bash
cd /home/lin/文档/project/AudioShare/linux
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH

# 构建（严格模式：任何警告即失败）
dotnet build AudioShare.Linux.sln -c Release -warnaserror          # 期望：0 个警告 / 0 个错误

# 自检（11 项：协议回放/声道分离/保活/发现/网卡过滤/ADB/真实内录）
dotnet run --project src/AudioShare.Cli -c Release -- selftest --keepalive-seconds 40

# 打包
./build-package.sh && rm -rf dist/deb-stage

# GUI（真实窗口）
./publish/AudioShare                       # 或 dotnet run --project src/AudioShare.Gui -c Release
# GUI 无人值守取证（不需要显示器）
./publish/AudioShare --screenshot /tmp/gui.png
```

### 3.1 真机端到端（斐讯 R1）

```bash
adb connect 192.168.8.4:5555 && adb devices -l

# A) Wi-Fi 直连推流（默认 100ms 帧；--ensure 会做版本比对 + am start）
dotnet run --project src/AudioShare.Cli -c Release -- \
  stream --target 192.168.8.4:8088 --channel stereo --seconds 60 --frame-ms 100 --ensure

# B) USB/adb forward 通道（与 USB 线完全相同的机制）
dotnet run --project src/AudioShare.Cli -c Release -- \
  stream --usb 192.168.8.4:5555 --channel stereo --seconds 30 --frame-ms 100 --ensure

# C) 静音保活长测（用空闲 HDMI monitor，全程无声）
dotnet run --project src/AudioShare.Cli -c Release -- \
  stream --target 192.168.8.4:8088 --seconds 185 --frame-ms 100 \
  --source alsa_output.pci-0000_01_00.1.hdmi-stereo.monitor

# 设备侧证据（必须按时间戳过滤，见 §6 陷阱 4）
adb -s 192.168.8.4:5555 logcat -d -T "$(date '+%m-%d %H:%M:%S.000')" -s AudioShareService:*
```

其他诊断命令：`monitors`（枚举 monitor 源）、`net-report`（网卡过滤判定）、`discover --seconds 30`（只监听广播）、
`adb-check --forward --ensure <serial>`、`capture --seconds 5 --wav /tmp/x.wav`、`serve --port 8088`（模拟安卓接收端）、
`download-apk`（取官方 APK，仓库不内置）。

---

## 4. 验收状态（对照目标契约 9 条）

| # | 标准 | 状态 | 证据 |
| --- | --- | --- | --- |
| 1 | Release 0 error/0 warning | ✅ | §3 命令；`VERIFICATION.md` §1 |
| 2 | 协议字节级一致性（独立解析器） | ✅ | `docs/evidence/selftest-100ms.log`；`VERIFICATION.md` §2 |
| 3 | 发现 + 网卡过滤 | ✅ | 同上（含伪造广播与反例、`net-report`） |
| 4 | 采集 + 静音保活 | ✅ | 自检 40s 静默 + 真机 3 分钟长测：`docs/evidence/device-logcat-3min-silence.log` |
| 5 | 多设备并发 + 声道分离 | ✅ | 自检：Left `1k=16383/2k=0`、Right `1k=0/2k=16383`、Stereo 全通 |
| 6 | ADB 流程 | ✅（真机） | `--usb` 实测：forward 建立/撤销 + 版本比对 + `am start`；见 `VERIFICATION.md` §6 |
| 7 | GUI 对等 + 可启动 | ✅ | 真实窗口 + `--screenshot` + 17 项控件对照表（§7.3） |
| 8 | 打包与文档 | ✅ | `.deb`（解包后直接运行通过）+ tar.gz + 三份文档 |
| 9 | 真机端到端 | ✅ | 4 分钟真实播放（用户听感"清晰流畅"）+ 3 分钟静音保活；`docs/evidence/e2e-4min-music-stream.log` |

---

## 5. 关键实现决策（为什么这么做）

1. **音频采集走 `parec --raw`**（而非 libpulse P/Invoke）：无额外原生依赖，采样率转换交给服务端。
   参数见 `ParecCaptureSource.BuildArguments`：`--device=@DEFAULT_MONITOR@ --format=s16le --rate=<rate> --channels=2 --raw --latency-msec=20`。
2. **采集与投递解耦**：生产者线程只往 `RingBuffer` 灌 PCM；投递循环用**实时时钟**每 `FrameMs` 投一帧，
   环形缓冲有数据就投真实数据、没有就投静音帧 → 这是"monitor 静默不产出数据"时仍不断流的关键（上游 commit `dac02fd` 的 Linux 等价实现）。
3. **帧长默认 100 ms**（真机校准，见 §6 陷阱 3），可由 `SettingsStore.FrameMs` / `--frame-ms` 调整。
4. **控制指令用独立短连接**：与 `windows/Speaker.cs:505-538` 一致（head+cmd[+4B] → 读 1 字节 → 关闭）。
5. **发现阈值放宽到 64 字节**：上游 `windows/Model.cs:83` 用 `>26` 过滤，但安卓端自 `5c06bac` 起报文恒为 30 字节
   → 原版会丢弃全部广播。这是**有意的偏离**，已在 README/VERIFICATION 标注并附证据。
6. **静音保活双保险**：① 实时节拍补静音帧；② 写循环空转 >5 s 补长度 0 心跳帧（`Speaker.SendHeartbeatIfIdleAsync`）。
7. **GUI 与 CLI 共用同一套 Core 代码**：`DevicePool.ConnectAsync` 是唯一连接入口（含 adb 安装拉起与 Wi-Fi→adb 回退），
   GUI/CLI 都走它，避免"两套实现"。
8. **无头截图自检**：`--screenshot` 用 Avalonia 官方 `CaptureRenderedFrame()`，可在无显示器的 CI 上验证界面。

---

## 6. 陷阱清单（最省时间的部分，务必先读）

1. **头部字面量是 20 字节**：`"picapico-audio-share"` = 20，不是 18；偏移：head(0..19)、命令(20)、采样率(21..24)、掩码(25..28)、帧从 29 起。
2. **安卓端每帧要回主线程清 `isWriting`**（`TcpService.java:462-477`），帧太密会丢帧断音 —— 见下条。
3. **真机帧长校准（重要）**：同一台 R1 上实测 `write audio busy` 率：
   | 帧长 | busy 率 | 结论 |
   | --- | --- | --- |
   | 10 ms | ≈105 次/秒 | 几乎全丢 |
   | 20 ms | 40 次/秒 | 严重丢帧 |
   | 50 ms | 25 次/秒 | 仍丢帧 |
   | **100 ms** | **0.1 次/秒** | 干净（与 Windows 端 NAudio 节奏一致） |
   → 默认已设 100 ms。**不要为了"低延迟"把帧长调小而不做真机回归。**
4. **`adb logcat -c` 不可靠**：清完后 `logcat -d` 仍可能吐出 3000+ 行历史。测设备端指标必须用
   `logcat -d -T "<起点时间戳>" -s AudioShareService:*`。我曾因此得出"225 次/秒"的错误结论。
5. **单实例 socket 不能先 unlink**：删除路径后新进程仍能 bind 同一路径 → 单实例失效。正确做法是先 connect 探测
   （`SingleInstance.IsAlive`），连不上才清理陈旧 socket。
6. **`TestTcpServer` 只保存"帧"，不保存握手字节**：握手要靠客户端侧旁录（`Speaker.OnWireWrite`）校验；
   旁录必须在**建连之前**挂载，否则握手会漏掉。
7. **Avalonia 11 细节**：`ComboBox` 没有 `SelectedValuePath`（用 `SelectedItem` 绑对象）；
   `App.axaml` 里的 `TrayIcon` 加 `x:Name` 会触发 XAML 编译器内部错误（改用代码创建托盘）；
   无头渲染必须 `UseSkia().UseHeadless(new(){UseHeadlessDrawing=false})`，且**必须先 `Show()` 再 `CaptureRenderedFrame()`**，
   自己用 `RenderTargetBitmap.Render` 会得到空白图。
8. **C# 陷阱**：`DevicePool` 基类构造函数会调用虚方法 `CreateEngine()`，而派生类的**属性初始化器晚于基类构造**执行
   → 用属性初始化器注入假引擎会拿到 null（这是我在测试里踩过的坑，已改为延迟创建）。
9. **`-warnaserror` 严格**：`TreatWarningsAsErrors=true`，未使用的字段/参数、可空性都会直接编译失败。
10. **打包 Dependencies 名称**：Debian/Ubuntu 上 adb 包名是 `adb`（不是 `android-tools-adb`），
    `parec` 来自 `pulseaudio-utils`；Avalonia 需要 `libx11-6 libice6 libsm6 libfontconfig1`。
11. **证据别只留在临时目录**：`$PI_SCRATCH_DIR` 随会话删除，`/tmp` 会清 → 关键日志已复制到 `linux/docs/evidence/`。
12. **无 root 安装新版**：本机 sudo 需要交互密码。等效方案（1.7.11 即以此部署）：
    ```bash
    dpkg-deb -x dist/audioshare_1.7.11_amd64.deb ~/apps/audioshare-1.7.11
    # 用户级 desktop 覆盖系统菜单入口（~/.local/share/applications 优先于 /usr/share/applications）
    # 正式升级仍建议：sudo apt install ./dist/audioshare_1.7.11_amd64.deb
    ```
13. **测试音约定**：用户明确要求不要擅自播放测试音；selftest 用 `--skip-real-audio`，真实音频场景先请用户放歌。
14. **`ScrollViewer.Padding` 的测量陷阱（Avalonia 11）**：内容出现垂直滚动条时 Padding 可能不参与测量，
    子元素按窗口全宽布局 → 右侧控件溢出被裁（实测控件 x 到 594 > 500）。**内边距必须放在内容容器的
    `Margin` 上**（`<ScrollViewer><StackPanel Margin="20,8,20,0">`），不要用 `ScrollViewer.Padding`。
    验证方法：`layout dump` 日志中控件 `x+width` 必须 ≤ 窗口宽度。

---

## 7. 未完成事项与建议优先级

### P0（需要用户配合，非代码问题）

1. **物理 USB 线**：本机当前只通过 adb-over-Wi-Fi 验证了 `adb forward` 机制（与 USB 线完全相同）；
   接 USB 线后跑 `stream --usb <serial> --seconds 30 --frame-ms 100` 即可闭环。
2. **APK 自动安装分支**：设备上已是 1.7.9 → 走"版本匹配"分支；`push` + `pm uninstall` + `pm install -r`
   分支未触发。复现方法：拿一个旧版本 APK，`AUDIOSHARE_APK=/path/old.apk` 后再连接。
3. **左右声道听感验证**：协议层已验证（mask 4/12、抽取算法逐字节比对、模拟设备频谱隔离），
   真机听感需用户配合（`--channel left` / `right`，先请用户放歌）。

### P1（可选增强）

4. **GUI 暴露 `FrameMs`**：目前只能改配置文件或 CLI 参数；可在设置区加一个下拉（10/20/50/100）。
5. **"窗口透明"开关：1.7.19 已按用户要求删除**（顶部开关、`MainWindowViewModel.Acrylic`、
   `MainWindow.ApplyTransparency` 全部移除；`SettingsStore.Acrylic` 仅保留用于兼容旧配置文件）。
6. **AppImage**：环境无 `appimagetool`（也不便联网安装），目前只提供 `.deb` + tar.gz。
7. **CI**：可加 GitHub Actions：`dotnet build -warnaserror` + `selftest` + `--screenshot` + `dpkg-deb --build`。
8. **英文界面**：目前界面为中文（与 `windows/Languages/zh-cn.xaml` 文案一致）；需英文则按 `LANG` 切。
9. **网络环境排查（用户配合）**：UDP 发现广播到不了本机（设备在发、电脑收不到；怀疑 AP 广播抑制或
   Clash TUN `Meta` 接口拦截）。已用"Wi-Fi 刷新自动登记 adb 无线设备"绕过；根治需查路由器/防火墙。
10. **卡顿根治（已决策：不做）**：根因在 Android 端 `write audio busy` 丢帧；用户决定不改 `android/`，
    PC 端防突发缓解已随 1.7.10 落地（见 §2.3）。

### P2（长期）

11. 用真正的测试框架（xunit）替换自制 `TestHarness`；把 `selftest` 拆成可并行用例。
12. 采样率切换时对非 48k 做重采样质量验证（当前交给 PipeWire 服务端重采样）。
13. 断线重连的退避策略（当前是"第一次失败重连一次"）与长时间网络抖动下的表现。

---

## 8. 风险与不确定性

| 项 | 说明 |
| --- | --- |
| 发现阈值偏离上游 | 有意的偏离（64 vs 26），依据是安卓端报文 30 字节。若上游未来改回小报文，本实现仍兼容（阈值更宽松）。 |
| 帧长 100 ms | 基于**斐讯 R1** 实测；其他机型（性能更好的手机）可能能吃下更小帧长。已做成可配置。 |
| 窗口透明 | **1.7.19 已按用户要求删除**（窗口固定为主题不透明背景），不再存在合成器兼容问题。 |
| 托盘图标 | 依赖桌面环境的 StatusNotifier/AppIndicator 支持；本机日志确认 `tray icon created`，但**托盘点击唤出未实操**。 |
| USB 模式 | 机制已验证（adb forward 建立/撤销），物理线未验证。 |
| 环境 | 无 root：不能装系统包、不能改系统音频配置；`~/.dotnet` 为用户态 SDK，换机器需重装或改用系统 dotnet。 |

---

## 9. Git 状态与提交建议

```bash
cd /home/lin/文档/project/AudioShare
git status --short        # 未提交时：?? linux/ 与 ?? .pi/（后者是宿主生成的目标工件目录）
```

- **`windows/`、`android/` 与仓库根文件均未改动**（这是本次任务的硬边界，已多次核验）。
- 只提交 `linux/`；`linux/.gitignore` 已排除 `bin/ obj/ publish/ dist/`：
  ```bash
  git add linux && git commit -m "Add native Linux client (Avalonia + PulseAudio/PipeWire, no Wine)"
  ```
- 远端 `origin = https://github.com/HeHang0/AudioShare.git` 是上游，账号 `wwwljxw` 对 it 只有 READ 权限。
  **代码已 fork 并推送到 `https://github.com/wwwljxw/AudioShare`（commit `64c5de6`，分支 master）**。
- 本机 git 未配置 `user.name/user.email`，提交时用命令行临时指定（不改全局配置）：
  ```bash
  git -c user.name="wwwljxw" -c user.email="57058161+wwwljxw@users.noreply.github.com" commit -m "..."
  ```
- 推送（`gh` 已登录 `wwwljxw`，token 含 repo 权限；**不要直接从 origin push**）：
  ```bash
  export PATH="$HOME/.local/bin:$PATH"
  git -c credential.helper='!gh auth git-credential' push \
      https://github.com/wwwljxw/AudioShare.git master
  ```
- 若要让分发包可下载：把 `dist/*.deb`、`dist/*.tar.gz` 作为 Release 资产上传（不入库）。

---

## 10. 临时文件与证据固化

- 会话临时目录 `$PI_SCRATCH_DIR`（`/home/lin/.pi-desktop/scratch/9fd7dd46-.../`）**随会话删除**，
  其中只有 `e2e-listen.log`、`selftest-*.log` 等有价值 —— 已复制到 `linux/docs/evidence/`。
- 已固化的证据：
  | 文件 | 内容 |
  | --- | --- |
  | `docs/evidence/selftest-100ms.log` | 当前帧长（100 ms）下 11/11 自检的完整输出 |
  | `docs/evidence/selftest-10ms-historical.log` | 帧长仍为 10 ms 时的自检输出（历史对照） |
  | `docs/evidence/e2e-4min-music-stream.log` | 真机 4 分钟真实播放推流日志（0 丢帧） |
  | `docs/evidence/device-logcat-4min-music.log` | 对应设备侧 logcat（ready/ended/busy/heartbeat 统计） |
  | `docs/evidence/device-logcat-3min-silence.log` | 3 分钟静音保活长测的设备侧日志 |
  | `docs/evidence/device-logcat-usb-forward.log` | `--usb`（adb forward）真机验证的设备侧日志 |
  | `docs/evidence/gui-real-window.log` | 真实窗口启动日志（托盘/发现/枚举/adb/音量跟随） |
- 运行期日志（不属交付物，会在用户机器上累积）：`~/.local/state/AudioShare/audioshare.log`。
- 用户配置：`~/.config/AudioShare/audio.share.config.json`（当前含 `FrameMs`、`Acrylic`、设备列表；
  注意里面可能有用户手点"添加"产生的 `192.168.8.x:8088` 条目，属正常）。

---

## 11. 一分钟自检（接手后立刻确认环境没坏）

```bash
cd /home/lin/文档/project/AudioShare/linux
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet build AudioShare.Linux.sln -c Release -warnaserror | tail -3   # 0/0
dotnet run --project src/AudioShare.Cli -c Release -- selftest --keepalive-seconds 20 | tail -14
adb connect 192.168.8.4:5555 && adb devices -l                       # 真机在线（如设备开机）
./publish/AudioShare --screenshot /tmp/gui.png                        # 界面渲染自检
```
