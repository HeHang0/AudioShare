# AudioShare for Linux（原生客户端，不依赖 Wine）

把电脑正在播放的声音实时分享到安卓设备。与仓库内 `android/` 客户端（`AudioShare.apk`，versionName 1.7.9）
**直接互通、无需修改 Android 端任何代码**，功能与 `windows/`（C#/WPF）客户端 1:1 对等。

- 技术栈：**C# / .NET 8 + Avalonia 11**（GUI），音频走 **PulseAudio / PipeWire 的 `.monitor` 源**（`parec --raw`），
  Android 控制走**系统原生 `adb`**。全过程不使用 Wine、不依赖任何 Windows 运行时。
- 协议、声道切分、广播发现、ADB 调用序列全部按 `windows/` 与 `android/` 源码逐项对齐，审计见
  [`PROTOCOL_AND_ARCH.md`](PROTOCOL_AND_ARCH.md)，验收证据见 [`VERIFICATION.md`](VERIFICATION.md)，
  **接手/二次开发的交接文档（环境事实、复现命令、陷阱清单、未完成项）见 [`HANDOVER.md`](HANDOVER.md)**。

---

## 1. 功能

| 功能 | 说明 |
| --- | --- |
| 系统音频内录 | 采集所选输出设备的 `.monitor` 源，默认 `@DEFAULT_MONITOR@`；可选 192k/176.4k/96k/48k/44.1k/16k/8k 采样率，帧长默认 100 ms |
| 静音保活 | 系统静默时 monitor 源可能停止产出数据；客户端**按实时速率补齐静音 PCM**（默认每 100 ms 一帧，与 Windows 端 NAudio 节奏一致），并内建 5 秒心跳帧，避免安卓端 AudioTrack 欠载后"一段时间后静音/断连"（对应上游 commit `dac02fd`） |
| 多设备并发 | 同时连接多台设备，每台独立声道模式：立体声 / 左声道 / 右声道 / 禁用；左右分离为**抽取**（不做混音），与 `windows/AudioManager.cs:116-132` 逐字节一致 |
| Wi-Fi 模式 | 局域网 UDP 自动发现（监听 58261–58270 全部端口，与安卓端每秒广播 10 个端口一致），也支持手动输入 `IP[:端口]`（默认 8088） |
| USB 模式 | 调用系统 `adb` 枚举设备，`adb forward tcp:<空闲端口> localabstract:picapico-audio-share` 后连接 |
| APK 自动安装/拉起 | 连接前比对 `dumpsys package ...versionName`；不匹配则 `adb push` + `pm uninstall` + `pm install -r` + `dumpsys deviceidle whitelist`，再 `am start -W -n com.picapico.audioshare/.MainActivity`（与 `windows/Speaker.cs:337-386` 同序列） |
| 音量 | 手动音量 + 跟随系统（读取默认输出音量并下发 `Volume(2)` 指令） |
| 远程管理页 | **双击设备行**，用 `xdg-open` 打开安卓端 `http://<ip>:<端口>`（默认 8080，斐讯 R1 为 8090） |
| 托盘与单实例 | 关闭窗口即最小化到托盘（音频转发不中断）；托盘菜单"显示窗口/退出"；再次启动会唤醒已有实例（Unix socket） |
| 网卡过滤 | 只对真实物理局域网网卡工作，自动排除 `lo`、`docker*`、`veth*`、`br-*`、`tun*`、`tailscale*`、`wg*` 等虚拟/隧道网卡 |

---

## 2. 安装

### 2.1 依赖

运行时依赖（Debian/Ubuntu 包名）：

```bash
sudo apt install adb pulseaudio-utils        # adb：USB 模式与自动装 APK；parec：内录
# 使用 PipeWire 的发行版：pipewire-bin 提供 parec，二者装其一即可
sudo apt install libx11-6 libice6 libsm6 libfontconfig1   # Avalonia 的 X11 依赖（Wayland 下同样需要）
```

- **.NET 运行时不需要单独安装**：发布的 `audioshare` 是 self-contained 单文件可执行。
- 若系统使用 PipeWire 且 `pactl info` 显示 `PulseAudio (on PipeWire ...)`，说明 pulse 兼容层已就绪。
- `adb` 需要能访问设备：USB 模式要求设备已授权调试；Wi-Fi 模式要求设备与电脑同一局域网（可选 `adb connect <ip>:5555`）。

### 2.2 三种安装方式

```bash
# A) Debian/Ubuntu 安装包
sudo apt install ./dist/audioshare_1.7.19_amd64.deb   # 已安装旧版本时同一命令即为升级
audioshare

# B) 免安装便携包
tar -xzf dist/audioshare-1.7.19-linux-x64.tar.gz -C ~/apps
~/apps/AudioShare

# C) 从源码运行（需要 .NET 8 SDK）
cd linux
dotnet run --project src/AudioShare.Gui -c Release
```

### 2.3 构建

```bash
cd linux
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH     # 或系统自带的 dotnet
dotnet build AudioShare.Linux.sln -c Release -warnaserror     # 0 error / 0 warning
./build-package.sh                                           # 产出 publish/ + dist/*.deb + dist/*.tar.gz
```

---

## 3. 使用

1. 启动 `audioshare`；窗口右上角是连接状态（绿点=已连接并显示设备数，红点=未连接）。
2. 选择"音频设备"= 要分享的输出设备（列表项即该设备的 `.monitor`，默认项表示 `@DEFAULT_MONITOR@`），必要时切换采样率。
   界面只有一个"刷新"按钮：一次同时刷新音频设备与播放设备列表。
3. 选择连接方式：
   - **USB**：打开 USB 开关 → 点"刷新"（与音频刷新共用一个按钮）→ 下拉列表出现设备 → 选择声道模式 → "连接"。首次连接会自动安装并拉起 APK（需 `AudioShare.apk`，见下节）。
   - **Wi-Fi**：关闭 USB 开关 → 局域网内已运行 AudioShare 的设备会被自动发现并出现在列表中（也可以手输 `IP` 或 `IP:端口` 后点"添加 IP"）→ 选择声道 → "连接"。
4. 想用两台设备分别放左右声道：给设备 A 选"左声道"、设备 B 选"右声道"，各自点"连接"。
5. **双击设备行**可用 `xdg-open` 打开安卓端的 Musiche 远程管理页。
6. 标题栏 ✕ ＝最小化到托盘，转发继续；**退出程序请用托盘菜单"退出"**（界面不再有单独的退出按钮）。

### APK 位置

自动安装/版本比对需要 `AudioShare.apk`（文件名固定，与程序同基名）。程序按以下顺序查找：

1. 环境变量 `AUDIOSHARE_APK` 指定的路径
2. 可执行文件所在目录（`/usr/bin` 或解包目录）
3. `linux/` 目录
4. 仓库根目录

没有 APK 时可用内置下载命令获取官方 release：

```bash
cd linux
dotnet run --project src/AudioShare.Cli -c Release -- download-apk --output ./AudioShare.apk
# 或直接 curl -fL -o AudioShare.apk \
#   https://github.com/HeHang0/AudioShare/releases/latest/download/AudioShare.apk
```

> 版本判定字符串为 `1.7.9`，即安卓 APK 的 `versionName`（与 `android/app/build.gradle` 一致），与桌面包版本号无关。设备上 APK 版本与该值不一致时会先卸载再安装。

---

## 4. 配置文件与日志

| 路径 | 内容 |
| --- | --- |
| `~/.config/AudioShare/audio.share.config.json` | 采集设备、设备列表与声道、采样率、音量、是否 USB、跟随系统 |
| `~/.config/autostart/audioshare.desktop` | 勾选"开机启动"后生成 |
| `~/.local/state/AudioShare/audioshare.log` | 运行日志（含每条 adb 命令行与退出码） |
| `$XDG_RUNTIME_DIR/audioshare.sock` | 单实例唤醒用的 Unix socket |

音量"跟随系统"按 3 秒周期读取默认输出的音量；不跟随系统时由客户端直接下发音量指令。

---

## 5. 自检与诊断（不需要 Android 设备）

`linux/src/AudioShare.Cli` 是不带 GUI 的自检/诊断工具，覆盖协议字节级一致性、声道分离、静音保活、
UDP 发现、ADB 序列与真实内录：

```bash
cd linux
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet run --project src/AudioShare.Cli -c Release -- selftest --keepalive-seconds 13   # 全部自检
dotnet run --project src/AudioShare.Cli -c Release -- monitors        # 枚举输出设备与 monitor 源
dotnet run --project src/AudioShare.Cli -c Release -- e2e             # 播放测试音并从 monitor 采集（验证真实内录）
dotnet run --project src/AudioShare.Cli -c Release -- capture --seconds 5 --wav /tmp/out.wav
dotnet run --project src/AudioShare.Cli -c Release -- discover --seconds 20   # 只监听局域网广播
dotnet run --project src/AudioShare.Cli -c Release -- net-report      # 网卡过滤判定（ACCEPT/REJECT 及原因）
dotnet run --project src/AudioShare.Cli -c Release -- adb-check --forward --ensure <serial>
dotnet run --project src/AudioShare.Cli -c Release -- serve --port 8088   # 模拟安卓端接收端，用于联调
dotnet run --project src/AudioShare.Cli -c Release -- stream --target 192.168.8.4:8088 \
    --channel stereo --seconds 60 --frame-ms 100 --ensure               # 真机端到端推流（打印实时统计）
```

已安装的 `.deb` 版本也带 GUI 自检开关（渲染真实界面到 PNG，不需要显示器）：

```bash
audioshare --screenshot /tmp/audioshare-gui.png
```

日志级别为 INFO；排查连接问题时先看 `~/.local/state/AudioShare/audioshare.log`，再对照 `PROTOCOL_AND_ARCH.md`。

---

## 6. 已知限制与注意事项

1. **Wi-Fi 自动发现的历史缺陷**：上游 Windows 客户端用 `报文长度 > 26 → 丢弃` 过滤广播，而自 `5c06bac`（2024-03-16）
   起安卓端报文固定为 `picapico-audio-share@<端口>@<管理页端口>`（@8088/@8080 = 30 字节），据此原版 Windows 客户端会
   **丢弃全部广播**。本客户端把阈值放宽到 64 字节以与真实客户端互通，其余过滤规则（前缀、`@` 分段、端口范围）保持一致。
2. **发现端口**：安卓端每 10 秒向 `255.255.255.255:58261–58270` 各发一份；本客户端把 10 个端口全部绑定，
   因此不依赖"发送端选了哪个端口"。
3. **USB 转发端口**：每台设备连接时动态取空闲端口（与 `windows/Utils.GetFreePort` 等价），断开会自动撤销 `adb forward`。
4. **采样率**：默认 48000 Hz（与上游 `Settings.SampleRate` 默认一致）。选择非 48000/44100 等
   非常用值时由 PipeWire/PulseAudio 服务端重采样；192k 以上会增加带宽与延迟，建议局域网使用 48k。
5. **帧长（重要，真机校准过）**：默认 **100 ms/帧**。安卓端每帧都要回主线程清 `isWriting`
   （`TcpService.java:462-477`），实测 10/20/50 ms 帧会大量触发 `write audio busy` 而丢帧断音
   （10 ms ≈105 次/秒、50 ms ≈25 次/秒），**100 ms 时 20 秒仅 2 次**、听感清晰流畅；
   该值与 Windows 端 NAudio loopback 的 ~100 ms 一致。可改 `~/.config/AudioShare/audio.share.config.json`
   里的 `FrameMs`（10/20/50/100）按设备调整：越小延迟越低，但老设备容易丢帧。
6. **延迟与网络**：帧长决定基础延迟（100 ms 帧串行 + 设备端缓冲，实测端到端约 200~300 ms）。若出现周期性卡顿，
   优先检查 Wi-Fi 带宽与 AP 的省电策略（组播/广播抑制会同时影响发现）。
7. **托盘图标**：取决于桌面环境是否支持 StatusNotifier/AppIndicator；不支持时窗口行为不受影响（关闭仍会隐藏）。
8. **X11/Wayland**：两种会话均可运行；Wayland 下运行在原生 Wayland 后端（必要时自动回退 XWayland）。
9. **斐讯 R1 氛围灯 / 多机互联**：由 Android 端实现，与客户端无关，功能不受影响。
10. **Windows 与 Android 代码零改动**：`linux/` 为新增目录，未修改 `windows/`、`android/` 与仓库根文件。
11. **真机端到端验证**已在斐讯 R1（`phicomm rk322x-box`，adb over Wi-Fi）上完成：连接/出声/切声道协议、4 分钟音乐播放 0 丢帧、3 分钟静音保活单一会话不断连，详见 `VERIFICATION.md` §9。
