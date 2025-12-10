using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ISPLedger.Services
{
    public class OAuthTokens
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public string? IdToken { get; set; }
        public int ExpiresIn { get; set; }
        public DateTime ObtainedAt { get; set; }
    }

    public static class GoogleDriveService
    {
        // Publicly exposed last auth URL to allow the host to share it with the web UI
        public static string? LastAuthUrl { get; private set; }

        // Use the Desktop OAuth Client (installed app) provided by Google Cloud
        // NOTE: this must be the Desktop type OAuth client; do NOT use the web OAuth client ID here.
        private const string ClientId = "650056019916-a60t87ao6n76r4qksc2gtv076uqsh46m.apps.googleusercontent.com";
        // Client secret for the desktop OAuth client (kept here to perform token exchange on the host)
        private const string ClientSecret = "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";
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        // Include OpenID scopes so Firebase can verify the returned ID token after a successful sign-in.
        // Also include Drive scopes which will be used for backups later.
        private static readonly string[] Scopes = new[] { "openid", "email", "profile", "https://www.googleapis.com/auth/drive.file", "https://www.googleapis.com/auth/drive.appdata" };

        private static readonly string CredFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "Credentials");
        private static readonly string TokenFile = Path.Combine(CredFolder, "gdrive_tokens.bin");

        // Optional onStarted callback receives the auth URL as soon as it's available (for UI fallback).
        public static async Task<bool> AuthenticateAsync(Action<string>? onStarted = null)
        {
            Directory.CreateDirectory(CredFolder);
            try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] AuthenticateAsync START\n"); } catch { }
            // Create a loopback HttpListener on an available port and try multiple loopback hostnames
            HttpListener listener = null;
            var port = GetFreePort();
            string redirectUri = null;
            var candidates = new[] { "localhost", "127.0.0.1", "[::1]" };

            foreach (var host in candidates)
            {
                try
                {
                    var prefix = $"http://{host}:{port}/";
                    var tmp = new HttpListener();
                    tmp.Prefixes.Add(prefix);
                    // Start will fail if the prefix cannot be registered for this user.
                    tmp.Start();
                    listener = tmp;
                    redirectUri = prefix;
                    try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] HttpListener listening on {prefix}\n"); } catch { }
                    break;
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] HttpListener start failed for host={host} port={port}: {ex.Message}\n"); } catch { }
                }
            }

            if (listener == null || redirectUri == null)
            {
                // Could not start HttpListener on preferred loopback addresses; try ephemeral HttpListener without prefix (experimental)
                try
                {
                    listener = new HttpListener();
                    redirectUri = $"http://localhost:{port}/";
                    listener.Prefixes.Add(redirectUri);
                    listener.Start();
                    try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] Fallback HttpListener started on {redirectUri}\n"); } catch { }
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] Failed to start any HttpListener on port {port}: {ex}\n"); } catch { }
                    throw new InvalidOperationException("Failed to start local HTTP listener for OAuth redirect. Ensure the app has permission to listen on loopback addresses.", ex);
                }
            }

            var state = Guid.NewGuid().ToString("N");
            var url = $"{AuthEndpoint}?response_type=code&client_id={Uri.EscapeDataString(ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(string.Join(' ', Scopes))}&access_type=offline&prompt=consent&state={state}";

            // Expose the URL so calling host can provide a fallback copy link to the web UI
            LastAuthUrl = url;
            try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] OAuth URL: {url}\n"); } catch { }
            // Notify caller (host) immediately so it can forward the URL to the web UI if needed
            try { onStarted?.Invoke(url); } catch { }

            // Open system browser (try multiple strategies and log failures)
            bool openSucceeded = false;
            try
            {
                openSucceeded = TryOpenUrl(url);
                try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] TryOpenUrl returned: {openSucceeded} for URL: {url}\n"); } catch { }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] Exception while trying to open browser: {ex}\nURL: {url}\n"); } catch { }
                openSucceeded = false;
            }

            // Wait for incoming request with timeout
            try
            {
                var getCtxTask = listener.GetContextAsync();
                var completed = await Task.WhenAny(getCtxTask, Task.Delay(TimeSpan.FromMinutes(5)));
                if (completed != getCtxTask)
                {
                    try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] OAuth listener timed out waiting for redirect.\n"); } catch { }
                    try { listener.Stop(); } catch { }
                    return false;
                }

                var context = getCtxTask.Result;
                var req = context.Request;
                var res = context.Response;

                var q = req.QueryString;
                var code = q["code"];
                var returnedState = q["state"];

                var responseHtml = "<html><body><h2>You may close this window and return to the app.</h2></body></html>";
                var buffer = Encoding.UTF8.GetBytes(responseHtml);
                res.ContentLength64 = buffer.Length;
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                res.OutputStream.Close();
                try { listener.Stop(); } catch { }

                if (string.IsNullOrEmpty(code) || returnedState != state)
                {
                    try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] OAuth redirect missing code or state mismatch (state expected={state} returned={returnedState}).\n"); } catch { }
                    return false;
                }

                // Exchange code for tokens
                using var client = new HttpClient();
                var data = new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                    ["redirect_uri"] = redirectUri,
                    ["grant_type"] = "authorization_code"
                };

                var resp = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(data));
                var txt = await resp.Content.ReadAsStringAsync();
                var doc = JsonSerializer.Deserialize<JsonElement>(txt);

                if (doc.TryGetProperty("access_token", out var at))
                {
                    var tokens = new OAuthTokens
                    {
                        AccessToken = at.GetString(),
                        RefreshToken = doc.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                        IdToken = doc.TryGetProperty("id_token", out var idt) ? idt.GetString() : null,
                        ExpiresIn = doc.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 0,
                        ObtainedAt = DateTime.UtcNow
                    };

                    SaveTokens(tokens);
                    try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] OAuth tokens obtained and saved.\n"); } catch { }

                    // If we obtained an id_token, first verify it against Google's JWKs, then sign-in with Firebase.
                    try
                    {
                        if (!string.IsNullOrEmpty(tokens.IdToken))
                        {
                            // Verify Google ID token signature and claims
                            var (verified, payload) = await FirebaseService.VerifyGoogleIdTokenAsync(tokens.IdToken, ClientId);
                            try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] Google id_token verification: {verified}\n"); } catch { }

                            if (verified)
                            {
                                var fb = await FirebaseService.SignInWithIdpAsync(tokens.IdToken);
                                if (fb.HasValue)
                                {
                                    try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] Firebase signInWithIdp succeeded.\n"); } catch { }
                                }
                                else
                                {
                                    try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] Firebase signInWithIdp FAILED.\n"); } catch { }
                                }
                            }
                            else
                            {
                                try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] id_token verification failed; skipping Firebase signIn.\n"); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] Firebase signIn exception: {ex}\n"); } catch { }
                    }

                    return true;
                }

                try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] Token exchange failed: {txt}\n"); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(CredFolder, "host_debug.log"), $"[{DateTime.UtcNow:o}] Exception while handling OAuth redirect: {ex}\n"); } catch { }
                try { listener.Stop(); } catch { }
                return false;
            }
        }

        public static async Task<string> GetAccessTokenAsync()
        {
            var tokens = LoadTokens();
            if (tokens == null) return null;
            // If expired or near expiry, refresh
            if (tokens.ObtainedAt.AddSeconds(tokens.ExpiresIn - 60) <= DateTime.UtcNow)
            {
                var ok = await RefreshAccessTokenAsync(tokens);
                if (!ok) return null;
                tokens = LoadTokens();
            }
            return tokens.AccessToken;
        }

        private static async Task<bool> RefreshAccessTokenAsync(OAuthTokens tokens)
        {
            if (tokens == null || string.IsNullOrEmpty(tokens.RefreshToken)) return false;
            using var client = new HttpClient();
            var data = new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken
            };
            var resp = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(data));
            var txt = await resp.Content.ReadAsStringAsync();
            var doc = JsonSerializer.Deserialize<JsonElement>(txt);
            if (doc.TryGetProperty("access_token", out var at))
            {
                tokens.AccessToken = at.GetString();
                tokens.ExpiresIn = doc.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : tokens.ExpiresIn;
                tokens.ObtainedAt = DateTime.UtcNow;
                SaveTokens(tokens);
                return true;
            }
            return false;
        }

        public static async Task<bool> UploadFileAsync(string filePath, string mime = "application/json")
        {
            var access = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(access)) return false;

            // Upload to Drive appDataFolder or files (here we use appDataFolder to store backups)
            // Create metadata
            var meta = new { name = Path.GetFileName(filePath), parents = new string[] { "appDataFolder" } };
            var metaJson = JsonSerializer.Serialize(meta);

            using var content = new MultipartFormDataContent();
            var metaContent = new StringContent(metaJson, Encoding.UTF8, "application/json");
            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);

            // Use multipart/related upload
            var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id")
            {
                Content = new MultipartContent("related")
            };

            var related = (MultipartContent)request.Content;
            related.Add(new StringContent(metaJson, Encoding.UTF8, "application/json"));
            related.Add(fileContent);

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);

            using var client = new HttpClient();
            var resp = await client.SendAsync(request);
            var ok = resp.IsSuccessStatusCode;
            return ok;
        }

        public static async Task<bool> DeleteFileAsync(string fileId)
        {
            var access = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(access)) return false;

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            var resp = await client.DeleteAsync($"https://www.googleapis.com/drive/v3/files/{fileId}");
            return resp.IsSuccessStatusCode;
        }

        public static async Task<bool> UploadFileWithTagAsync(string filePath, string tag, string mime = "application/json", int keep = 3)
        {
            var access = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(access)) return false;

            var name = $"kams_backup_{tag}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
            var meta = new { name = name, parents = new string[] { "appDataFolder" } };
            var metaJson = JsonSerializer.Serialize(meta);
            var fileBytes = await File.ReadAllBytesAsync(filePath);

            using var content = new MultipartContent("related");
            content.Add(new StringContent(metaJson, Encoding.UTF8, "application/json"));
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
            content.Add(fileContent);

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            var resp = await client.PostAsync("https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id", content);
            var ok = resp.IsSuccessStatusCode;

            // Cleanup older files with same tag: keep newest `keep`
            try
            {
                var files = await ListFilesAsync();
                if (files != null)
                {
                    var matched = new List<(string Id, string Name, DateTime Modified)>();
                    foreach (var f in files)
                    {
                        if (!string.IsNullOrEmpty(f.Name) && f.Name.Contains($"_{tag}_"))
                        {
                            matched.Add((f.Id, f.Name, f.ModifiedTime));
                        }
                    }
                    matched.Sort((a, b) => b.Modified.CompareTo(a.Modified));
                    for (int i = keep; i < matched.Count; i++)
                    {
                        try { await DeleteFileAsync(matched[i].Id); } catch { }
                    }
                }
            }
            catch { }

            return ok;
        }

        public static async Task<List<(string Id, string Name, DateTime ModifiedTime)>> ListFilesAsync()
        {
            var access = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(access)) return null;

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);

            // Query files in appDataFolder
            var url = "https://www.googleapis.com/drive/v3/files?spaces=appDataFolder&pageSize=100&fields=files(id,name,modifiedTime)";
            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var txt = await resp.Content.ReadAsStringAsync();
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(txt);
                var list = new List<(string, string, DateTime)>();
                if (doc.TryGetProperty("files", out var files))
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        var id = f.GetProperty("id").GetString();
                        var name = f.GetProperty("name").GetString();
                        var mt = f.TryGetProperty("modifiedTime", out var mte) ? DateTime.Parse(mte.GetString()) : DateTime.MinValue;
                        list.Add((id, name, mt));
                    }
                }
                return list;
            }
            catch
            {
                return null;
            }
        }

        public static async Task<string> DownloadFileAsync(string fileId)
        {
            var access = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(access)) return null;

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            var url = $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media";
            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ISPLedger", "Backups");
            Directory.CreateDirectory(backupRoot);
            var path = Path.Combine(backupRoot, $"kams_drive_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static bool TryOpenUrl(string url)
        {
            var logPath = Path.Combine(CredFolder, "host_debug.log");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] Opened URL via UseShellExecute: {url}\n"); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] UseShellExecute failed: {ex}\n"); } catch { }
            }

            // Try explorer.exe which is a reliable way to open URLs on Windows
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", url) { UseShellExecute = true });
                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] Opened URL via explorer: {url}\n"); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] explorer.exe failed: {ex}\n"); } catch { }
            }

            // Fallback to cmd start which usually works on Windows
            try
            {
                var psiCmd = new System.Diagnostics.ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true, UseShellExecute = false };
                System.Diagnostics.Process.Start(psiCmd);
                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] Opened URL via cmd start: {url}\n"); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] cmd start failed: {ex}\n"); } catch { }
            }

            // As a last resort, write the URL to the debug log for manual copy
            try
            {
                File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] Please open this URL manually: {url}\n");
            }
            catch { }

            return false;
        }

        private static void SaveTokens(OAuthTokens tokens)
        {
            var json = JsonSerializer.Serialize(tokens);
            var bytes = Encoding.UTF8.GetBytes(json);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenFile, protectedBytes);
        }

        private static OAuthTokens LoadTokens()
        {
            try
            {
                if (!File.Exists(TokenFile)) return null;
                var protectedBytes = File.ReadAllBytes(TokenFile);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(bytes);
                return JsonSerializer.Deserialize<OAuthTokens>(json);
            }
            catch { return null; }
        }
    }
}
