using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
            InitWindowBackdropType();
            _model = new Model();
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
                WindowBackdropType = Wpf.Ui.Appearance.BackgroundType.Tabbed;
            }
            else if (Utils.IsMicaSupported)
            {
                WindowBackdropType = Wpf.Ui.Appearance.BackgroundType.Mica;
            }
            else if (Utils.IsAcrylicSupported)
            {
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                DragHelper.Visibility = Visibility.Visible;
                DragHelper.PreviewMouseLeftButtonDown += DragWindow;
                WindowBackdropType = Wpf.Ui.Appearance.BackgroundType.Acrylic;
            }
            else
            {
                WindowBackdropType = Wpf.Ui.Appearance.BackgroundType.Auto;
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
            Wpf.Ui.Appearance.Watcher.Watch(
                this,
                Wpf.Ui.Appearance.BackgroundType.Mica,
                true,
                true
            );
            WindowState = WindowState.Minimized;
            _model.RefreshAudios();
            await _model.RefreshSpeakers();
            List<Speaker> speakers = new List<Speaker>(_model.Speakers);
            foreach (var speaker in speakers)
            {
                if (speaker.UnConnected && speaker.ChannelSelected.Key != AudioChannel.None)
                {
                    await speaker.Connect();
                    if (!_model.Speakers.Contains(speaker))
                    {
                        speaker.Dispose();
                    }
                }
            }
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
        }

        private void Exit(object sender, RoutedEventArgs e)
        {
            _model.Stop();
            AudioManager.SetDevice(null, 0);
            _model.ResetSpeakerSetting();
            Settings.Load().Save();
            Application.Current.Shutdown();
        }
    }
}
