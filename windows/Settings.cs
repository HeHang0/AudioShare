using Newtonsoft.Json;
using System;
using System.IO;

namespace AudioShare
{
    public class Settings
    {
        public string AudioId { get; set; } = string.Empty;
        public string AndroidId { get; set; } = string.Empty;
        public string IPAddress { get; set; } = "192.168.3.194:8088";
        public int SampleRate { get; set; } = 48000;
        public bool IsUSB { get; set; } = true;

        private static readonly string savePath;
        static Settings()
        {
            string roming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appName = System.Reflection.Assembly.GetExecutingAssembly()?.GetName()?.Name?.ToString() ?? "AudioShare";
            string dataPath = Path.Combine(roming, appName);
            if (!Directory.Exists(dataPath)) Directory.CreateDirectory(dataPath);
            savePath = Path.Combine(dataPath, "audio.share.config.json");
        }

        public static Settings Read()
        {
            try
            {
                return JsonConvert.DeserializeObject<Settings>(File.ReadAllText(savePath));
            }
            catch (Exception)
            {
                return new Settings();
            }
        }

        public override string ToString()
        {
            try
            {

                return JsonConvert.SerializeObject(this);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public void Save()
        {
            File.WriteAllText(savePath, this.ToString());
        }
    }
}
