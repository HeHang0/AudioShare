using Microsoft.Win32;
using PicaPico;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using NotifyIcon = System.Windows.Forms.NotifyIcon;

namespace AudioShare
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : UiWindow
    {

        private NotifyIcon notifyIcon;
        private ResourceDictionary zhRD;
        private ResourceDictionary enRD;
        private readonly Model _model;

        public MainWindow()
        {
            InitializeComponent();
            _model = new Model();
            InitWindowBackdropType();
            InitLanguage();
            DataContext = _model;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            InitNotify();
            InitNamedPipeServerStream();
        }

        private void InitWindowBackdropType()
        {
            if (Utils.IsMicaTabbedSupported)
            {
                WindowBackdropType = BackgroundType.Tabbed;
            }
            else if (Utils.IsMicaSupported)
            {
                WindowBackdropType = BackgroundType.Mica;
            }
            else if (Utils.IsAcrylicSupported && _model.Acrylic)
            {
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                DragHelper.Visibility = Visibility.Visible;
                WindowBackdropType = BackgroundType.Acrylic;
                DragHelper.PreviewMouseLeftButtonDown += DragWindow;
                Activated += WindowActivated;
                Deactivated += WindowDeactivated;
            }
            else
            {
                WindowBackdropType = BackgroundType.Auto;
            }
            ThemeListener.ThemeChanged += ApplyTheme;
            ApplyTheme(ThemeListener.IsDarkMode);
        }

        private void WindowActivated(object sender, EventArgs e)
        {
            WinBackground.Background = ThemeListener.IsDarkMode ? _blackBackgroundA : _whiteBackgroundA;
        }

        private void WindowDeactivated(object sender, EventArgs e)
        {
            WinBackground.Background = ThemeListener.IsDarkMode ? _blackBackground : _whiteBackground;
        }

        private readonly Brush _blackBackgroundA = new SolidColorBrush(Color.FromArgb(0xA0, 0x1F, 0x1F, 0x1F));
        private readonly Brush _whiteBackgroundA = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        private readonly Brush _blackBackground = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        private readonly Brush _whiteBackground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private void ApplyTheme(bool isDark)
        {
            Theme.Apply(
              isDark ? ThemeType.Dark : ThemeType.Light,
              WindowBackdropType,
              true,
              false
            );
            if (WindowBackdropType == BackgroundType.Acrylic)
            {
                if (IsActive) WindowActivated(null, null);
                else WindowDeactivated(null, null);
            }
            else
            {
                WinBackground.Background = Brushes.Transparent;
            }
        }

        private void DragWindow(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (WindowStyle == WindowStyle.None)
            {
                DragMove();
            }
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
            catch (Exception)
            {
            }
            InitNamedPipeServerStream();
        }

        private void InitLanguage()
        {
            zhRD = Application.Current.Resources.MergedDictionaries.First(m => m.Source?.OriginalString.Contains("zh-cn") ?? false);
            enRD = Application.Current.Resources.MergedDictionaries.First(m => m.Source?.OriginalString.Contains("en-us") ?? false);
            bool isChinese = System.Globalization.CultureInfo.InstalledUICulture.Name.ToLower().Contains("zh");
            Languages.Language.SetLanguage(isChinese ? zhRD : enRD);
            Application.Current.Resources.MergedDictionaries.Remove(isChinese ? enRD : zhRD);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Locale)
            {
                SetLanguage(System.Globalization.CultureInfo.InstalledUICulture.Name.ToLower().Contains("zh"));
                _model.UpdateTitle();
            }
        }

        private void SetLanguage(bool isChinese)
        {
            var currentRD = isChinese ? zhRD : enRD;
            Languages.Language.SetLanguage(currentRD);
            Application.Current.Resources.MergedDictionaries.Add(currentRD);
            Application.Current.Resources.MergedDictionaries.Remove(isChinese ? enRD : zhRD);
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
#if DEBUG
#else
            WindowState = WindowState.Minimized;
#endif
            _model.RefreshAudios();
            await _model.RefreshSpeakers();
#if DEBUG
#else
            List<Task> tasks = new List<Task>();
            foreach (var speaker in _model.Speakers)
            {
                if (speaker.UnConnected && speaker.ChannelSelected.Key != AudioChannel.None)
                {
                    tasks.Add(speaker.Connect());
                }
            }
            await Task.WhenAll(tasks);
            if (_model.Speakers.Any(m => m.Connected))
            {
                Hide();
            }
            else
            {
                ShowWindow();
            }
            if (_model.IsUSB && _model.Speakers.Count == 0)
            {
                _model.IsUSB = false;
            }
#endif
        }

        private void Exit(object sender, RoutedEventArgs e)
        {
            notifyIcon.Dispose();
            _model.Stop();
            AudioManager.SetDevice(null, 0);
            _model.ResetSpeakerSetting();
            Settings.Load().Save();
            Application.Current.Shutdown();
        }
    }
}
