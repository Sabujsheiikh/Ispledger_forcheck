using System;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace ISPLedger.Services
{
    // Minimal Firebase helper: verifies Google's OIDC id_token signature using Google's JWKs
    // and performs a sign-in with Firebase (signInWithIdp) when requested.
    public static class FirebaseService
    {
        // Firebase web API key (from provided config)
        private const string ApiKey = "AIzaSyA81MJu8aAHoTzyC4LE-_rsj16OZbPS0E8";

        private const string GoogleCertsUrl = "https://www.googleapis.com/oauth2/v3/certs";

        // Fetch JWKs (cached) and return the JSON element containing keys
        private static (JsonElement? keys, DateTime expiresUtc) _cachedKeys = (null, DateTime.MinValue);

        private static async Task<JsonElement?> GetGoogleJwksAsync()
        {
            if (_cachedKeys.keys.HasValue && DateTime.UtcNow < _cachedKeys.expiresUtc)
                return _cachedKeys.keys;

            try
            {
                using var client = new HttpClient();
                var resp = await client.GetAsync(GoogleCertsUrl);
                if (!resp.IsSuccessStatusCode) return null;
                var txt = await resp.Content.ReadAsStringAsync();
                var doc = JsonSerializer.Deserialize<JsonElement>(txt);

                // Cache based on Cache-Control max-age if present
                var cache = resp.Headers.CacheControl?.MaxAge;
                var expiry = DateTime.UtcNow.AddSeconds(cache?.TotalSeconds ?? 3600);
                if (doc.TryGetProperty("keys", out var keys))
                {
                    _cachedKeys = (keys, expiry);
                    return keys;
                }
            }
            catch { }
            return null;
        }

        private static byte[] Base64UrlDecode(string input)
        {
            string s = input;
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        // Verify Google-issued OIDC id_token (JWT) using Google's JWKs
        // expectedAudience should be the OAuth client ID used in the auth request.
        public static async Task<(bool Verified, JsonElement? Payload)> VerifyGoogleIdTokenAsync(string idToken, string expectedAudience)
        {
            try
            {
                if (string.IsNullOrEmpty(idToken)) return (false, null);
                var parts = idToken.Split('.');
                if (parts.Length != 3) return (false, null);

                var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
                var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                var signature = Base64UrlDecode(parts[2]);

                var header = JsonSerializer.Deserialize<JsonElement>(headerJson);
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
                if (!header.TryGetProperty("kid", out var kidEl)) return (false, null);
                var kid = kidEl.GetString();

                var keys = await GetGoogleJwksAsync();
                if (!keys.HasValue) return (false, null);

                JsonElement? match = null;
                foreach (var k in keys.Value.EnumerateArray())
                {
                    if (k.TryGetProperty("kid", out var kk) && kk.GetString() == kid)
                    {
                        match = k;
                        break;
                    }
                }
                if (!match.HasValue) return (false, null);

                var m = match.Value;
                if (!m.TryGetProperty("n", out var nEl) || !m.TryGetProperty("e", out var eEl)) return (false, null);
                var n = nEl.GetString();
                var e = eEl.GetString();

                var modulus = Base64UrlDecode(n);
                var exponent = Base64UrlDecode(e);

                var rsa = RSA.Create();
                var rsaParams = new RSAParameters { Modulus = modulus, Exponent = exponent };
                rsa.ImportParameters(rsaParams);

                // Verify signature: compute SHA256 of header.payload
                var signed = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(signed);

                var verified = rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (!verified) return (false, null);

                // Validate claims: iss, aud, exp
                if (payload.TryGetProperty("iss", out var issEl))
                {
                    var iss = issEl.GetString();
                    if (iss != "https://accounts.google.com" && iss != "accounts.google.com") return (false, null);
                }

                if (payload.TryGetProperty("aud", out var audEl))
                {
                    var aud = audEl.GetString();
                    if (aud != expectedAudience) return (false, null);
                }

                if (payload.TryGetProperty("exp", out var expEl))
                {
                    var exp = expEl.GetInt64();
                    var expDt = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
                    if (expDt < DateTime.UtcNow.AddMinutes(-1)) return (false, null);
                }

                return (true, payload);
            }
            catch
            {
                return (false, null);
            }
        }

        // Sign in / verify an OpenID Connect ID token issued by Google and obtain a Firebase token
        // Uses the `signInWithIdp` endpoint. Returns the parsed JSON response or null on failure.
        public static async Task<JsonElement?> SignInWithIdpAsync(string idToken)
        {
            try
            {
                if (string.IsNullOrEmpty(idToken)) return null;

                var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key={ApiKey}";
                using var client = new HttpClient();

                var post = new
                {
                    postBody = $"id_token={Uri.EscapeDataString(idToken)}&providerId=google.com",
                    requestUri = "http://localhost",
                    returnIdpCredential = true,
                    returnSecureToken = true
                };

                var json = JsonSerializer.Serialize(post);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(url, content);
                var txt = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode) return null;
                var doc = JsonSerializer.Deserialize<JsonElement>(txt);
                return doc;
            }
            catch
            {
                return null;
            }
        }
    }
}
