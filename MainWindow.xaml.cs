using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using System;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.IO.Compression;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using ISPLedger.Services;

namespace ISPLedger
{
    public partial class MainWindow : Window
    {
        // Host backup scheduler state
        private class HostBackupSettings
        {
            public int ScheduleDays { get; set; } = 1; // 1,3,7
            public bool AutoUploadToDrive { get; set; } = false;
            public DateTime LastRunUtc { get; set; } = DateTime.MinValue;
            // Update cleanup settings
            public int UpdateCleanupDays { get; set; } = 7; // delete installers older than this
            public int UpdateKeepLatest { get; set; } = 1; // keep this many newest installers
        }

        private HostBackupSettings _hostSettings;
        private readonly string _hostSettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_settings.json");
        private System.Timers.Timer _backupTimer;
        private string _webRootPath = null;
        public MainWindow()
        {
            InitializeComponent();
            InitializeWebView();

            // ✅ App start হলেই Auto-Update check
            _ = CheckForUpdateAsync();
        }

        private async void InitializeWebView()
        {
            try
            {
                // Robust WebView2 initialization:
                // - Attempt to create an environment and initialize WebView2 with retries
                // - If the environment is busy (HRESULT 0x800700AA) try with an alternate userDataFolder
                bool initialized = false;
                int maxAttempts = 5;
                int attempt = 0;
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var baseUserData = Path.Combine(localAppData, "ISPLedger", "WebView2");

                while (!initialized && attempt < maxAttempts)
                {
                    try
                    {
                        string userDataFolder = baseUserData;
                        if (attempt > 0)
                        {
                            // If previous attempt failed due to resource contention, use a unique folder
                            userDataFolder = baseUserData + $"_{Process.GetCurrentProcess().Id}_{attempt}";
                        }
                        try { Directory.CreateDirectory(userDataFolder); } catch { }

                        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                        await webView.EnsureCoreWebView2Async(env);
                        initialized = true;
                    }
                    catch (COMException cex) when (cex.HResult == unchecked((int)0x800700AA))
                    {
                        // ERROR_BUSY (resource in use) — retry with backoff and alternate userDataFolder
                        attempt++;
                        try
                        {
                            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_debug.log");
                            Directory.CreateDirectory(Path.GetDirectoryName(log));
                            File.AppendAllText(log, $"[{DateTime.UtcNow:o}] WebView2 init busy (attempt {attempt}). HResult={cex.HResult}\n");
                        }
                        catch { }
                        await Task.Delay(200 * attempt);
                    }
                    catch (Exception ex)
                    {
                        // Other transient failures — retry a few times
                        attempt++;
                        try
                        {
                            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_debug.log");
                            Directory.CreateDirectory(Path.GetDirectoryName(log));
                            File.AppendAllText(log, $"[{DateTime.UtcNow:o}] WebView2 init error (attempt {attempt}): {ex.Message}\n");
                        }
                        catch { }
                        await Task.Delay(200 * attempt);
                    }
                }

                if (!initialized)
                {
                    throw new Exception("Unable to initialize WebView2 after multiple attempts (resource busy or initialization failure). Check that no other instance is locking WebView2 user data folder.");
                }

                // Try to locate wwwroot folder robustly. If not found, create a small fallback page so WebView2 can still initialize.
                string rootPath = FindWebRoot();
                if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
                {
                    // Create a minimal fallback folder with an error page so WebView won't fail to navigate.
                    var tmp = Path.Combine(Path.GetTempPath(), "ISPLedger_fallback_wwwroot");
                    try { if (!Directory.Exists(tmp)) Directory.CreateDirectory(tmp); } catch { }
                    var indexFile = Path.Combine(tmp, "index.html");
                    try
                    {
                        File.WriteAllText(indexFile, "<html><body style=\"background:#111;color:#fff;font-family:sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;\"><div style=\"max-width:720px;text-align:center;\"><h1>Application Resources Missing</h1><p>The application could not find its bundled web assets (wwwroot). This typically means the app was run from the build output without copying the web assets. Please ensure the installer or build process includes the 'wwwroot' folder.</p></div></body></html>");
                    }
                    catch { }
                    rootPath = tmp;
                }

                _webRootPath = rootPath;

                try
                {
                    webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "app.local",
                        rootPath,
                        CoreWebView2HostResourceAccessKind.Allow
                    );
                }
                catch
                {
                    // If mapping fails, fallback to directly navigating to the file path
                    try
                    {
                        var index = Path.Combine(rootPath, "index.html");
                        if (File.Exists(index)) webView.CoreWebView2.Navigate(index);
                    }
                    catch { }
                }

                // Load local web app (use virtual host URL which maps to rootPath)
                try { webView.CoreWebView2.Navigate("http://app.local/index.html"); } catch { }
                try { BackupService.RunBackup(rootPath); } catch { }


                // ✅ Browser UI disable
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                // Listen for messages from the web app
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                // After navigation completed, try to seed localStorage from latest backup
                webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                // Open DevTools on F12 or Ctrl+Shift+I for debugging host/web issues
                // Use a window-level key handler (PreviewKeyDown) instead of WebView2 accelerator events
                this.PreviewKeyDown += (s, e) =>
                {
                    try
                    {
                        // F12 to open DevTools
                        if (e.Key == System.Windows.Input.Key.F12)
                        {
                            try { webView.CoreWebView2.OpenDevToolsWindow(); } catch { }
                            e.Handled = true;
                            return;
                        }

                        // Ctrl+Shift+I to open DevTools
                        var mods = System.Windows.Input.Keyboard.Modifiers;
                        if ((mods & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control &&
                            (mods & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift &&
                            e.Key == System.Windows.Input.Key.I)
                        {
                            try { webView.CoreWebView2.OpenDevToolsWindow(); } catch { }
                            e.Handled = true;
                        }
                    }
                    catch { }
                };
                // Notify the web UI that the host bridge is ready
                try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "host_ready", message = "host_initialized" })); } catch { }
                // Load host settings and start backup timer
                LoadHostSettings();
                StartBackupTimer();
                // Cleanup any old temp installers downloaded by updater using configured retention
                try { UpdateService.CleanupOldTempInstallers(_hostSettings?.UpdateCleanupDays ?? 7, _hostSettings?.UpdateKeepLatest ?? 1); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView Init Error: " + ex.Message);
            }
        }

        // Try to locate the embedded web app folder (wwwroot) by searching up the directory tree
        private string FindWebRoot()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                // Try common candidates
                var candidates = new List<string>();
                candidates.Add(Path.Combine(baseDir, "wwwroot"));
                candidates.Add(Path.Combine(baseDir, "..", "wwwroot"));
                candidates.Add(Path.Combine(baseDir, "..", "..", "wwwroot"));
                candidates.Add(Path.Combine(baseDir, "..", "..", "..", "wwwroot"));
                candidates.Add(Path.Combine(Environment.CurrentDirectory, "wwwroot"));

                foreach (var c in candidates)
                {
                    try { var full = Path.GetFullPath(c); if (Directory.Exists(full)) return full; } catch { }
                }

                // As a last resort walk up the tree from baseDir looking for wwwroot
                var dir = baseDir;
                for (int i = 0; i < 6; i++)
                {
                    try
                    {
                        var cand = Path.Combine(dir, "wwwroot");
                        if (Directory.Exists(cand)) return Path.GetFullPath(cand);
                        dir = Path.GetDirectoryName(dir);
                        if (string.IsNullOrEmpty(dir)) break;
                    }
                    catch { break; }
                }

                return null;
            }
            catch { return null; }
        }

        private async void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                // Only run when navigation succeeded
                if (e.IsSuccess)
                {
                    var backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "Backups");
                    if (!Directory.Exists(backupRoot)) return;

                    // Find most recent kams_state_*.json file
                    var files = Directory.GetFiles(backupRoot, "kams_state_*.json");
                    if (files.Length == 0) return;
                    Array.Sort(files, (a, b) => File.GetCreationTimeUtc(b).CompareTo(File.GetCreationTimeUtc(a)));
                    var latest = files[0];
                    var json = await File.ReadAllTextAsync(latest);

                    // Check if localStorage already has the DB key
                    var storageKey = "kams_enterprise_db_v1"; // must match client STORAGE_KEY
                    var checkScript = $"(function(){{ try{{ return localStorage.getItem('{storageKey}') === null; }}catch(e){{return false;}} }})();";
                    var result = await webView.CoreWebView2.ExecuteScriptAsync(checkScript);
                    // result is a JSON boolean like "true" or "false"
                    if (result != null && result.Trim().ToLower().Contains("true"))
                    {
                        // Use JsonSerializer to produce a safe JS string literal for the JSON payload
                        var jsonLiteral = System.Text.Json.JsonSerializer.Serialize(json);
                        var setScript = $"(function(){{ try{{ localStorage.setItem('{storageKey}', {jsonLiteral}); return true; }}catch(e){{ return false; }} }})();";
                        await webView.CoreWebView2.ExecuteScriptAsync(setScript);
                        // Notify web app that restore was performed
                        webView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new { type = "restored_from_backup", path = latest }));
                    }
                }
            }
            catch { }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Process> _runningProcesses = new System.Collections.Concurrent.ConcurrentDictionary<string, Process>();

        private async void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = e.TryGetWebMessageAsString();
                // Log raw incoming web messages for debugging host <-> web bridge
                try
                {
                    var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_debug.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(log));
                    File.AppendAllText(log, $"[{DateTime.UtcNow:o}] WebMsgReceived: {msg}\n");
                }
                catch { }
                if (string.IsNullOrWhiteSpace(msg)) return;

                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                if (!root.TryGetProperty("action", out var actionEl)) return;
                var action = actionEl.GetString();

                if (action == "backup_local")
                {
                    // payload is the app state JSON string
                    if (!root.TryGetProperty("payload", out var payloadEl)) return;
                    var json = payloadEl.GetString() ?? payloadEl.ToString();

                    try
                    {
                        var backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "Backups");
                        Directory.CreateDirectory(backupRoot);
                        var filePath = Path.Combine(backupRoot, $"kams_state_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                        await File.WriteAllTextAsync(filePath, json);

                        // Also create a zip copy for rotation service compatibility
                        try
                        {
                            var zipPath = Path.Combine(backupRoot, $"backup_{DateTime.Now:yyyyMMdd}.zip");
                            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Update))
                            {
                                var entryName = Path.GetFileName(filePath);
                                // Create a new entry and copy file bytes to it (CreateEntryFromFile may not be available depending on references)
                                var entry = zip.CreateEntry(entryName);
                                using (var es = entry.Open())
                                using (var fs = File.OpenRead(filePath))
                                {
                                    fs.CopyTo(es);
                                }
                            }
                        }
                        catch { /* non-fatal */ }

                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "backup_success", path = filePath }));
                    }
                    catch (Exception ex)
                    {
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "backup_failed", message = ex.Message }));
                    }
                }

                else if (action == "run_cmd")
                {
                    if (!root.TryGetProperty("payload", out var payloadEl)) return;
                    var cmd = payloadEl.GetProperty("cmd").GetString() ?? string.Empty;
                    var id = payloadEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();

                    // Start process and stream output
                    var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };

                    var proc = new Process() { StartInfo = psi, EnableRaisingEvents = true };
                    if (!_runningProcesses.TryAdd(id, proc))
                    {
                        // id collision, generate new
                        id = Guid.NewGuid().ToString();
                        _runningProcesses.TryAdd(id, proc);
                    }

                    proc.Start();
                        // Notify host->web that process started
                        try
                        {
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "cmd_started", id = id, pid = proc.Id }));
                        }
                        catch { }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (!proc.HasExited)
                            {
                                var line = await proc.StandardOutput.ReadLineAsync();
                                if (line == null) break;
                                var ts = DateTime.UtcNow.ToString("o");
                                try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "cmd_output", id = id, pid = proc.Id, line = line, timestamp = ts, isError = false })); } catch { }
                                try
                                {
                                    var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_debug.log");
                                    Directory.CreateDirectory(Path.GetDirectoryName(log));
                                    File.AppendAllText(log, $"[{ts}] [OUT] id={id} pid={proc.Id} {line}\n");
                                }
                                catch { }
                            }

                            // Read remaining error output
                            while (!proc.StandardError.EndOfStream)
                            {
                                var el = await proc.StandardError.ReadLineAsync();
                                if (el == null) break;
                                var ts2 = DateTime.UtcNow.ToString("o");
                                try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "cmd_output", id = id, pid = proc.Id, line = el, timestamp = ts2, isError = true })); } catch { }
                                try
                                {
                                    var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_debug.log");
                                    Directory.CreateDirectory(Path.GetDirectoryName(log));
                                    File.AppendAllText(log, $"[{ts2}] [ERR] id={id} pid={proc.Id} {el}\n");
                                }
                                catch { }
                            }

                            // Include exit code if available
                            int? exitCode = null;
                            try { exitCode = proc.HasExited ? proc.ExitCode : (int?)null; } catch { }
                            try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "cmd_done", id = id, pid = proc.Id, exitCode = exitCode })); } catch { }
                        }
                        catch (Exception ex)
                        {
                            try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "cmd_error", id = id, message = ex.Message })); } catch { }
                            try
                            {
                                var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_debug.log");
                                Directory.CreateDirectory(Path.GetDirectoryName(log));
                                File.AppendAllText(log, $"[{DateTime.Now}] run_cmd error (id={id}): {ex}\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            _runningProcesses.TryRemove(id, out var _);
                            try { proc.Kill(); } catch { }
                            proc.Dispose();
                        }
                    });
                }

                else if (action == "kill_cmd")
                {
                    if (!root.TryGetProperty("payload", out var payloadEl)) return;
                    var id = payloadEl.GetProperty("id").GetString();
                    if (id != null && _runningProcesses.TryRemove(id, out var proc))
                    {
                        try { if (!proc.HasExited) proc.Kill(); } catch { }
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "cmd_killed", id = id }));
                    }
                }
                else if (action == "google_auth")
                {
                    // Trigger Google OAuth desktop flow. If browser fails to open, provide the URL to the web UI so user can copy it.
                    try
                    {
                        try { var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_debug.log"); Directory.CreateDirectory(Path.GetDirectoryName(log)); File.AppendAllText(log, $"[{DateTime.UtcNow:o}] -> Received google_auth message from web UI\n"); } catch { }
                        var authTask = GoogleDriveService.AuthenticateAsync((u) => {
                            try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "google_auth_started", url = u })); } catch { }
                        });
                        // Wait with timeout so we can show a helpful message if nothing opens
                        var completed = await Task.WhenAny(authTask, Task.Delay(TimeSpan.FromSeconds(30)));
                        if (completed != authTask)
                        {
                            // Authentication still running — inform web UI that host attempted to open browser
                            try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "google_auth_result", success = false, message = "auth_started", url = GoogleDriveService.LastAuthUrl })); } catch { }
                            // wait for final result but don't block indefinitely
                            var ok = await Task.WhenAny(authTask, Task.Delay(TimeSpan.FromMinutes(5))) == authTask ? await authTask : false;
                            try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "google_auth_result", success = ok, url = GoogleDriveService.LastAuthUrl })); } catch { }
                        }
                        else
                        {
                            var ok = await authTask;
                            try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "google_auth_result", success = ok, url = GoogleDriveService.LastAuthUrl })); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "google_auth_result", success = false, message = ex.Message })); } catch { }
                    }
                }
                else if (action == "check_update")
                {
                    try
                    {
                        // inform UI that check started
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update_checking" }));

                        var info = await UpdateService.GetLatestAsync();
                        if (info != null && UpdateService.IsNewer(info.LatestVersion))
                        {
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update_available", version = info.LatestVersion, downloadUrl = info.DownloadUrl }));
                        }
                        else
                        {
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "up_to_date" }));
                        }
                    }
                    catch (Exception ex)
                    {
                        try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update_error", message = ex.Message })); } catch { }
                    }
                }
                else if (action == "start_update")
                {
                    try
                    {
                        // Determine download URL from payload or fallback to latest.json
                        string? downloadUrl = null;
                        if (root.TryGetProperty("payload", out var payloadEl) && payloadEl.ValueKind == JsonValueKind.Object && payloadEl.TryGetProperty("downloadUrl", out var du))
                        {
                            downloadUrl = du.GetString();
                        }

                        var info = await UpdateService.GetLatestAsync();
                        if (string.IsNullOrEmpty(downloadUrl)) downloadUrl = info?.DownloadUrl;
                        if (string.IsNullOrEmpty(downloadUrl))
                        {
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update_error", message = "No download URL available" }));
                        }
                        else
                        {
                            var progress = new Progress<int>(pct => {
                                try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update_progress", percent = pct })); } catch { }
                            });

                            var expectedSha = info?.Checksum;
                            var tmp = await UpdateService.DownloadToTempAsync(downloadUrl!, progress, expectedSha!);
                            if (string.IsNullOrEmpty(tmp))
                            {
                                webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update_error", message = "Download failed or checksum mismatch" }));
                            }
                            else
                            {
                                webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update_downloaded", path = tmp }));
                                // Give UI a moment to update then launch installer and exit
                                try
                                {
                                    await Task.Delay(500);
                                    var launched = UpdateService.LaunchInstaller(tmp);
                                    if (launched)
                                    {
                                        // close the app to allow installer to run
                                        Application.Current.Dispatcher.Invoke(() => { Application.Current.Shutdown(); });
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try { webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "update_error", message = ex.Message })); } catch { }
                    }
                }
                else if (action == "drive_upload")
                {
                    // Optionally accept a 'path' payload, otherwise upload latest on-disk state
                    string path = null;
                    if (root.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.Object && pl.TryGetProperty("path", out var pathEl))
                        path = pathEl.GetString();

                    try
                    {
                        if (string.IsNullOrEmpty(path))
                        {
                            var backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "Backups");
                            if (Directory.Exists(backupRoot))
                            {
                                var files = Directory.GetFiles(backupRoot, "kams_state_*.json");
                                Array.Sort(files, (a, b) => File.GetCreationTimeUtc(b).CompareTo(File.GetCreationTimeUtc(a)));
                                if (files.Length > 0) path = files[0];
                            }
                        }

                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            var uploaded = await GoogleDriveService.UploadFileAsync(path);
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_upload_result", success = uploaded }));
                        }
                        else
                        {
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_upload_result", success = false, message = "No file to upload" }));
                        }
                    }
                    catch (Exception ex)
                    {
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_upload_result", success = false, message = ex.Message }));
                    }
                }
                else if (action == "drive_list")
                {
                    try
                    {
                        var files = await GoogleDriveService.ListFilesAsync();
                        var shortList = new List<object>();
                        if (files != null)
                        {
                            foreach (var f in files)
                            {
                                shortList.Add(new { id = f.Id, name = f.Name, modified = f.ModifiedTime.ToString("o") });
                            }
                        }
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_list_result", files = shortList }));
                    }
                    catch (Exception ex)
                    {
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_list_result", files = new object[0], error = ex.Message }));
                    }
                }
                else if (action == "drive_download")
                {
                    try
                    {
                        if (!root.TryGetProperty("payload", out var payloadEl))
                        {
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_download_result", success = false, message = "No payload" }));
                        }
                        else
                        {
                            var fileId = payloadEl.GetProperty("id").GetString();
                            var path = await GoogleDriveService.DownloadFileAsync(fileId);
                            if (string.IsNullOrEmpty(path))
                            {
                                webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_download_result", success = false, message = "Download failed" }));
                            }
                            else
                            {
                                // Read content and inject into localStorage, replacing all state
                                var json = await File.ReadAllTextAsync(path);
                                var storageKey = "kams_enterprise_db_v1";
                                var jsonLiteral = JsonSerializer.Serialize(json);
                                var setScript = $"(function(){{ try{{ localStorage.setItem('{storageKey}', {jsonLiteral}); return true; }}catch(e){{ return false; }} }})();";
                                await webView.CoreWebView2.ExecuteScriptAsync(setScript);
                                // Auto-reload so web app immediately picks up restored state
                                await webView.CoreWebView2.ExecuteScriptAsync("location.reload();");
                                webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_download_result", success = true, path = path }));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_download_result", success = false, message = ex.Message }));
                    }
                }
                else if (action == "drive_delete")
                {
                    try
                    {
                        if (!root.TryGetProperty("payload", out var payloadEl))
                        {
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_delete_result", success = false, message = "No payload" }));
                        }
                        else
                        {
                            var fileId = payloadEl.GetProperty("id").GetString();
                            var ok = await GoogleDriveService.DeleteFileAsync(fileId);
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_delete_result", success = ok }));
                        }
                    }
                    catch (Exception ex)
                    {
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "drive_delete_result", success = false, message = ex.Message }));
                    }
                }
                else if (action == "set_backup_schedule")
                {
                    try
                    {
                        if (!root.TryGetProperty("payload", out var pl))
                        {
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "set_backup_schedule_result", success = false, message = "No payload" }));
                        }
                        else
                        {
                            var days = pl.GetProperty("days").GetInt32();
                            var autoUpload = pl.GetProperty("autoUpload").GetBoolean();
                            _hostSettings.ScheduleDays = days;
                            _hostSettings.AutoUploadToDrive = autoUpload;
                            _hostSettings.LastRunUtc = _hostSettings.LastRunUtc; // unchanged
                            SaveHostSettings();
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "set_backup_schedule_result", success = true }));
                        }
                    }
                    catch (Exception ex)
                    {
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "set_backup_schedule_result", success = false, message = ex.Message }));
                    }
                }
                else if (action == "get_backup_schedule")
                {
                    try
                    {
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "get_backup_schedule_result", days = _hostSettings?.ScheduleDays ?? 1, autoUpload = _hostSettings?.AutoUploadToDrive ?? false, lastRunUtc = _hostSettings?.LastRunUtc.ToString("o") }));
                    }
                    catch { }
                }
                else if (action == "set_update_cleanup")
                {
                    try
                    {
                        if (!root.TryGetProperty("payload", out var pl))
                        {
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "set_update_cleanup_result", success = false, message = "No payload" }));
                        }
                        else
                        {
                            var days = pl.GetProperty("days").GetInt32();
                            var keep = pl.GetProperty("keepLatest").GetInt32();
                            _hostSettings.UpdateCleanupDays = days;
                            _hostSettings.UpdateKeepLatest = keep;
                            SaveHostSettings();
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "set_update_cleanup_result", success = true }));
                        }
                    }
                    catch (Exception ex)
                    {
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "set_update_cleanup_result", success = false, message = ex.Message }));
                    }
                }
                else if (action == "get_update_cleanup")
                {
                    try
                    {
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "get_update_cleanup_result", days = _hostSettings?.UpdateCleanupDays ?? 7, keepLatest = _hostSettings?.UpdateKeepLatest ?? 1 }));
                    }
                    catch { }
                }
                else if (action == "save_db")
                {
                    try
                    {
                        var status = root.TryGetProperty("status", out var st) ? st.GetString() : "unknown";
                        var ts = root.TryGetProperty("timestamp", out var t) ? t.GetString() : DateTime.UtcNow.ToString("o");
                        var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "host_debug.log");
                        Directory.CreateDirectory(Path.GetDirectoryName(log));
                        File.AppendAllText(log, $"[{ts}] save_db status={status} message={message}\n");
                    }
                    catch { }
                }
            }
            catch
            {
                // silent
            }
        }

        // ✅ GitHub Version Check + Update Popup
        private async Task CheckForUpdateAsync()
        {
            try
            {
                var info = await UpdateService.GetLatestAsync();

                if (info != null && UpdateService.IsNewer(info.LatestVersion))
                {
                    var win = new UpdateWindow(info);
                    win.Owner = this;
                    win.ShowDialog();
                }
            }
            catch
            {
                // Network error হলে silent fail
            }
        }

        private void LoadHostSettings()
        {
            try
            {
                var dir = Path.GetDirectoryName(_hostSettingsFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(_hostSettingsFile))
                {
                    var txt = File.ReadAllText(_hostSettingsFile);
                    _hostSettings = JsonSerializer.Deserialize<HostBackupSettings>(txt) ?? new HostBackupSettings();
                }
                else
                {
                    _hostSettings = new HostBackupSettings();
                    SaveHostSettings();
                }
            }
            catch
            {
                _hostSettings = new HostBackupSettings();
            }
        }

        private void SaveHostSettings()
        {
            try
            {
                var dir = Path.GetDirectoryName(_hostSettingsFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var txt = JsonSerializer.Serialize(_hostSettings);
                File.WriteAllText(_hostSettingsFile, txt);
            }
            catch { }
        }

        private void StartBackupTimer()
        {
            try
            {
                _backupTimer = new System.Timers.Timer(TimeSpan.FromMinutes(30).TotalMilliseconds);
                _backupTimer.AutoReset = true;
                _backupTimer.Elapsed += async (s, e) => await Task.Run(async () => await CheckAndRunScheduledBackup());
                _backupTimer.Start();
            }
            catch { }
        }

        private async Task CheckAndRunScheduledBackup()
        {
            try
            {
                if (_hostSettings == null) LoadHostSettings();
                if (_webRootPath == null) return;

                var now = DateTime.UtcNow;
                var days = Math.Max(1, _hostSettings.ScheduleDays);
                if (_hostSettings.LastRunUtc == DateTime.MinValue || (now - _hostSettings.LastRunUtc).TotalDays >= days)
                {
                    // Request the web app to emit its current state via the existing backup_local handler
                    try
                    {
                        var req = JsonSerializer.Serialize(new { action = "request_state_for_backup" });
                        webView.CoreWebView2.PostWebMessageAsJson(req);
                    }
                    catch { }

                    // Wait briefly for the web app to send backup_local which writes a kams_state_*.json file
                    await Task.Delay(5000);

                    var backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "Backups");
                    if (!Directory.Exists(backupRoot)) Directory.CreateDirectory(backupRoot);

                    var files = Directory.GetFiles(backupRoot, "kams_state_*.json");
                    Array.Sort(files, (a, b) => File.GetCreationTimeUtc(b).CompareTo(File.GetCreationTimeUtc(a)));
                    if (files.Length > 0)
                    {
                        var latest = files[0];
                        // Also run existing zip backup for compatibility
                        try { BackupService.RunBackup(_webRootPath); } catch { }

                        // If auto-upload enabled try upload with tag mapping
                        if (_hostSettings.AutoUploadToDrive)
                        {
                            var tag = _hostSettings.ScheduleDays == 1 ? "D1" : _hostSettings.ScheduleDays == 3 ? "D3" : "D7";
                            try
                            {
                                await GoogleDriveService.UploadFileWithTagAsync(latest, tag);
                            }
                            catch { }
                        }

                        _hostSettings.LastRunUtc = now;
                        SaveHostSettings();
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "scheduled_backup_done", path = files[0], uploaded = _hostSettings.AutoUploadToDrive }));
                    }
                }
            }
            catch { }
        }
    }
}
