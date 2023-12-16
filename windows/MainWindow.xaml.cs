using NAudio.CoreAudioApi;
using NAudio.Wave;
using SharpAdbClient;
using SharpAdbClient.DeviceCommands;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Threading;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using DeviceState = NAudio.CoreAudioApi.DeviceState;
using Microsoft.Win32;

namespace AudioShare
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private const int HTTP_PORT = 32337;
        private NotifyIcon notifyIcon;
        private ResourceDictionary zhRD;
        private ResourceDictionary enRD;
        private ResourceDictionary currentRD;
        private Settings settings = Settings.Read();
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
            if(ButtonStop.Visibility == Visibility.Visible)
            {
                ConnectionText.Content = currentRD["connected"];
            }
            else
            {
                ConnectionText.Content = currentRD["unconnected"];
            }
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
                ShellLink.Shortcut.CreateShortcut(appPath).WriteToFile(lnkPath);
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
            Activate();
            Topmost = true;
            Topmost = false;
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshAudioDevices();
            RefreshAndroidDevices();
            ConnectTCP();
        }

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
                    if (audioDeviceSelected == null)
                    {
                        CancelVolumeListener();
                        audioDeviceInstance = null;
                        return;
                    }
                    MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                    MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    var instance = devices.FirstOrDefault(m => m.ID == audioDeviceSelected.ID);
                    if(instance == null || instance?.ID != audioDeviceInstance?.ID)
                    {
                        CancelVolumeListener();
                    }
                    audioDeviceInstance = instance;
                }
            }
        }

        public ObservableCollection<AudioDevice> AndroidDevices { get; } = new ObservableCollection<AudioDevice>();

        private DeviceData androiDeviceInstance = null;
        private AudioDevice androidDeviceSelected;
        public AudioDevice AndroidDeviceSelected
        {
            get
            {
                return androidDeviceSelected;
            }
            set
            {
                if(androidDeviceSelected != value)
                {
                    androidDeviceSelected = value;
                    settings.AndroidId = androidDeviceSelected?.ID ?? string.Empty;
                    if (androidDeviceSelected == null)
                    {
                        androiDeviceInstance = null;
                        return;
                    }
                    androiDeviceInstance = adbClient.GetDevices().FirstOrDefault(m => m.Serial == androidDeviceSelected.ID);
                }
            }
        }

        private WasapiLoopbackCapture waveIn;
        private TcpClient tcpClient;

        private async void ConnectTCP()
        {
            GridLoading.Visibility = Visibility.Visible;
            ConnectLoading.Visibility = Visibility.Visible;
            try
            {
                StopTCP();
                if (IsUSB)
                {
                    var receiver = new ConsoleOutputReceiver();
                    await adbClient.ExecuteRemoteCommandAsync("am start -W -n com.picapico.audioshare/.MainActivity", androiDeviceInstance, receiver, CancellationToken.None);
                    adbClient.CreateForward(androiDeviceInstance, "tcp:" + HTTP_PORT, "localabstract:picapico-audio-share", true);
                    tcpClient = new TcpClient();
                    await tcpClient.ConnectAsync("127.0.0.1", HTTP_PORT);
                }
                else
                {
                    var addressArr = IPAddress.Split(':');
                    string ip = addressArr.FirstOrDefault()?.Trim() ?? string.Empty;
                    if(!int.TryParse(addressArr.LastOrDefault()?.Trim() ?? string.Empty, out int port))
                    {
                        port = 80;
                    }
                    tcpClient = new TcpClient();
                    await tcpClient.ConnectAsync(ip, port);
                }
                if(tcpClient.Connected)
                {
                    var sampleRateBytes = BitConverter.GetBytes(settings.SampleRate);
                    Array.Reverse(sampleRateBytes);
                    writeTcp(sampleRateBytes);
                    StartCapture();
                }
                settings.Save();
            }
            catch (Exception)
            {
                StopCapture();
                StopTCP();
            }
            GridLoading.Visibility = Visibility.Collapsed;
            ConnectLoading.Visibility = Visibility.Collapsed;
        }

        private void StartCapture()
        {
            StopCapture();
            waveIn = new WasapiLoopbackCapture(audioDeviceInstance);
            waveIn.WaveFormat = new WaveFormat(settings.SampleRate, 16, 2);
            waveIn.DataAvailable += SendAudioData;
            waveIn.StartRecording();
            SetConnectionStatus(true);
            if (IsUSB)
            {
                audioDeviceInstance.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;
                SyncAndroidVolume();
            }
        }

        private void SetConnectionStatus(bool connected)
        {
            Dispatcher.Invoke(() =>
            {
                ConnectionDot.Background = connected ? Brushes.Green : Brushes.Red;
                ConnectionText.Content = connected ? currentRD["connected"] : currentRD["unconnected"];
                ButtonRun.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
                ButtonStop.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        private void SendAudioData(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
            {
                writeTcp(e.Buffer, e.BytesRecorded);
            }
        }

        private void CancelVolumeListener()
        {
            if (audioDeviceInstance != null)
            {
                try
                {
                    audioDeviceInstance.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
                }
                catch (Exception)
                {
                }
            }
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            SyncAndroidVolume();
        }

        private bool synchronizing = false;
        private async void SyncAndroidVolume()
        {
            if (synchronizing || androiDeviceInstance == null || audioDeviceInstance == null)
            {
                return;
            }
            synchronizing = true;
            var receiver = new ConsoleOutputReceiver();
            await adbClient.ExecuteRemoteCommandAsync("dumpsys audio | grep -A 6 STREAM_MUSIC", androiDeviceInstance, receiver, CancellationToken.None);
            var output = receiver.ToString();
            Regex maxRegex = new Regex(@"Max:[\s]*([\d]+)");
            Match maxMatch = maxRegex.Match(output);
            int maxVolume = 10;
            if (maxMatch.Success)
            {
                int.TryParse(maxMatch.Groups[1].Value, out maxVolume);
            }
            int currentVolume = 10;
            Regex currentRegex = new Regex(@"streamVolume:[\s]*([\d]+)");
            Match currentMatch = currentRegex.Match(output);
            if (currentMatch.Success)
            {
                int.TryParse(currentMatch.Groups[1].Value, out currentVolume);
            }
            else
            {
                currentRegex = new Regex(@"Current[\s\S]+\(spdif\):[\s]*([\d]+)");
                currentMatch = currentRegex.Match(output);
                if (currentMatch.Success)
                {
                    int.TryParse(currentMatch.Groups[1].Value, out currentVolume);
                }
            }
            int volumeSet = (int)(audioDeviceInstance.AudioEndpointVolume.MasterVolumeLevelScalar * maxVolume);
            if (currentVolume == volumeSet)
            {
                synchronizing = false;
                return;
            }
            string command = $"input keyevent KEYCODE_VOLUME_{(currentVolume > volumeSet ? "DOWN" : "UP")}";
            var commandArray = Enumerable.Repeat(command, Math.Abs(currentVolume - volumeSet));
            var receiver1 = new ConsoleOutputReceiver();
            adbClient.ExecuteShellCommand(androiDeviceInstance, string.Join(" && ", commandArray), receiver1);
            synchronizing = false;
        }

        private void writeTcp(byte[] buffer, int length=0)
        {
            if (length == 0) length = buffer.Length;
            try
            {
                tcpClient?.GetStream().Write(buffer, 0, length);
            }
            catch (Exception)
            {
                StopCapture();
                StopTCP();
            }
        }

        private void Run(object sender, RoutedEventArgs e)
        {
            if (AudioDeviceSelected == null || (AndroidDeviceSelected == null && IsUSB))
            {
                return;
            }
            ConnectTCP();
        }

        private void Stop(object sender, RoutedEventArgs e)
        {
            StopCapture();
            StopTCP();
        }

        private void Exit(object sender, RoutedEventArgs e)
        {
            StopCapture();
            StopTCP();
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


        private void RefreshAndroidDevices(object sender, RoutedEventArgs e)
        {
            RefreshAndroidDevices();
        }
        AdbClient adbClient = new AdbClient();
        private void RefreshAndroidDevices()
        {
            AdbServer server = new AdbServer();
            string adbPath = FindAdbPath();
            if (string.IsNullOrWhiteSpace(adbPath))
            {
                return;
            }
            var result = server.StartServer(adbPath, restartServerIfNewer: false);
            if (result == StartServerResult.RestartedOutdatedDaemon)
            {
                return;
            }

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
        }

        static string FindAdbPath()
        {
            string[] pathDirectories = Environment.GetEnvironmentVariable("PATH").Split(';');

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

        private bool captureStoping = false;
        private void StopCapture()
        {
            if (captureStoping) return;
            captureStoping = true;
            SetConnectionStatus(false);
            CancelVolumeListener();
            try
            {
                if(waveIn != null)
                {
                    waveIn.DataAvailable -= SendAudioData;
                    waveIn.StopRecording();
                    waveIn.Dispose();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Stop Recording Error: " + ex.Message);
            }
            waveIn = null;
            captureStoping = false;
        }

        private bool tcpStoping = false;
        private void StopTCP()
        {
            if (tcpStoping) return;
            tcpStoping = true;
            try
            {
                tcpClient?.Close();
                tcpClient?.Dispose();
            }
            catch (Exception)
            {
            }
            tcpClient = null;
            tcpStoping = false;
            SetConnectionStatus(false);
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
