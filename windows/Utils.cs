using Microsoft.VisualBasic;
using SharpAdbClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioShare
{
    public static class Utils
    {
        private static readonly string _versionName = string.Empty;
        public static string VersionName => _versionName;

        static Utils()
        {
            var version = Application.ResourceAssembly.GetName()?.Version;
            if (version == null) return;
            _versionName = $"{version.Major}.{version.Minor}.{version.Build}";
        }

        public static Task<bool> PortIsOpen(int port, int timeout = 1000)
        {
            return PortIsOpen("127.0.0.1", port, timeout);
        }

        public static async Task<bool> PortIsOpen(string host, int port, int timeout = 1000)
        {
            try
            {
                using (TcpClient tcpClient = new TcpClient())
                {
                    tcpClient.ReceiveTimeout = timeout;
                    await tcpClient.ConnectAsync(host, port);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static async Task<int> GetFreePort(int port = 8088)
        {
            for (; port < 65535; port++)
            {
                if (!await PortIsOpen(port, 50)) break;
            }
            return port;
        }

        public static async Task<int> RunCommandAsync(string fileName, string arguments)
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

        public static async Task<string> RunAdbShellCommandAsync(AdbClient adbClient, string command, DeviceData device)
        {
            var receiver = new ConsoleOutputReceiver();
            await adbClient.ExecuteRemoteCommandAsync(command, device, receiver, CancellationToken.None);
            return receiver.ToString() ?? string.Empty;
        }

        public static string GetAdbPath(this IAdbClient client)
        {
            return FindAdbPath();
        }

        public static void RemoveRemoteForward(this IAdbClient client, DeviceData device, string remote)
        {
            if (device == null) return;
            var forwards = client.ListForward(device);
            foreach (var forward in forwards)
            {
                if (forward.SerialNumber == device.Serial && forward.Remote == remote)
                {
                    client.RemoveForward(device, forward.LocalSpec.Port);
                }
            }
        }

        public static DeviceData GetDevice(this IAdbClient client, string serial)
        {
            try
            {
                return client.GetDevices()?.FirstOrDefault(m => m.Serial == serial);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static async Task PushAsync(this IAdbClient client, DeviceData device, string filePath, string remotePath)
        {
            string adbPath = FindAdbPath();
            if (!string.IsNullOrWhiteSpace(adbPath))
            {
                await RunCommandAsync(adbPath, $"-s {device.Serial} push \"{filePath}\" {remotePath}");
            }
        }

        public static string FindAdbPath()
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

        public static async Task EnsureAdb(string adbPath)
        {
            await RunCommandAsync(adbPath, "start-server");
            await RunCommandAsync(adbPath, "devices");
        }

        public static ImageSource AppIcon
        {
            get
            {
                Icon appIcon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (appIcon != null)
                {
                    return Imaging.CreateBitmapSourceFromHIcon(
                        appIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }

                return null;
            }
        }

        public static bool IsAcrylicSupported => IsWindowsNT && OSVersion >= new Version(10, 0) && OSVersion < new Version(10, 0, 22523);

        public static bool IsMicaSupported => IsWindowsNT && OSVersion >= new Version(10, 0, 21996);

        public static bool IsMicaTabbedSupported => IsWindowsNT && OSVersion >= new Version(10, 0, 22523);

        public static bool IsWindowsNT => Environment.OSVersion.Platform == PlatformID.Win32NT;

        private static readonly Version _osVersion = GetOSVersion();

        public static Version OSVersion => _osVersion;

        private static Version GetOSVersion()
        {
            var osv = new RTL_OSVERSIONINFOEX();
            osv.dwOSVersionInfoSize = (uint)Marshal.SizeOf(osv);
            _ = RtlGetVersion(out osv);
            return new Version((int)osv.dwMajorVersion, (int)osv.dwMinorVersion, (int)osv.dwBuildNumber);
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(out RTL_OSVERSIONINFOEX lpVersionInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct RTL_OSVERSIONINFOEX
        {
            internal uint dwOSVersionInfoSize;
            internal uint dwMajorVersion;
            internal uint dwMinorVersion;
            internal uint dwBuildNumber;
            internal uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string szCSDVersion;
        }
    }
}
