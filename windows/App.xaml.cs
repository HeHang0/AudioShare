using System;
using System.IO.Pipes;
using System.Text;
using System.Windows;

namespace AudioShare
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        System.Threading.Mutex procMutex;
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            procMutex = new System.Threading.Mutex(true, "_AUDIO_SHARE_MUTEX", out var result);
            if (!result)
            {
                try
                {
                    using (var clientStream = new NamedPipeClientStream(".", "_AUDIO_SHARE_PIPE", PipeDirection.InOut, PipeOptions.None))
                    {
                        clientStream.Connect();
                    }
                }
                catch (Exception)
                {
                }
                Current.Shutdown();
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                return;
            }
            MainWindow = new MainWindow();
            MainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            procMutex?.ReleaseMutex();
        }
    }
}
