using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ISPLedger
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            RegisterGlobalExceptionHandlers();
        }

        private void RegisterGlobalExceptionHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try
                {
                    var ex = ev.ExceptionObject as Exception;
                    WriteCrashLog(ex, "AppDomain.CurrentDomain.UnhandledException");
                }
                catch { }
            };

            TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                try
                {
                    WriteCrashLog(ev.Exception, "TaskScheduler.UnobservedTaskException");
                    ev.SetObserved();
                }
                catch { }
            };

            DispatcherUnhandledException += (s, ev) =>
            {
                try
                {
                    WriteCrashLog(ev.Exception, "Application.DispatcherUnhandledException");
                }
                catch { }
                // let application continue to perform its normal crash handling after logging
            };
        }

        private static void WriteCrashLog(Exception? ex, string context)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger");
                try { Directory.CreateDirectory(folder); } catch { }
                var path = Path.Combine(folder, "host_crash.log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("--- ISPLedger Crash Log ---");
                sb.AppendLine($"Timestamp: {DateTime.UtcNow:o}");
                sb.AppendLine($"Context: {context}");
                if (ex != null)
                {
                    sb.AppendLine("Exception:");
                    sb.AppendLine(ex.ToString());
                }
                else
                {
                    sb.AppendLine("Exception object was null.");
                }
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString());
            }
            catch
            {
                // Fallback: try writing to Temp if LocalAppData is blocked
                try
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "ISPLedger_host_crash.log");
                    var sb2 = new System.Text.StringBuilder();
                    sb2.AppendLine("--- ISPLedger Crash Log (Temp fallback) ---");
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
