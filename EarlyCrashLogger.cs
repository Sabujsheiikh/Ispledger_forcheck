using System;
using System.IO;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace ISPLedger
{
    internal static class EarlyCrashLogger
    {
        [ModuleInitializer]
        public static void Initialize()
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
                {
                    try { WriteCrashLog(ev.ExceptionObject as Exception, "AppDomain.CurrentDomain.UnhandledException (module)"); } catch { }
                };

                TaskScheduler.UnobservedTaskException += (s, ev) =>
                {
                    try { WriteCrashLog(ev.Exception, "TaskScheduler.UnobservedTaskException (module)"); ev.SetObserved(); } catch { }
                };
            }
            catch { }
        }

        private static void WriteCrashLog(Exception? ex, string context)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger");
                try { Directory.CreateDirectory(folder); } catch { }
                var path = Path.Combine(folder, "host_crash.log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("--- ISPLedger Early Crash Log ---");
                sb.AppendLine($"Timestamp: {DateTime.UtcNow:o}");
                sb.AppendLine($"Context: {context}");
                if (ex != null) sb.AppendLine(ex.ToString()); else sb.AppendLine("Exception object was null.");
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString());
            }
            catch
            {
                try
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "ISPLedger_host_crash_early.log");
                    var sb2 = new System.Text.StringBuilder();
                    sb2.AppendLine("--- ISPLedger Early Crash Log (Temp fallback) ---");
                    sb2.AppendLine($"Timestamp: {DateTime.UtcNow:o}");
                    sb2.AppendLine($"Context: {context}");
                    if (ex != null) sb2.AppendLine(ex.ToString()); else sb2.AppendLine("Exception object was null.");
                    sb2.AppendLine();
                    File.AppendAllText(tmp, sb2.ToString());
                }
                catch { }
            }
        }
    }
}
