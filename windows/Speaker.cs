using Microsoft.Toolkit.Uwp.Notifications;
using NAudio.Wave;
using SharpAdbClient;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NamePair = System.Collections.Generic.KeyValuePair<AudioShare.AudioChannel, string>;

namespace AudioShare
{
    public class Speaker : INotifyPropertyChanged, IDisposable
    {
        enum Command
        {
            None = 0,
            AudioData = 1,
            Volume = 2,
            SyncTime = 3,
            Stop = 4
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<Speaker> Remove;
        public event EventHandler<ConnectStatus> ConnectStatusChanged;
        private static readonly ObservableCollection<NamePair> _channels = new ObservableCollection<NamePair>();
        private static readonly byte[] TCP_HEAD = Encoding.Default.GetBytes("picapico-audio-share");
        private static readonly string REMOTE_SOCKET = "localabstract:picapico-audio-share";

        static Speaker()
        {
            _channels.Add(new NamePair(AudioChannel.Stereo, "立体声"));
            _channels.Add(new NamePair(AudioChannel.Left, "左声道"));
            _channels.Add(new NamePair(AudioChannel.Right, "右声道"));
            _channels.Add(new NamePair(AudioChannel.None, "禁用"));
        }
        private TcpClient tcpClient = null;
        readonly AdbClient adbClient = new AdbClient();
        private string _remoteIP = string.Empty;
        private int _remotePort = -1;
        private readonly bool _isUSB = false;
        private readonly string _name = string.Empty;
        private string _id = string.Empty;
        private bool _disposed = false;
        private bool _retried = false;
        private readonly Dispatcher _dispatcher;

        public string Id => _id;

        public string Display
        {
            get => _isUSB ? $"{_name} [{_id}]" : _id;
            set { _id = value; }
        }
        public bool IdReadOnly => _isUSB || _connectStatus != ConnectStatus.UnConnected;
        public bool RemoveVisible => !_isUSB;
        public bool ChannelEnabled => _connectStatus == ConnectStatus.UnConnected;
        public bool ConnectEnabled => _channel != AudioChannel.None;
        private ConnectStatus _connectStatus = ConnectStatus.UnConnected;
        public bool Connected => _connectStatus == ConnectStatus.Connected;
        public bool Connecting => _connectStatus == ConnectStatus.Connecting;
        public bool UnConnected => _connectStatus != ConnectStatus.Connected;
        public ObservableCollection<NamePair> Channels => _channels;
        private AudioChannel _channel = AudioChannel.None;
        public NamePair ChannelSelected
        {
            get => _channels.FirstOrDefault(m => m.Key == _channel);
            set
            {
                _channel = value.Key;
                OnPropertyChanged(nameof(ConnectEnabled));
            }
        }

        public Speaker(Dispatcher dispatcher, string id, string name, AudioChannel channel, bool isUSB)
        {
            _id = id;
            _name = name;
            _channel = channel;
            _isUSB = isUSB;
            _dispatcher = dispatcher;
        }

        public Speaker(Dispatcher dispatcher, string id) : this(dispatcher, id, AudioChannel.None)
        {
        }

        public Speaker(Dispatcher dispatcher, string id, AudioChannel channel) : this(dispatcher, id, string.Empty, channel, false)
        {
        }

        public Speaker(Dispatcher dispatcher, string id, string name) : this(dispatcher, id, name, AudioChannel.None)
        {
        }

        public Speaker(Dispatcher dispatcher, string id, string name, AudioChannel channel) : this(dispatcher, id, name, channel, true)
        {
        }

        private void SetConnectStatus(ConnectStatus connectStatus, bool toast = false)
        {
            if (_connectStatus == connectStatus) return;
            _dispatcher.InvokeAsync(() =>
            {
                if (toast && connectStatus == ConnectStatus.UnConnected && _connectStatus != connectStatus)
                {
                    var builder = new ToastContentBuilder()
                        .AddText($"{Display} {Languages.Language.GetLanguageText("disconnected")}");
                    if (!_disposed)
                    {
                        builder.AddButton(Languages.Language.GetLanguageText("reconnecte"), ToastActivationType.Foreground, _id);
                        builder.SetToastDuration(ToastDuration.Long);
                    }
                    builder.Show();
                }
                Logger.Info("set connect status start: " + connectStatus);
                _connectStatus = connectStatus;
                OnPropertyChanged(nameof(Connected));
                OnPropertyChanged(nameof(Connecting));
                OnPropertyChanged(nameof(UnConnected));
                OnPropertyChanged(nameof(IdReadOnly));
                OnPropertyChanged(nameof(RemoveVisible));
                OnPropertyChanged(nameof(ChannelEnabled));
                Logger.Info("set connect status end");
                ConnectStatusChanged?.Invoke(this, connectStatus);
            });
        }

        public RelayCommand ConnectCommand => new RelayCommand(Connect, CanConnect);

        public RelayCommand DisConnectCommand => new RelayCommand(DisConnect, CanDisConnect);

        public RelayCommand RemoveCommand => new RelayCommand(RemoveSpeaker, CanRemoveSpeaker);

        public void SetVolume(int volume)
        {
            byte[] volumeBytes = BitConverter.GetBytes(volume);
            _ = RequestTcp(Command.Volume, volumeBytes);
        }

        public void SyncTime()
        {
            _ = RequestTcp(Command.SyncTime);
        }

        public void Dispose()
        {
            _disposed = true;
            _ = DisConnect();
        }

        private void Connect(object sender)
        {
            _ = Connect();
        }

        public async Task Connect(bool retry=false)
        {
            if (Connecting) return;
            _disposed = false;
            await DisConnect(false, retry);
            SetConnectStatus(ConnectStatus.Connecting);
            Logger.Info("connect start");
            try
            {
                tcpClient = new TcpClient();
                tcpClient.NoDelay = true;
                if (!retry)
                {
                    if (_isUSB)
                    {
                        if (!await EnsureDevice(_id))
                        {
                            throw new Exception("device not ready");
                        }
                        int port = await Utils.GetFreePort();
                        var device = adbClient.GetDevice(_id);
                        adbClient.RemoveRemoteForward(device, REMOTE_SOCKET);
                        adbClient.CreateForward(device, "tcp:" + port, REMOTE_SOCKET, true);
                        _remoteIP = "127.0.0.1";
                        _remotePort = port;
                    }
                    else
                    {
                        string pattern = @"^[\[]?(?<ip>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}|([0-9A-Fa-f]{1,4}:){1,7}([0-9A-Fa-f]{1,4}|:))[\]]?(:(?<port>\d+))?$";
                        var match = Regex.Match(_id, pattern);
                        if (!match.Success)
                        {
                            MessageBox.Show(Application.Current.MainWindow,
                                Languages.Language.GetLanguageText("ipParseError"),
                                Application.Current.MainWindow.Title);
                            throw new Exception("address error");
                        }
                        var groupIP = match.Groups["ip"];
                        var groupPort = match.Groups["port"];
                        string ip = groupIP.Value;
                        if (!int.TryParse(groupPort?.Value ?? string.Empty, out int port))
                        {
                            port = 80;
                        }
                        if (!await EnsureDevice(ip, port))
                        {
                            throw new Exception("device not ready");
                        }
                        _remoteIP = ip;
                        _remotePort = port;
                    }
                }
                await RequestTcp(Command.Stop, force: true);
                IPAddress ipAddress = IPAddress.Parse(_remoteIP);
                await tcpClient.ConnectAsync(ipAddress, _remotePort);

                if (tcpClient.Connected)
                {
                    Logger.Info("connect send head");
                    await WriteTcp(TCP_HEAD);
                    await WriteTcp(new byte[] { (byte)Command.AudioData });
                    var sampleRateBytes = BitConverter.GetBytes(AudioManager.SampleRate);
                    await WriteTcp(sampleRateBytes);
                    var channelBytes = BitConverter.GetBytes(_channel == AudioChannel.Stereo ? 12 : 4);
                    await WriteTcp(channelBytes);
                    await tcpClient.GetStream().ReadAsync(new byte[1], 0, 1);
                    _ = _dispatcher.InvokeAsync(() =>
                    {
                        AudioManager.StartCapture();
                        switch (_channel)
                        {
                            case AudioChannel.Left:
                                AudioManager.LeftAvailable += SendAudioData;
                                break;
                            case AudioChannel.Right:
                                AudioManager.RightAvailable += SendAudioData;
                                break;
                            case AudioChannel.Stereo:
                                AudioManager.StereoAvailable += SendAudioData;
                                break;
                        }
                        AudioManager.Stoped += OnAudioStoped;
                    });
                    ReadAsync(tcpClient.GetStream());
                }
                SetConnectStatus(ConnectStatus.Connected);
                _retried = false;
            }
            catch (Exception ex)
            {
                await DisConnect();
                Logger.Error("connect error: " + ex.Message);
            }
            Logger.Info("connect end");
        }
        private readonly byte[] _receiveBuffer = new byte[512];
        private async void ReadAsync(NetworkStream stream)
        {
            try
            {
                while (stream.CanRead)
                {
                    await stream.ReadAsync(_receiveBuffer, 0, _receiveBuffer.Length);
                }
            }
            catch (Exception e)
            {
                // 处理异常
            }
        }

        private void OnAudioStoped(object sender, EventArgs e)
        {
            _ = DisConnect();
        }

        private readonly object writeLock = new object();
        private bool isBusy = false;
        private async void SendAudioData(object sender, WaveInEventArgs e)
        {
            lock (writeLock)
            {
                if (isBusy) return;
                isBusy = true;
            }
            if (!(await WriteTcp(e.Buffer, e.BytesRecorded, true)))
            {
                if (_retried)
                {
                    await DisConnect(true);
                }
                else
                {
                    _retried = true;
                    await Connect(true);
                }
            }
            lock (writeLock)
            {
                isBusy = false;
            }
        }

        private async Task<bool> EnsureDevice(string host, int port)
        {
            bool result = await Utils.PortIsOpen(host, port);
            string adbPath = Utils.FindAdbPath();
            if (string.IsNullOrWhiteSpace(adbPath)) return result;
            DeviceData device;
            bool needDisconnect = false;
            try
            {
                await Utils.EnsureAdb(adbPath);
                device = adbClient.GetDevices().FirstOrDefault(m => m.Serial?.StartsWith(host) ?? false);
                needDisconnect = device == null;
                if (device == null)
                {
                    if (!await Utils.PortIsOpen(host, 5555)) return result;
                    await Utils.RunCommandAsync(adbPath, $"connect {host}:5555");
                    device = adbClient.GetDevices().FirstOrDefault(m => m.Serial?.StartsWith(host) ?? false);
                }
                if (device == null) return result;
                result = await EnsureDevice(device);
            }
            catch (Exception)
            {
                return result;
            }
            if (needDisconnect) await Utils.RunCommandAsync(adbPath, $"disconnect {host}:5555");
            return result;
        }

        private Task<bool> EnsureDevice(string serial)
        {
            return EnsureDevice(adbClient.GetDevices().FirstOrDefault(m => m.Serial == serial));
        }

        private async Task<bool> EnsureDevice(DeviceData device)
        {
            if (device == null) return false;

            if (string.IsNullOrWhiteSpace(device.Serial)) return false;
            string adbPath = Utils.FindAdbPath();
            if (string.IsNullOrWhiteSpace(adbPath)) return false;
            await Utils.RunCommandAsync(adbPath, "start-server");
            string result = await Utils.RunAdbShellCommandAsync(adbClient, "dumpsys package com.picapico.audioshare|grep versionName", device);
            if (result.Contains(Utils.VersionName))
            {
                await Utils.RunAdbShellCommandAsync(adbClient, "am start -W -n com.picapico.audioshare/.MainActivity", device);
                return true;
            }
            string appPath = Process.GetCurrentProcess()?.MainModule.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(appPath)) return false;
            string apkPath = Path.Combine(Path.GetDirectoryName(appPath), Path.GetFileNameWithoutExtension(appPath) + ".apk");
            if (!File.Exists(apkPath))
            {
                MessageBox.Show(Application.Current.MainWindow, Languages.Language.GetLanguageText("apkMisMatch") +
                    Languages.Language.GetLanguageText("or") +
                    Languages.Language.GetLanguageText("apkExistsTips"),
                    Application.Current.MainWindow.Title);
                return false;
            }

            await adbClient.PushAsync(device, apkPath, "/data/local/tmp/audioshare.apk");
            await Utils.RunAdbShellCommandAsync(adbClient,
                "/system/bin/pm uninstall com.picapico.audioshare;" +
                "/system/bin/pm install -r /data/local/tmp/audioshare.apk;" +
                "rm -f /data/local/tmp/audioshare.apk;" +
                "dumpsys deviceidle whitelist +com.picapico.audioshare;", device);
            Logger.Info("install apk success");

            result = await Utils.RunAdbShellCommandAsync(adbClient, "dumpsys package com.picapico.audioshare|grep versionName", device);
            if (result.Contains(Utils.VersionName))
            {
                await Utils.RunAdbShellCommandAsync(adbClient, "am start -W -n com.picapico.audioshare/.MainActivity", device);
                return true;
            }
            MessageBox.Show(Application.Current.MainWindow,
                Languages.Language.GetLanguageText("apkMisMatch"),
                Application.Current.MainWindow.Title);
            return false;
        }

        private async Task DisConnect(bool toast=false, bool retry = false)
        {
            if (_connectStatus == ConnectStatus.UnConnected) return;
            SetConnectStatus(ConnectStatus.UnConnected, toast);
            Logger.Info("disconnect start");
            if (!retry)
            {
                _remoteIP = string.Empty;
                _remotePort = -1;
            }
            try
            {
                AudioManager.Stoped -= OnAudioStoped;
            }
            catch (Exception)
            {
            }
            await _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    switch (ChannelSelected.Key)
                    {
                        case AudioChannel.Left:
                            AudioManager.LeftAvailable -= SendAudioData;
                            break;
                        case AudioChannel.Right:
                            AudioManager.RightAvailable -= SendAudioData;
                            break;
                        case AudioChannel.Stereo:
                            AudioManager.StereoAvailable -= SendAudioData;
                            break;
                    }
                }
                catch (Exception)
                {
                }
            });
            try
            {
                tcpClient?.Close();
            }
            catch (Exception ex)
            {
                Logger.Error("stop tcp error: " + ex.Message);
            }
            tcpClient = null;
            Logger.Info("disconnect end");
        }

        private void DisConnect(object sender)
        {
            _ = DisConnect();
        }

        private void RemoveSpeaker(object sender)
        {
            Remove?.Invoke(null, this);
        }

        private bool CanConnect(object sender)
        {
            return true;
        }
        private bool CanDisConnect(object sender)
        {
            return true;
        }
        private bool CanRemoveSpeaker(object sender)
        {
            return _connectStatus == ConnectStatus.UnConnected;
        }

        private long _lastSendTime = 0;
        private static readonly byte[] _heartBeatBytes = new byte[] { 0x00, 0x00, 0x00, 0x00 };
        public void SendHeartbeat()
        {
            _dispatcher.Invoke(async () =>
            {
                if(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastSendTime > 5)
                {
                    if(!await WriteTcp(_heartBeatBytes)) {
                        _ = DisConnect();
                    }
                }
            });
        }
        private async Task<bool> WriteTcp(byte[] buffer, int length = 0, bool sendLength = false)
        {
            if (length == 0) length = buffer.Length;
            if (length == 0) return true;
            _lastSendTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try
            {
                if (tcpClient != null)
                {
                    if (sendLength)
                    {
                        var dataLength = BitConverter.GetBytes(length);
                        await tcpClient.GetStream().WriteAsync(dataLength, 0, dataLength.Length);
                    }
                    await tcpClient.GetStream().WriteAsync(buffer, 0, length);
                    await tcpClient.GetStream().FlushAsync();
                    if (length > tcpClient.SendBufferSize)
                    {
                        tcpClient.SendBufferSize = length;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("write tcp error: " + ex.Message);
            }
            return false;
        }

        private async Task RequestTcp(Command command, byte[] data = null, bool force=false)
        {
            if (command == Command.None || 
                string.IsNullOrWhiteSpace(_remoteIP) || 
                _remotePort <= 0 || 
                (!force && !Connected))
            {
                return;
            }
            TcpClient client = new TcpClient();
            client.SendTimeout = 1000;
            try
            {
                await client.ConnectAsync(_remoteIP, _remotePort);
                await client.GetStream().WriteAsync(TCP_HEAD, 0, TCP_HEAD.Length);
                await client.GetStream().WriteAsync(new byte[] { (byte)command }, 0, 1);
                if (data != null && data.Length > 0)
                {
                    await client.GetStream().WriteAsync(data, 0, data.Length);
                }
                await client.GetStream().FlushAsync();
                await client.GetStream().ReadAsync(new byte[1], 0, 1);
            }
            catch (Exception)
            {
            }
            try
            {
                client.Close();
            }
            catch (Exception)
            {
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
