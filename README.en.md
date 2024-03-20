# AudioShare - Real time sharing of Windows audio to Android devices

[![image](https://img.shields.io/github/v/release/hehang0/AudioShare.svg?label=latest)](https://github.com/HeHang0/AudioShare/releases)

English | [简体中文](./README.md)

AudioShare is an application that allows you to transfer real-time sound from your Windows computer to Android devices for playback. It supports multiple Android devices to connect simultaneously and can play different sounds according to different channels. You can connect your device through a USB data cable or Wi Fi network.

## Features

+ Real time transmission: Real time transmission of sound from Windows computers to Android devices with low latency and high sound quality.
+ Multi device support: Supports multiple Android devices to connect simultaneously, and can play different sounds according to different channels.
+ Two connection methods: supports USB data cable and Wi Fi network connection.
+ Remote control: supports remote control of playing cloud music, using the Musiche project, and supports NetEase Cloud, QQ, and Migu music playback.
+ Multi machine interconnection: supports synchronous playback of multiple devices during remote playback, which can be disabled or enabled in the settings interface.

## User Guide

### Windows

1. Download AudioShare.exe, AudioShare.apk, adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll(ignore the last three adb related files if only used for wifi connections or if adb is already installed) from the [Release Page](https://github.com/HeHang0/AudioShare/releases/latest).
2. Open AudioShare.exe application.
3. Choose connection method：
   + USB: Connect the Windows computer and Android device to the same USB cable.
   + Wi-Fi: Ensure that Windows computers and Android devices are connected to the same Wi Fi network.
4. USB Connection: Select the device from the "USB Device" dropdown menu, and then click the "Connect" button.
5. Wi-Fi Connection: Enter the IP address and port number of the Android device (default port number is 8088), and then click the "Connect" button (Windows will automatically detect the available Android devices on the local area network when using WiFi connection).
6. After a successful connection, you can hear the sound of your Windows computer on your Android device.
![image](./images/windows.png)
![image](./images/windows-wifi.png)

### Android Device

1. If possible, you can manually install the app to an Android device (Windows will try to install it when unable to connect).
2. If manually installed, you can open the app to view the remote management address and Windows remote connection address.
3. If you are using a USB connection, please wait for the application to automatically install.
4. If you are using a Wi Fi connection, make sure that your Windows computer and Android device are connected to the same Wi Fi network.
5. After a successful connection, you can hear the sound of your Windows computer on your Android device.
![image](./images/android.png)

## Advanced

### Phicomm R1 atmosphere light

1. Authorize the app to obtain Android root privileges.
2. It takes effect after restarting the app.
3. Multiple devices can synchronize atmosphere lighting effect.

### Remote control for playing cloud music

1. Access the remote management address displayed in the upper left corner of the Android app interface, with a default port of 8080 (Phicomm R1 is 8090).
2. Open the remote management page and select to log in to NetEase Cloud, QQ, or Migu Music accounts in the settings interface to view personal playlists.
3. Detailed introduction can be found in [Musiche project](https://github.com/HeHang0/Musiche).

### Multi interconnection

1. Install the AudioShare app on multiple Android devices.
2. After opening the AudioShare application on all devices, it will automatically discover local area network devices and connect them
3. By default, it will automatically connect and synchronize playback. If you need to disable the multi machine interconnection function, you can turn it off in the settings interface.
![image](./images/remote.png)

### Download

+ [Release](https://github.com/HeHang0/AudioShare/releases/latest)
