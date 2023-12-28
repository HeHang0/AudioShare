using Microsoft.Toolkit.Uwp.Notifications;
using NAudio.CoreAudioApi;
using SharpAdbClient;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DeviceState = NAudio.CoreAudioApi.DeviceState;
using NamePair = System.Collections.Generic.KeyValuePair<string, string>;
using SampleRatePair = System.Collections.Generic.KeyValuePair<int, string>;

namespace AudioShare
{
    public class Model : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private readonly Settings _settings = Settings.Load();

        private readonly Dispatcher _dispatcher;
        private UdpClient _udpListener;
        public Model()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            AudioManager.OnVolumeNotification += OnVolumeChanged;
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            ConnectUdp();
        }

        private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
        {
            var speaker = Speakers.FirstOrDefault(m => m.UnConnected && m.Id == e.Argument);
            if (speaker != null)
            {
                _ = speaker.Connect();
            }
        }

        private async void ConnectUdp()
        {
            for (int i = 58261; i < 58271; i++)
            {
                try
                {
                    _udpListener = new UdpClient(i);
                    _udpListener.EnableBroadcast = true;
                }
                catch (Exception)
                {
                }
            }
            if (_udpListener == null) return;
            while (true)
            {
                await Task.Delay(1000);
                UdpReceiveResult result = await _udpListener.ReceiveAsync();
                if (result.Buffer.Length > 26) continue;
                string message = Encoding.UTF8.GetString(result.Buffer);
                var messages = message.Split('@');
                if (messages.Length != 2 || messages[0] != "picapico-audio-share") continue;
                if (int.TryParse(messages[1], out int port) && port > 0 && port < 65535)
                {
                    _dispatcher.Invoke(() =>
                    {
                        AddIPSpeaker(result.RemoteEndPoint.Address.ToString(), port);
                    });
                }
            }
        }

        private void OnVolumeChanged(object sender, int volume)
        {
            if (VolumeFollowSystem) Volume = volume;
        }

        private readonly List<MMDevice> _audioDevices = new List<MMDevice>();
        public ObservableCollection<NamePair> AudioDevices { get; private set; } = new ObservableCollection<NamePair>();
        public ObservableCollection<Speaker> Speakers { get; private set; } = new ObservableCollection<Speaker>();
        public ObservableCollection<SampleRatePair> SampleRates => new ObservableCollection<SampleRatePair>()
        {
            //192000, 176400, 96000, 48000, 44100
            new SampleRatePair(192000, "192kHz"),
            new SampleRatePair(176400, "176.4kHz"),
            new SampleRatePair(96000, "96kHz"),
            new SampleRatePair(48000, "48kHz"),
            new SampleRatePair(44100, "44.1kHz"),
        };
        public ImageSource Icon => Utils.AppIcon;
        public string Title => Languages.Language.GetLanguageText("title") + " " + Utils.VersionName;
        public void UpdateTitle()
        {
            OnPropertyChanged(nameof(Title));
        }
        public bool IsStartup
        {
            get => IsStartupEnabled();
            set
            {
                SetStartup(value);
            }
        }
        private int _connectedCount = 0;
        public bool Connected => _connectedCount > 0;
        public bool UnConnected => _connectedCount <= 0;
        public int ConnectedCount
        {
            get => _connectedCount;
            set
            {
                _connectedCount = value;
                OnPropertyChanged(nameof(ConnectedCount));
                OnPropertyChanged(nameof(Connected));
                OnPropertyChanged(nameof(UnConnected));
            }
        }
        public NamePair AudioSelected
        {
            get => AudioDevices.FirstOrDefault(m => m.Key == _settings.AudioId);
            set
            {
                _settings.AudioId = value.Key;
                var mDevice = _audioDevices.FirstOrDefault(m => m.ID == _settings.AudioId);
                if (VolumeFollowSystem)
                {
                    try
                    {
                        Volume = mDevice.AudioEndpointVolume.Mute ? 0 : (int)(mDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                    }
                    catch (Exception)
                    {
                    }
                }
                Stop();
                AudioManager.SetDevice(mDevice, _settings.SampleRate);
                OnPropertyChanged(nameof(AudioSelected));
            }
        }
        public SampleRatePair SampleRate
        {
            get => SampleRates.FirstOrDefault(m => m.Key == _settings.SampleRate);
            set
            {
                _settings.SampleRate = value.Key;
                var mDevice = _audioDevices.FirstOrDefault(m => m.ID == _settings.AudioId);
                Stop();
                AudioManager.SetDevice(mDevice, _settings.SampleRate);
            }
        }
        public int Volume
        {
            get => _settings.Volume;
            set
            {
                _settings.Volume = value;
                OnPropertyChanged(nameof(Volume));
                foreach (var speaker in Speakers)
                {
                    if (speaker.Connected)
                    {
                        speaker.SetVolume(value);
                    }
                }
            }
        }
        private bool _adbLoading = false;
        public bool AdbLoading
        {
            get => _adbLoading;
            set
            {
                _adbLoading = value;
                OnPropertyChanged(nameof(AdbLoading));
            }
        }
        public bool IsIP => !_settings.IsUSB;
        public bool IsUSB
        {
            get => _settings.IsUSB;
            set
            {
                if (_settings.IsUSB == value) return;
                ResetSpeakerSetting();
                _settings.IsUSB = value;
                if (value && string.IsNullOrWhiteSpace(Utils.FindAdbPath()))
                {
                    _settings.IsUSB = false;
                    MessageBox.Show(Application.Current.MainWindow, Languages.Language.GetLanguageText("adbMisMatch"),
                        Application.Current.MainWindow.Title);
                }
                else
                {
                    _ = RefreshSpeakers();
                }
                OnPropertyChanged(nameof(IsUSB));
                OnPropertyChanged(nameof(IsIP));
                OnPropertyChanged(nameof(AddIPSpeakerVisible));
            }
        }
        private bool _addIPSpeakerVisible = true;
        public bool AddIPSpeakerVisible
        {
            get => _addIPSpeakerVisible && IsIP;
            set
            {
                _addIPSpeakerVisible = value;
                OnPropertyChanged(nameof(AddIPSpeakerVisible));
            }
        }
        public bool VolumeFollowSystem
        {
            get => _settings.VolumeFollowSystem;
            set
            {
                _settings.VolumeFollowSystem = value;
                if (value)
                {
                    var mDevice = _audioDevices.FirstOrDefault(m => m.ID == _settings.AudioId);
                    if (VolumeFollowSystem)
                    {
                        try
                        {
                            Volume = mDevice.AudioEndpointVolume.Mute ? 0 : (int)(mDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                OnPropertyChanged(nameof(VolumeCustom));
            }
        }
        public bool VolumeCustom => !_settings.VolumeFollowSystem;

        public RelayCommand RefreshAudiosCommand => new RelayCommand(RefreshAudios, CanRefreshAudios);

        public RelayCommand RefreshSpeakersCommand => new RelayCommand(RefreshSpeakers, CanRefreshSpeakers);

        public RelayCommand AddIPSpeakerCommand => new RelayCommand(AddIPSpeaker, CanAddIPSpeaker);

        public void RefreshAudios()
        {
            RefreshAudios(null);
        }

        public void Stop()
        {
            foreach (var speaker in Speakers)
            {
                speaker.Dispose();
            }
        }

        readonly AdbClient _adbClient = new AdbClient();
        public async Task RefreshSpeakers()
        {
            var connectedSpeakers = Speakers.Where(speaker => speaker.Connected).ToList();
            if (IsUSB)
            {
                string adbPath = Utils.FindAdbPath();
                if (string.IsNullOrWhiteSpace(adbPath))
                {
                    IsUSB = false;
                    Stop();
                    return;
                }
                AdbLoading = true;
                await Utils.RunCommandAsync(adbPath, "start-server");
                await Utils.RunCommandAsync(adbPath, "devices");
                var devices = _adbClient.GetDevices();
                Speakers.Clear();
                foreach (var item in devices)
                {
                    if (item.State != SharpAdbClient.DeviceState.Online) continue;
                    var connectedSpeaker = connectedSpeakers.FirstOrDefault(m => m.Id == item.Serial);
                    if (connectedSpeaker != null)
                    {
                        Speakers.Add(connectedSpeaker);
                    }
                    else
                    {
                        var savedSpeaker = _settings.AdbDevices.FirstOrDefault(m => m.Id == item.Serial);
                        var speaker = new Speaker(_dispatcher, item.Serial, $"{item.Name} {item.Model}", savedSpeaker?.Channel ?? AudioChannel.None);
                        speaker.Remove += OnRemoveSpeaker;
                        speaker.ConnectStatusChanged += OnConnectStatusChanged;
                        Speakers.Add(speaker);
                    }
                }
                AdbLoading = false;
            }
            else
            {
                Speakers.Clear();
                HashSet<string> addresses = new HashSet<string>();
                foreach (var item in _settings.IPDevices)
                {
                    if (addresses.Contains(item.Id)) continue;
                    addresses.Add(item.Id);
                    var connectedSpeaker = connectedSpeakers.FirstOrDefault(m => m.Id == item.Id);
                    if (connectedSpeaker != null)
                    {
                        Speakers.Add(connectedSpeaker);
                    }
                    else
                    {
                        var speaker = new Speaker(_dispatcher, item.Id, item.Channel);
                        speaker.Remove += OnRemoveSpeaker;
                        speaker.ConnectStatusChanged += OnConnectStatusChanged;
                        Speakers.Add(speaker);
                    }
                }
            }
            foreach (var speaker in connectedSpeakers)
            {
                if (!Speakers.Any(m => m.Id == speaker.Id))
                {
                    speaker.Dispose();
                }
            }
        }

        private void AddIPSpeaker(object sender)
        {
            if (IsUSB) return;
            for (int i = 2; i < 255; i++)
            {
                string id = $"192.168.1.{i}:8088";
                if (!_settings.IPDevices.Any(m => m.Id == id))
                {
                    _settings.IPDevices.Add(new Settings.Device(id, AudioChannel.None));
                    var speaker = new Speaker(_dispatcher, id, AudioChannel.None);
                    speaker.Remove += OnRemoveSpeaker;
                    speaker.ConnectStatusChanged += OnConnectStatusChanged;
                    Speakers.Add(speaker);
                    return;
                }
            }
            AddIPSpeakerVisible = false;
        }

        private void AddIPSpeaker(string ip, int port)
        {
            string id = $"{ip}:{port}";
            if (!_settings.IPDevices.Any(m => m.Id == id))
            {
                _settings.IPDevices.Add(new Settings.Device(id, AudioChannel.None));
                if (!IsUSB)
                {
                    var speaker = new Speaker(_dispatcher, id, AudioChannel.None);
                    speaker.Remove += OnRemoveSpeaker;
                    speaker.ConnectStatusChanged += OnConnectStatusChanged;
                    Speakers.Add(speaker);
                }
                _settings.Save();
                return;
            }
        }

        private void RefreshSpeakers(object sender)
        {
            _ = RefreshSpeakers();
        }

        public void ResetSpeakerSetting()
        {
            if (IsUSB)
            {
                _settings.AdbDevices.Clear();
                foreach (var speaker in Speakers)
                {
                    _settings.AdbDevices.Add(new Settings.Device(speaker.Id, speaker.ChannelSelected.Key));
                }
            }
            else
            {
                _settings.IPDevices.Clear();
                foreach (var speaker in Speakers)
                {
                    _settings.IPDevices.Add(new Settings.Device(speaker.Id, speaker.ChannelSelected.Key));
                }
            }
        }

        private void RefreshAudios(object sender)
        {
            Stop();
            AudioManager.StopCapture();
            AudioManager.SetDevice(null, _settings.SampleRate);
            MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
            _audioDevices.Clear();
            AudioDevices.Clear();
            MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            MMDevice mDevice = null;
            foreach (var device in devices)
            {
                _audioDevices.Add(device);
                AudioDevices.Add(new NamePair(device.ID, device.FriendlyName));
                if (_settings.AudioId == device.ID) mDevice = device;
            }
            if (mDevice == null) mDevice = devices.FirstOrDefault();
            AudioSelected = new NamePair(mDevice?.ID, mDevice?.FriendlyName);
        }

        private static void SetStartup(bool startup)
        {
            string startupFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string appPath = Process.GetCurrentProcess()?.MainModule.FileName ?? string.Empty;
            string lnkPath = Path.Combine(startupFolderPath, Path.GetFileNameWithoutExtension(appPath) + ".lnk");
            var exists = File.Exists(lnkPath);
            if (exists && startup) return;
            if (!exists && !startup) return;
            if (startup)
            {
                ShellLink.Shortcut.CreateShortcut(appPath, "startup").WriteToFile(lnkPath);
            }
            else
            {
                File.Delete(lnkPath);
            }
        }

        private static bool IsStartupEnabled()
        {
            string startupFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string appPath = Process.GetCurrentProcess()?.MainModule.FileName ?? string.Empty;
            string lnkPath = Path.Combine(startupFolderPath, Path.GetFileNameWithoutExtension(appPath) + ".lnk");
            return File.Exists(lnkPath);
        }

        private bool CanRefreshAudios(object sender)
        {
            return true;
        }

        private bool CanRefreshSpeakers(object sender)
        {
            return true;
        }

        private bool CanAddIPSpeaker(object sender)
        {
            return IsIP;
        }

        private void OnRemoveSpeaker(object sender, Speaker speaker)
        {
            speaker?.Dispose();
            Speakers.Remove(speaker);
            ResetSpeakerSetting();
            _settings.Save();
        }

        private static CancellationTokenSource SetConnectStatusCancel = null;
        private async void OnConnectStatusChanged(object sender, ConnectStatus status)
        {
            if (status == ConnectStatus.Connecting) return;
            SetConnectStatusCancel?.Cancel();
            SetConnectStatusCancel = new CancellationTokenSource();
            CancellationToken token = SetConnectStatusCancel.Token;
            await Task.Delay(200);
            if (token.IsCancellationRequested) return;
            Logger.Info("connect status changed start");
            if (status == ConnectStatus.Connected)
            {
                if (sender != null && sender is Speaker)
                {
                    ((Speaker)sender).SetVolume(Volume);
                }
                List<Speaker> allConnected = Speakers.Where(speaker => speaker.Connected).ToList();
                foreach (var speaker in allConnected)
                {
                    if (speaker.Connected) speaker.SyncTime();
                }
                ResetSpeakerSetting();
                _settings.Save();
            }
            if (status != ConnectStatus.Connecting)
            {
                ConnectedCount = Speakers.Where(m => m.Connected).Count();
            }
            Logger.Info("connect status changed end");
            //if(Speakers.Where(m => m.Connected || m.Connecting).Count() == 0)
            //{
            //    AudioManager.StopCapture();
            //}
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
