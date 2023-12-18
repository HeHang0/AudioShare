using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
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
using DeviceState = NAudio.CoreAudioApi.DeviceState;
using NotifyIcon = System.Windows.Forms.NotifyIcon;

namespace AudioShare
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private const int HTTP_PORT = 32337;
        private readonly byte[] TCP_HEAD = Encoding.Default.GetBytes("picapico-audio-share");
        private NotifyIcon notifyIcon;
        private ResourceDictionary zhRD;
        private ResourceDictionary enRD;
        private ResourceDictionary currentRD;
        private readonly Settings settings = Settings.Read();
        enum Command
        {
            None = 0,
            AudioData = 1,
            Volume = 2,
            SampleRate = 3
        }
        public MainWindow()
        {
            InitializeComponent();
            InitLanguage();
            DataContext = this;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            InitNotify();
        }

        private void InitLanguage()
        {
            zhRD = Application.Current.Resources.MergedDictionaries.First(m => m.Source?.OriginalString.Contains("zh-cn") ?? false);
            enRD = Application.Current.Resources.MergedDictionaries.First(m => m.Source?.OriginalString.Contains("en-us") ?? false);
            bool isChinese = System.Globalization.CultureInfo.InstalledUICulture.Name.ToLower().Contains("zh");
            currentRD = isChinese ? zhRD : enRD;
            Application.Current.Resources.MergedDictionaries.Remove(isChinese ? enRD : zhRD);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Locale)
            {
                SetLanguage(System.Globalization.CultureInfo.InstalledUICulture.Name.ToLower().Contains("zh"));
            }
        }

        private void SetLanguage(bool isChinese)
        {
            currentRD = isChinese ? zhRD : enRD;
            Application.Current.Resources.MergedDictionaries.Add(currentRD);
            Application.Current.Resources.MergedDictionaries.Remove(isChinese ? enRD : zhRD);
        }

        private void SetStartup(bool startup)
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

        string GetExePath()
        {
            Process currentProcess = Process.GetCurrentProcess();
            return currentProcess.MainModule?.FileName ?? string.Empty;
        }

        private void InitNotify()
        {
            notifyIcon = new NotifyIcon()
            {
                Text = "Audio Share",
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(GetExePath())
            };
            notifyIcon.Click += OnNotifyIconClick;
            notifyIcon.Visible = true;
        }

        private void OnNotifyIconClick(object sender, EventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            Hide();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
            RefreshAudioDevices();
            await RefreshAndroidDevices();
            await ConnectTCP();
            if (Connected)
            {
                Hide();
            }
            else
            {
                OnNotifyIconClick(null, null);
            }
        }

        #region Model

        public bool IsUSB
        {
            get
            {
                return settings.IsUSB;
            }
            set
            {
                settings.IsUSB = value;
                OnPropertyChanged(nameof(IsUSB));
                OnPropertyChanged(nameof(IsIP));
            }
        }

        public string IPAddress
        {
            get
            {
                return settings.IPAddress;
            }
            set
            {
                settings.IPAddress = value;
            }
        }

        public bool VolumeFollowSystem
        {
            get
            {
                return settings.VolumeFollowSystem;
            }
            set
            {
                settings.VolumeFollowSystem = value;
                OnPropertyChanged(nameof(VolumeFollowSystem));
                OnPropertyChanged(nameof(VolumeCustom));
                if (value) SyncVolume();
            }
        }

        public bool VolumeCustom => !VolumeFollowSystem;

        public int Volume
        {
            get
            {
                return settings.Volume;
            }
            set
            {
                settings.Volume = value;
                OnPropertyChanged(nameof(Volume));
                SetRemoteVolume();
            }
        }

        public bool IsIP => !IsUSB;

        public bool IsStartup
        {
            get
            {
                return IsStartupEnabled();
            }
            set
            {
                SetStartup(value);
            }
        }

        public int[] SampleRates => new int[] { 192000, 176400, 96000, 48000, 44100 };
        public int SampleRateSelected
        {
            get
            {
                return settings.SampleRate;
            }
            set
            {
                settings.SampleRate = value;
            }
        }

        public ObservableCollection<AudioDevice> AudioDevices { get; } = new ObservableCollection<AudioDevice>();

        private MMDevice audioDeviceInstance = null;
        private AudioDevice audioDeviceSelected;
        public AudioDevice AudioDeviceSelected
        {
            get
            {
                return audioDeviceSelected;
            }
            set
            {
                if (audioDeviceSelected != value)
                {
                    audioDeviceSelected = value;
                    settings.AudioId = audioDeviceSelected?.ID ?? string.Empty;
                    CancelVolumeListener();
                    if (audioDeviceSelected == null)
                    {
                        audioDeviceInstance = null;
                        return;
                    }
                    MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                    MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    var instance = devices.FirstOrDefault(m => m.ID == audioDeviceSelected.ID);
                    if (instance == null || instance?.ID != audioDeviceInstance?.ID)
                    {
                    }
                    audioDeviceInstance = instance;
                    SyncVolume();
                    audioDeviceInstance.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                }
            }
        }

        public ObservableCollection<AudioDevice> AndroidDevices { get; } = new ObservableCollection<AudioDevice>();

        private DeviceData androidDeviceInstance = null;
        private AudioDevice androidDeviceSelected;
        public AudioDevice AndroidDeviceSelected
        {
            get
            {
                return androidDeviceSelected;
            }
            set
            {
                if (androidDeviceSelected != value)
                {
                    androidDeviceSelected = value;
                    settings.AndroidId = androidDeviceSelected?.ID ?? string.Empty;
                    if (androidDeviceSelected == null)
                    {
                        androidDeviceInstance = null;
                        return;
                    }
                    androidDeviceInstance = adbClient.GetDevices().FirstOrDefault(m => m.Serial == androidDeviceSelected.ID);
                }
            }
        }

        private bool loading = false;
        public bool Loading
        {
            get
            {
                return loading;
            }
            set
            {
                loading = value;
                OnPropertyChanged(nameof(Loading));
            }
        }

        private bool adbLoading = false;
        public bool AdbLoading
        {
            get
            {
                return adbLoading;
            }
            set
            {
                adbLoading = value;
                OnPropertyChanged(nameof(AdbLoading));
                OnPropertyChanged(nameof(IsAndroidDevicesEnabled));
            }
        }

        public bool IsAndroidDevicesEnabled => !AdbLoading && UnConnected;

        private bool connected = false;
        public bool Connected
        {
            get
            {
                return connected;
            }
            set
            {
                connected = value;
                OnPropertyChanged(nameof(Connected));
                OnPropertyChanged(nameof(UnConnected));
                OnPropertyChanged(nameof(IsAndroidDevicesEnabled));
            }
        }
        public bool UnConnected => !Connected;
        #endregion

        private WasapiLoopbackCapture waveIn = null;
        private TcpClient tcpClient = null;

        private async Task ConnectTCP()
        {
            Loading = true;
            try
            {
                StopTCP();
                tcpClient = new TcpClient();
                if (IsUSB)
                {
                    var receiver = new ConsoleOutputReceiver();
                    await adbClient.ExecuteRemoteCommandAsync("am start -W -n com.picapico.audioshare/.MainActivity", androidDeviceInstance, receiver, CancellationToken.None);
                    adbClient.CreateForward(androidDeviceInstance, "tcp:" + HTTP_PORT, "localabstract:picapico-audio-share", true);
                    await tcpClient.ConnectAsync("127.0.0.1", HTTP_PORT);
                }
                else
                {
                    var addressArr = IPAddress.Split(':');
                    string ip = addressArr.FirstOrDefault()?.Trim() ?? string.Empty;
                    if (!int.TryParse(addressArr.LastOrDefault()?.Trim() ?? string.Empty, out int port))
                    {
                        port = 80;
                    }
                    await tcpClient.ConnectAsync(ip, port);
                }
                if (tcpClient.Connected)
                {
                    WriteTcp(TCP_HEAD);
                    WriteTcp(new byte[] { (byte)Command.AudioData });
                    var sampleRateBytes = BitConverter.GetBytes(settings.SampleRate);
                    WriteTcp(sampleRateBytes);
                    StartCapture();
                }
                settings.Save();
            }
            catch (Exception)
            {
                Stop();
            }
            Loading = false;
        }

        private void StartCapture()
        {
            StopCapture();
            waveIn = new WasapiLoopbackCapture(audioDeviceInstance);
            waveIn.WaveFormat = new WaveFormat(settings.SampleRate, 16, 2);
            waveIn.DataAvailable += SendAudioData;
            waveIn.StartRecording();
            Dispatcher.Invoke(() =>
            {
                Connected = true;
            });
            SetRemoteVolume();
        }

        private void SendAudioData(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
            {
                var data = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, data, e.BytesRecorded);
                Dispatcher.InvokeAsync(() =>
                {
                    if (!WriteTcp(e.Buffer, e.BytesRecorded))
                    {
                        Stop();
                    }
                });
            }
        }

        private void CancelVolumeListener()
        {
            if (audioDeviceInstance != null)
            {
                try
                {
                    audioDeviceInstance.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                }
                catch (Exception)
                {
                }
            }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            SyncVolume();
        }

        private void SyncVolume()
        {
            if (audioDeviceInstance == null || VolumeCustom)
            {
                return;
            }
            Volume = (int)(audioDeviceInstance.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        }

        private CancellationTokenSource SetRemoteVolumeCancel = null;
        private async void SetRemoteVolume()
        {
            SetRemoteVolumeCancel?.Cancel();
            SetRemoteVolumeCancel = new CancellationTokenSource();
            CancellationToken token = SetRemoteVolumeCancel.Token;
            await Task.Delay(200);
            if (token.IsCancellationRequested) return;
            byte[] volumeBytes = BitConverter.GetBytes(Volume);
            RequestTcp(Command.Volume, volumeBytes);
        }

        private async void RequestTcp(Command command, byte[] data)
        {
            if (UnConnected) return;
            TcpClient client = new TcpClient();
            try
            {
                if (IsUSB)
                {
                    await client.ConnectAsync("127.0.0.1", HTTP_PORT);
                }
                else
                {
                    var addressArr = IPAddress.Split(':');
                    string ip = addressArr.FirstOrDefault()?.Trim() ?? string.Empty;
                    if (!int.TryParse(addressArr.LastOrDefault()?.Trim() ?? string.Empty, out int port))
                    {
                        port = 80;
                    }
                    await client.ConnectAsync(ip, port);
                }
                client.GetStream().Write(TCP_HEAD, 0, TCP_HEAD.Length);
                client.GetStream().Write(new byte[] { (byte)command }, 0, 1);
                client.GetStream().Write(data, 0, data.Length);
                await client.GetStream().FlushAsync();
                await Task.Delay(1000);
            }
            catch (Exception)
            {
            }
            try
            {
                client.Close();
                client.Dispose();
            }
            catch (Exception)
            {

                throw;
            }
        }

        private bool WriteTcp(byte[] buffer, int length = 0)
        {
            if (length == 0) length = buffer.Length;
            try
            {
                if (tcpClient != null)
                {
                    tcpClient.GetStream().Write(buffer, 0, length);
                    tcpClient.GetStream().Flush();
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private async void Run(object sender, RoutedEventArgs e)
        {
            if (AudioDeviceSelected == null || (AndroidDeviceSelected == null && IsUSB))
            {
                return;
            }
            await ConnectTCP();
        }

        private void Stop(object sender, RoutedEventArgs e)
        {
            Stop();
        }

        private void Stop()
        {
            StopCapture();
            StopTCP();
            Connected = false;
        }

        private void Exit(object sender, RoutedEventArgs e)
        {
            Stop();
            Application.Current.Shutdown();
        }

        private void RefreshAudioDevices(object sender, RoutedEventArgs e)
        {
            RefreshAudioDevices();
        }

        private void RefreshAudioDevices()
        {
            MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
            MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            string selectedId = settings.AudioId;
            if (string.IsNullOrWhiteSpace(selectedId) && enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                selectedId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID;
            }
            AudioDevices.Clear();
            foreach (MMDevice device in devices)
            {
                AudioDevices.Add(new AudioDevice(device.ID, device.FriendlyName));
            }
            AudioDeviceSelected = AudioDevices.FirstOrDefault(m => m.ID == selectedId);
            if (AudioDeviceSelected == null)
            {
                AudioDeviceSelected = AudioDevices.FirstOrDefault();
            }
            OnPropertyChanged(nameof(AudioDeviceSelected));
        }


        private async void RefreshAndroidDevices(object sender, RoutedEventArgs e)
        {
            await RefreshAndroidDevices();
        }
        readonly AdbClient adbClient = new AdbClient();
        private async Task RefreshAndroidDevices()
        {
            if (AdbLoading) return;
            string adbPath = FindAdbPath();
            if (string.IsNullOrWhiteSpace(adbPath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "devices",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            AdbLoading = true;
            Process process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            var completionSource = new TaskCompletionSource<int>();
            process.Exited += (sender, args) =>
            {
                completionSource.SetResult(process.ExitCode);
                process.Dispose();
            };
            if (process.Start())
            {
                await completionSource.Task;
            }

            AdbServer server = new AdbServer();
            var result = server.StartServer(adbPath, restartServerIfNewer: false);
            if (result == StartServerResult.RestartedOutdatedDaemon)
            {
                Dispatcher.Invoke(() =>
                {
                    AdbLoading = false;
                });
                return;
            }
            Dispatcher.Invoke(() =>
            {
                AndroidDevices.Clear();
                var devices = adbClient.GetDevices();
                foreach (var device in devices)
                {
                    AndroidDevices.Add(new AudioDevice(device.Serial, $"{device.Name} {device.Model}"));
                }
                AndroidDeviceSelected = AndroidDevices.FirstOrDefault(m => m.ID == settings.AndroidId);
                if (AndroidDeviceSelected == null)
                {
                    AndroidDeviceSelected = AndroidDevices.FirstOrDefault();
                }
                OnPropertyChanged(nameof(AndroidDeviceSelected));
                AdbLoading = false;
            });
        }

        static string FindAdbPath()
        {
            List<string> pathDirectories = Environment.GetEnvironmentVariable("PATH").Split(';').ToList();
            var mainModule = Process.GetCurrentProcess()?.MainModule;
            if (mainModule != null)
            {
                pathDirectories.Add(Path.GetDirectoryName(mainModule.FileName));
            }
            foreach (string directory in pathDirectories)
            {
                string adbPath = Path.Combine(directory, "adb.exe");
                if (File.Exists(adbPath))
                {
                    return adbPath;
                }
            }

            return null;
        }

        private void StopCapture()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (waveIn != null)
                    {
                        waveIn.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("Stop Recording Error: " + ex.Message);
                }
                waveIn = null;
            });
        }

        private void StopTCP()
        {
            try
            {
                tcpClient?.Close();
                tcpClient?.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Stop Tcp Error: " + ex.Message);
            }
            tcpClient = null;
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
