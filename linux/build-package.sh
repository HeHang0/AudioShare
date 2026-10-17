#!/usr/bin/env bash
# 构建 AudioShare Linux 客户端的分发包：
#   1) publish/            —— 自包含单文件可执行（零 .NET 运行时依赖）
#   2) dist/*.deb          —— Debian/Ubuntu 安装包（dpkg-deb 构建，无需 root）
#   3) dist/*.tar.gz       —— 免安装便携包
#
# 用法： ./build-package.sh [版本号]
set -euo pipefail

VERSION="${1:-1.7.19}"
ARCH="amd64"
LINUX_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$LINUX_DIR/.." && pwd)"
DIST="$LINUX_DIR/dist"
PUBLISH="$LINUX_DIR/publish"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

echo "==> 构建自包含可执行（linux-x64, single-file）"
rm -rf "$PUBLISH"
dotnet publish "$LINUX_DIR/src/AudioShare.Gui/AudioShare.Gui.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$PUBLISH"

echo "==> 构建 CLI 自检工具（框架依赖，供 README 中的验证流程使用）"
dotnet publish "$LINUX_DIR/src/AudioShare.Cli/AudioShare.Cli.csproj" \
  -c Release -o "$DIST/cli"
cp "$REPO_DIR/linux/PROTOCOL_AND_ARCH.md" "$DIST/cli/" 2>/dev/null || true

STAGE="$DIST/deb-stage"
rm -rf "$STAGE"
mkdir -p "$STAGE/DEBIAN" "$STAGE/usr/bin" "$STAGE/usr/share/applications" \
         "$STAGE/usr/share/icons/hicolor/256x256/apps" \
         "$STAGE/usr/share/doc/audioshare"

install -m 0755 "$PUBLISH/AudioShare" "$STAGE/usr/bin/audioshare"
install -m 0644 "$LINUX_DIR/src/AudioShare.Gui/Assets/audioshare.png" \
  "$STAGE/usr/share/icons/hicolor/256x256/apps/audioshare.png"   # 256x256 PNG
install -m 0644 "$LINUX_DIR/README.md" "$STAGE/usr/share/doc/audioshare/README.md"
install -m 0644 "$LINUX_DIR/PROTOCOL_AND_ARCH.md" "$STAGE/usr/share/doc/audioshare/PROTOCOL_AND_ARCH.md"
install -m 0644 "$LINUX_DIR/VERIFICATION.md" "$STAGE/usr/share/doc/audioshare/VERIFICATION.md" 2>/dev/null || true

cat > "$STAGE/usr/share/applications/audioshare.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=AudioShare
Name[zh_CN]=音频同享
Comment=Share this computer's audio to Android devices over Wi-Fi or USB
Comment[zh_CN]=通过 Wi-Fi 或 USB 把电脑声音实时分享到安卓设备
Exec=audioshare
Icon=audioshare
Terminal=false
Categories=AudioVideo;Network;
Keywords=audio;share;android;airplay;audioshare;
StartupNotify=false
DESKTOP

SIZE_KB=$(du -sk "$STAGE/usr" | cut -f1)
cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: audioshare
Version: $VERSION
Section: sound
Priority: optional
Architecture: $ARCH
Maintainer: AudioShare Linux Port <noreply@example.com>
Installed-Size: $SIZE_KB
Depends: adb | android-tools-adb, pulseaudio-utils | pipewire-bin, libx11-6, libice6, libsm6, libfontconfig1
Recommends: android-sdk-platform-tools-common
Description: AudioShare Linux client (native, no Wine)
 Streams the system audio (PulseAudio/PipeWire monitor source) to Android devices
 over Wi-Fi or USB, 1:1 compatible with the official AudioShare Android app.
 .
 Features: multi-device with per-device channel selection (stereo/left/right),
 UDP auto-discovery (ports 58261-58270), adb forward over USB, automatic APK
 install/launch via adb, remote music page shortcut (8080/8090), tray icon.
 .
 The GUI is a native Avalonia application; audio capture uses parec from
 pulseaudio-utils (or pipewire-bin) and the Android side is driven through the
 system adb binary. Wine is not used at any point.
CONTROL

cat > "$STAGE/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database -q /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
echo "AudioShare 已安装。运行: audioshare   自检: audioshare --screenshot /tmp/gui.png"
POSTINST
chmod 0755 "$STAGE/DEBIAN/postinst"

echo "==> 生成 .deb"
DEB="$DIST/audioshare_${VERSION}_${ARCH}.deb"
dpkg-deb --root-owner-group --build "$STAGE" "$DEB" >/dev/null
echo "    $DEB ($(du -h "$DEB" | cut -f1))"

echo "==> 生成便携 tar.gz"
TARBALL="$DIST/audioshare-${VERSION}-linux-x64.tar.gz"
tar -C "$PUBLISH" -czf "$TARBALL" AudioShare
echo "    $TARBALL ($(du -h "$TARBALL" | cut -f1))"

echo "==> 校验 .deb 内容"
dpkg-deb --info "$DEB" | head -12
dpkg-deb --contents "$DEB"

echo "==> 完成。产物位于 $DIST"
