using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;

namespace ISPLedger.Services
{
    // JSON data model
    public class UpdateInfo
    {
        public string LatestVersion { get; set; }
        public string MinSupportedVersion { get; set; }
        public string DownloadUrl { get; set; }
        public string Checksum { get; set; }
        public string ReleaseNotes { get; set; }
    }

    // Update checker service
    public static class UpdateService
    {
        private const string TempPrefix = "ISPLedger_updater_";
        // ✅ তোমার GitHub RAW JSON Link (READY)
        private const string VersionUrl =
        "https://raw.githubusercontent.com/Sabujsheiikh/ISPLedger_Updates/main/latest.json";

        // ✅ Current software version read করবে
        public static Version GetCurrentVersion()
        {
            return Assembly.GetExecutingAssembly()
                           .GetName()
                           .Version ?? new Version(1, 0, 0, 0);
        }

        // ✅ GitHub থেকে Latest Version JSON পড়বে
        public static async Task<UpdateInfo?> GetLatestAsync()
        {
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_debug.log");
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                try
                {
                    var json = await client.GetStringAsync(VersionUrl);
                    try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] Fetched remote latest.json from {VersionUrl}\n"); } catch { }
                    return JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] Failed to fetch remote latest.json: {ex}\n"); } catch { }
                }

                // Fallback: check for a local latest.json shipped with the app (baseDir or wwwroot)
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var candidates = new[] {
                        Path.Combine(baseDir, "latest.json"),
                        Path.Combine(baseDir, "wwwroot", "latest.json"),
                        Path.Combine(baseDir, "..", "wwwroot", "latest.json")
                    };

                    foreach (var cand in candidates)
                    {
                        try
                        {
                            if (File.Exists(cand))
                            {
                                var txt = File.ReadAllText(cand);
                                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] Loaded local latest.json from {cand}\n"); } catch { }
                                return JsonSerializer.Deserialize<UpdateInfo>(txt, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            }
                        }
                        catch (Exception ex2)
                        {
                            try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] Failed to read {cand}: {ex2}\n"); } catch { }
                        }
                    }
                }
                catch { }

                return null; // give up quietly
            }
            catch
            {
                return null; // overall failure, ensure app stays up
            }
        }

        // ✅ New version আছে কিনা check করবে
        public static bool IsNewer(string latestVersion)
        {
            if (Version.TryParse(latestVersion, out var latest))
            {
                return latest > GetCurrentVersion();
            }

            return false;
        }

        // Download a file to temp and report progress (percent 0-100)
        public static async Task<string> DownloadToTempAsync(string url, IProgress<int> progress = null, string expectedSha256 = null)
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(10);

                var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return null;

                var total = resp.Content.Headers.ContentLength ?? -1L;
                // Run a cleanup pass before downloading
                try { CleanupOldTempInstallers(7, 1); } catch { }

                var originalName = Path.GetFileName(new Uri(url).LocalPath);
                var tempFile = Path.Combine(Path.GetTempPath(), TempPrefix + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + originalName);
                using var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                using var stream = await resp.Content.ReadAsStreamAsync();

                var buffer = new byte[81920];
                long read = 0;
                int r;
                while ((r = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, r);
                    read += r;
                    if (progress != null && total > 0)
                    {
                        var pct = (int)((read * 100L) / total);
                        progress.Report(pct);
                    }
                }

                progress?.Report(100);

                // If expected SHA256 provided, verify
                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    try
                    {
                        var actual = ComputeFileSha256(tempFile);
                        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(tempFile); } catch { }
                            return null;
                        }
                    }
                    catch
                    {
                        try { File.Delete(tempFile); } catch { }
                        return null;
                    }
                }

                return tempFile;
            }
            catch
            {
                return null;
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using var fs = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(fs);
            var sb = new System.Text.StringBuilder();
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Launch installer with user consent. Returns true if process started.
        public static bool LaunchInstaller(string installerPath)
        {
            try
            {
                if (string.IsNullOrEmpty(installerPath) || !File.Exists(installerPath)) return false;

                var psi = new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    Verb = "runas" // request elevation
                };

                var proc = Process.Start(psi);

                // Attempt to delete the installer shortly after launching.
                // Deletion may fail if the installer is locked; CleanupOldTempInstallers will remove it later.
                try
                {
                    Task.Run(async () => {
                        try {
                            await Task.Delay(2000);
                            try { File.Delete(installerPath); } catch { }
                        } catch { }
                    });
                }
                catch { }

                return proc != null;
            }
            catch
            {
                return false;
            }
        }

        // Cleanup old temporary installers downloaded by the update service.
        // retentionDays: delete files older than this many days. If <=0, only keep the newest 'keepLatest' files.
        public static void CleanupOldTempInstallers(int retentionDays = 7, int keepLatest = 1)
        {
            try
            {
                var tmp = Path.GetTempPath();
                if (!Directory.Exists(tmp)) return;

                var files = Directory.GetFiles(tmp, TempPrefix + "*");
                if (files == null || files.Length == 0) return;

                var list = new List<(string Path, DateTime Modified)>();
                foreach (var f in files)
                {
                    try
                    {
                        var mt = File.GetLastWriteTimeUtc(f);
                        list.Add((f, mt));
                    }
                    catch { }
                }

                list.Sort((a, b) => b.Modified.CompareTo(a.Modified)); // newest first

                // Keep newest 'keepLatest' always
                var toKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < Math.Min(keepLatest, list.Count); i++) toKeep.Add(list[i].Path);

                var threshold = DateTime.UtcNow.AddDays(-Math.Max(0, retentionDays));

                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i].Path;
                    if (toKeep.Contains(p)) continue;

                    // If retentionDays <= 0, delete all except kept ones
                    if (retentionDays <= 0 || list[i].Modified <= threshold)
                    {
                        try { File.Delete(p); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
