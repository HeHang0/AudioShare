using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SharpAdbClient;
using SharpAdbClient.DeviceCommands;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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
        private string versionName = string.Empty;
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
            InitVersion();
            DataContext = this;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            InitNotify();
            InitNamedPipeServerStream();
        }

        private async void InitNamedPipeServerStream()
        {
            NamedPipeServerStream serverStream = new NamedPipeServerStream("_AUDIO_SHARE_PIPE", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await serverStream.WaitForConnectionAsync();
                Dispatcher.Invoke(() =>
                {
                    ShowWindow();
                });
                serverStream.Close();
            }
            catch (Exception ex)
            {
            }
            InitNamedPipeServerStream();
        }

        private void InitVersion()
        {
            var version = Application.ResourceAssembly.GetName()?.Version;
            if (version == null) return;
            versionName = $"{version.Major}.{version.Minor}.{version.Build}";
            Title += $" {versionName}";
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
            ShowWindow();
        }

        private void ShowWindow()
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
                ShowWindow();
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

        public ObservableCollection<NamePair> AudioDevices { get; } = new ObservableCollection<NamePair>();

        private MMDevice audioDeviceInstance = null;
        private NamePair audioDeviceSelected;
        public NamePair AudioDeviceSelected
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

        public ObservableCollection<NamePair> AndroidDevices { get; } = new ObservableCollection<NamePair>();

        private DeviceData androidDeviceInstance = null;
        private NamePair androidDeviceSelected;
        public NamePair AndroidDeviceSelected
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
                tcpClient.NoDelay = true;
                if (IsUSB)
                {
                    if(!await EnsureDevice(androidDeviceInstance))
                    {
                        throw new Exception("device not ready");
                    }
                    adbClient.CreateForward(androidDeviceInstance, "tcp:" + HTTP_PORT, "localabstract:picapico-audio-share", true);
                    await tcpClient.ConnectAsync("127.0.0.1", HTTP_PORT);
                }
                else
                {
                    var addressArr = IPAddress.Split(':');
                    string ip = addressArr.FirstOrDefault()?.Trim() ?? string.Empty;
                    if (addressArr.Length < 2 ||
                        !int.TryParse(addressArr.LastOrDefault()?.Trim() ?? string.Empty, out int port))
                    {
                        port = 80;
                    }
                    if(!await EnsureDevice(ip, port))
                    {
                        throw new Exception("device not ready");
                    }
                    await tcpClient.ConnectAsync(ip, port);
                }
                if (tcpClient.Connected)
                {
                    WriteTcp(TCP_HEAD);
                    WriteTcp(new byte[] { (byte)Command.AudioData });
                    var sampleRateBytes = BitConverter.GetBytes(settings.SampleRate);
                    WriteTcp(sampleRateBytes);
                    var channelBytes = BitConverter.GetBytes(12);
                    WriteTcp(channelBytes);
                    tcpClient.GetStream().Read(new byte[1], 0, 1);
                    StartCapture();
                }
                settings.Save();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("connect tcp error: " + ex);
                Stop();
            }
            Loading = false;
        }

        private async Task<bool> PortIsOpen(string host, int port)
        {
            try
            {
                using (TcpClient tcpClient = new TcpClient())
                {
                    tcpClient.ReceiveTimeout = 1000;
                    await tcpClient.ConnectAsync(host, port);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async Task<bool> EnsureDevice(string host, int port)
        {
            bool result = await PortIsOpen(host, port);
            DeviceData device;
            bool needDisconnect = false;
            try
            {
                device = adbClient.GetDevices().FirstOrDefault(m => m.Serial?.StartsWith(host) ?? false);
                needDisconnect = device == null;
                if (device == null)
                {
                    if (!await PortIsOpen(host, 5555)) return result;
                    await RunCommandAsync(FindAdbPath(), $"connect {host}:5555");
                    device = adbClient.GetDevices().FirstOrDefault(m => m.Serial?.StartsWith(host) ?? false);
                }
                if (device == null) return result;
                result = await EnsureDevice(device);
            }
            catch (Exception)
            {
                return result;
            }
            if(needDisconnect) await RunCommandAsync(FindAdbPath(), $"disconnect {host}:5555");
            return result;
        }

        private async Task<bool> EnsureDevice(DeviceData device)
        {
            if (device == null) return false;

            if (string.IsNullOrEmpty(device.Serial)) return false;

            string result = await ExecuteRemoteCommandAsync("dumpsys package com.picapico.audioshare|grep versionName", androidDeviceInstance);
            if (result.Contains(versionName))
            {
                await ExecuteRemoteCommandAsync("am start -W -n com.picapico.audioshare/.MainActivity", androidDeviceInstance);
                return true;
            }
            string appPath = Process.GetCurrentProcess()?.MainModule.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(appPath)) return false;
            string apkPath = Path.Combine(Path.GetDirectoryName(appPath), Path.GetFileNameWithoutExtension(appPath)+".apk");
            if(!File.Exists(apkPath))
            {
                WindowState = WindowState.Normal;
                MessageBox.Show(this, $"{currentRD["apkMisMatch"]}{currentRD["or"]}{currentRD["apkExistsTips"]}", Title);
                return false;
            }

            await ExecuteRemoteCommandAsync("rm -f /data/local/tmp/audioshare.apk", androidDeviceInstance);
            await RunCommandAsync(FindAdbPath(), $"-s {device.Serial} push \"{apkPath}\" /data/local/tmp/audioshare.apk");
            string shellPath = Path.Combine(Path.GetTempPath(), "audioshareinstall.sh");
            File.WriteAllText(shellPath, "pm install -r /data/local/tmp/audioshare.apk && rm -f /data/local/tmp/audioshareinstall.sh");
            await RunCommandAsync(FindAdbPath(), $"-s {device.Serial} push \"{shellPath}\" /data/local/tmp/audioshareinstall.sh");
            await ExecuteRemoteCommandAsync("chmod 777 /data/local/tmp/audioshareinstall.sh && /data/local/tmp/audioshareinstall.sh", androidDeviceInstance);

            result = await ExecuteRemoteCommandAsync("dumpsys package com.picapico.audioshare|grep versionName", androidDeviceInstance);
            if (result.Contains(versionName))
            {
                await ExecuteRemoteCommandAsync("am start -W -n com.picapico.audioshare/.MainActivity", androidDeviceInstance);
                return true;
            }
            WindowState = WindowState.Normal;
            MessageBox.Show(this, currentRD["apkMisMatch"].ToString(), Title);
            return false;
        }

        private async Task<string> ExecuteRemoteCommandAsync(string command, DeviceData device)
        {
            var receiver = new ConsoleOutputReceiver();
            await adbClient.ExecuteRemoteCommandAsync(command, device, receiver, CancellationToken.None);
            return receiver.ToString() ?? string.Empty;
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
                /* Support Mono
                byte[] buffer = e.Buffer;
                int bufferLen = e.BytesRecorded;
                if (Channel != AudioChannel.Stereo)
                {
                    buffer = new byte[bufferLen /= 2];
                    for (int i = Channel == AudioChannel.Left ? 0 : 2, j = 0;
                        j < buffer.Length;
                        i += 4, j += 2)
                    {
                        buffer[j] = e.Buffer[i];
                        buffer[j + 1] = e.Buffer[i + 1];
                    }
                }
                */
                if (!WriteTcp(e.Buffer, e.BytesRecorded))
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        Stop();
                    });
                }
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
            client.ReceiveTimeout = 1000;
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
                    if (addressArr.Length < 2 || !int.TryParse(addressArr.LastOrDefault()?.Trim() ?? string.Empty, out int port))
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
            catch (Exception ex)
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
            ShowWindow();
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
                AudioDevices.Add(new NamePair(device.ID, device.FriendlyName));
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

            AdbLoading = true;
            await RunCommandAsync(adbPath, "start-server");
            await RunCommandAsync(adbPath, "devices");
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
                    AndroidDevices.Add(new NamePair(device.Serial, $"{device.Name} {device.Model}"));
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

        static async Task<int> RunCommandAsync(string fileName, string arguments)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            Process process = new Process
            {
                StartInfo = processInfo,
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
                return await completionSource.Task;
            }
            return 0;
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
