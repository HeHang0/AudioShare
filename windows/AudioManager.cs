using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AudioShare
{
    public class AudioManager
    {
        public static event EventHandler<WaveInEventArgs> StereoAvailable;
        public static event EventHandler<WaveInEventArgs> LeftAvailable;
        public static event EventHandler<WaveInEventArgs> RightAvailable;
        public static event EventHandler Stoped;
        public static event EventHandler<int> OnVolumeNotification;

        private static WasapiLoopbackCapture _capture;
        private static MMDevice _device;
        private static Dispatcher _dispatcher;
        private static int _sampleRate;

        static AudioManager()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        private AudioManager() { }

        public static int SampleRate => _sampleRate;

        public static void SetDevice(MMDevice device, int sampleRate)
        {
            if(_capture != null)
            {
                _capture.DataAvailable -= SendAudioData;
            }
            _capture?.StopRecording();
            _device?.Dispose();
            Stoped?.Invoke(null, null);
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeChange;
            }
            _device = device;
            _sampleRate = sampleRate;
            if(_device == null)
            {
                _capture = null;
                return;
            }
            _capture = new WasapiLoopbackCapture(device);
            _capture.WaveFormat = new WaveFormat(sampleRate, 16, 2);
            _capture.DataAvailable += SendAudioData;
            if (StereoAvailable != null || LeftAvailable != null || RightAvailable != null)
            {
                StartCapture();
            }
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeChange;
        }

        public static void StartCapture()
        {
            _dispatcher.Invoke(() =>
            {
                if(_capture == null) return;
                try
                {
                    if(_capture.CaptureState == CaptureState.Stopped)
                    {
                        _capture.StartRecording();
                    }
                }
                catch (Exception)
                {

                }
            });
        }

        public static void StopCapture()
        {
            try
            {
                _capture?.StopRecording();
            }
            catch (Exception)
            {

            }
        }

        private static CancellationTokenSource SetRemoteVolumeCancel = null;
        private static async void OnVolumeChange(AudioVolumeNotificationData data)
        {
            SetRemoteVolumeCancel?.Cancel();
            SetRemoteVolumeCancel = new CancellationTokenSource();
            CancellationToken token = SetRemoteVolumeCancel.Token;
            await Task.Delay(200);
            if (token.IsCancellationRequested) return;
            OnVolumeNotification?.Invoke(null, data.Muted ? 0 : (int)(data.MasterVolume * 100));
        }

        private static void SendAudioData(object sender, WaveInEventArgs e)
        {
            if(e.BytesRecorded <= 0) return;
            StereoAvailable?.Invoke(null, e);
            bool canLeft = LeftAvailable != null;
            bool canRight = RightAvailable != null;
            if (canLeft || canRight)
            {
                byte[] bufferLeft = new byte[e.BytesRecorded / 2];
                byte[] bufferRight = new byte[e.BytesRecorded / 2];
                for (int i = 0, j = 0;
                    j < e.BytesRecorded / 2;
                    i += 4, j += 2)
                {
                    if(canLeft)
                    {
                        bufferLeft[j] = e.Buffer[i];
                        bufferLeft[j + 1] = e.Buffer[i + 1];
                    }
                    if (canRight)
                    {
                        bufferRight[j] = e.Buffer[i + 2];
                        bufferRight[j + 1] = e.Buffer[i + 3];
                    }
                }
                _dispatcher.InvokeAsync(() =>
                {
                    LeftAvailable?.Invoke(null, new WaveInEventArgs(bufferLeft, bufferLeft.Length));
                    RightAvailable?.Invoke(null, new WaveInEventArgs(bufferRight, bufferRight.Length));
                });
            }
        }
    }
}
