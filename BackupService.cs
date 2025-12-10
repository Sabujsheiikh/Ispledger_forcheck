using System;
using System.IO;
using System.IO.Compression;

namespace ISPLedger.Services
{
    public static class BackupService
    {
        // ✅ WebView2 Data বা App Data Backup করবে
        public static void RunBackup(string sourceFolder)
        {
            try
            {
                if (!Directory.Exists(sourceFolder))
                    return;

                string backupRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ISPLedger",
                    "Backups"
                );

                Directory.CreateDirectory(backupRoot);

                string todayBackup = Path.Combine(
                    backupRoot,
                    $"backup_{DateTime.Now:yyyyMMdd}.zip"
                );

                // ✅ আজকের backup না থাকলে তৈরি করবে
                if (!File.Exists(todayBackup))
                {
                    ZipFile.CreateFromDirectory(
                        sourceFolder,
                        todayBackup,
                        CompressionLevel.Optimal,
                        false
                    );
                }

                // ✅ 10 দিনের পুরোনো backup auto delete
                foreach (var file in Directory.GetFiles(backupRoot, "backup_*.zip"))
                {
                    try
                    {
                        if (File.GetCreationTime(file).AddDays(10) < DateTime.Now)
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }

                // Also cleanup JSON state snapshots older than 10 days
                foreach (var file in Directory.GetFiles(backupRoot, "kams_state_*.json"))
                {
                    try
                    {
                        if (File.GetCreationTime(file).AddDays(10) < DateTime.Now)
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                // Silent fail — কখনো crash করবে না
            }
        }
    }
}
