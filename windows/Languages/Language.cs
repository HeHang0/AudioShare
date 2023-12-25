using System.Windows;

namespace AudioShare.Languages
{
    public class Language
    {
        private static ResourceDictionary _language = null;
        private static readonly object _languageLock = new object();

        public static void SetLanguage(ResourceDictionary language)
        {
            lock (_languageLock)
            {
                _language = language;
            }
        }

        public static string GetLanguageText(string key)
        {
            lock (_languageLock)
            {
                return _language?[key]?.ToString() ?? string.Empty;
            }
        }
    }
}
