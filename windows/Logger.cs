using System;
using System.Diagnostics;
using System.IO;

namespace AudioShare
{
    public class Logger
    {
        public enum LogLevel
        {
            Error,
            Warning,
            Info,
            Debug
        }
        private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        private static void Log(LogLevel logLevel = LogLevel.Info, params object[] message)
        {
#if DEBUG
            var stacktrace = new StackTrace(skipFrames: 2, fNeedFileInfo: true);
            var frame = stacktrace.GetFrame(0);
            string stack = "";
            if (frame != null)
            {
                stack = $"{Path.GetFileName(frame.GetFileName())}:{frame.GetFileLineNumber()}:{frame.GetMethod()?.Name}";
            }
            Trace.WriteLine($"[{Now}]{stack} {string.Join(" ", message)}", logLevel.ToString());
#endif
        }

        public static void Info(params object[] message)
        {
            Log(LogLevel.Info, message);
        }

        public static void Warning(params object[] message)
        {
            Log(LogLevel.Warning, message);
        }

        public static void Error(params object[] message)
        {
            Log(LogLevel.Error, message);
        }

        public static void Debug(params object[] message)
        {
            //Log(LogLevel.Debug, message);
        }
    }
}
