using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    /// <summary>
    /// §18 Google Workspace 整合 — 資料側第一步：Drive 上傳（休眠擴充點，給 OAuth 憑證即啟用）。
    ///
    /// 啟用步驟（使用者動作）：
    /// 1. Google Cloud Console → 建立專案 → 啟用 Drive API → OAuth 同意畫面（External/測試）
    ///    → 建立「桌面應用程式」OAuth 用戶端。
    /// 2. 設環境變數 GOOGLE_OAUTH_CLIENT_ID 與 GOOGLE_OAUTH_CLIENT_SECRET，重啟程式。
    /// 3. 首次呼叫 UploadFileAsync 會開瀏覽器要求授權（drive.file 範圍＝只碰本 App 建立的檔案，最小權限），
    ///    refresh token 以 DPAPI 加密存本機，之後全自動。
    ///
    /// 流程＝標準 OAuth 2.0 桌面 loopback：開瀏覽器 → 127.0.0.1 臨時埠收 code → 換 token →
    /// refresh token 落地 → 之後用 refresh 換 access token → drive/v3 multipart 上傳。
    /// 完全不依賴 UI；接線點（後續）：GeneratedFileWriter 寫檔完成後 / 產出物卡片「存到 Drive」。
    /// </summary>
    public sealed class GoogleDriveService
    {
        private const string Scope = "https://www.googleapis.com/auth/drive.file";
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

        private readonly string _tokenPath;
        private string? _accessToken;
        private DateTime _accessTokenExpiresUtc;

        public GoogleDriveService(string configDir)
        {
            _tokenPath = Path.Combine(configDir, "_google_token.bin");
        }

        private static string ClientId => ApiKeyStore.Resolve("GOOGLE_OAUTH_CLIENT_ID");
        private static string ClientSecret => ApiKeyStore.Resolve("GOOGLE_OAUTH_CLIENT_SECRET");

        /// <summary>OAuth 憑證存在即視為已配置。</summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

        /// <summary>已完成過授權（有 refresh token）。</summary>
        public bool IsAuthorized => File.Exists(_tokenPath);

        /// <summary>主動跑一次授權（設定頁「連結 Google」按鈕用）。已授權則只驗 token 換發。</summary>
        public async Task AuthorizeAsync(CancellationToken ct)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("尚未設定 GOOGLE_OAUTH_CLIENT_ID / GOOGLE_OAUTH_CLIENT_SECRET（環境變數）。");
            await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
        }

        /// <summary>解除連結：刪除本機 refresh token 與帳戶快取。</summary>
        public void Unlink()
        {
            try { File.Delete(_tokenPath); } catch (Exception ex) { AppLog.Warn("GoogleDrive", "解除連結失敗", ex); }
            try { File.Delete(AccountPath); } catch { }
            _accessToken = null;
        }

        // 已連結帳戶的信箱（顯示用）。快取在本機純文字（僅自己機器可見，非機密）。
        private string AccountPath => Path.Combine(Path.GetDirectoryName(_tokenPath)!, "_google_account.txt");

        /// <summary>已快取的帳戶信箱；沒有快取回 null（可呼叫 GetUserEmailAsync 取得）。</summary>
        public string? CachedEmail
        {
            get
            {
                try
                {
                    return File.Exists(AccountPath) ? File.ReadAllText(AccountPath).Trim() : null;
                }
                catch { return null; }
            }
        }

        /// <summary>取已連結帳戶的信箱（drive/v3 about，drive.file 範圍即可），並快取本機。</summary>
        public async Task<string> GetUserEmailAsync(CancellationToken ct)
        {
            string token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(
                HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"取帳戶資訊失敗（{(int)resp.StatusCode}）：{body}");

            using var doc = JsonDocument.Parse(body);
            string email = doc.RootElement.GetProperty("user").GetProperty("emailAddress").GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(email))
            {
                try { File.WriteAllText(AccountPath, email); } catch { }
            }
            return email;
        }

        /// <summary>
        /// 上傳檔案到使用者 Drive（必要時先跑授權）。回傳 (fileId, webViewLink)。
        /// convertTo 給 Google 格式 mime（如 application/vnd.google-apps.document）時，
        /// Drive 會自動把 DOCX→Google Docs、XLSX→Google Sheets（§18「一鍵輸出成 Docs/Sheets」）。
        /// </summary>
        public async Task<(string FileId, string Link)> UploadFileAsync(
            string filePath, CancellationToken ct, string? convertTo = null)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("尚未設定 GOOGLE_OAUTH_CLIENT_ID / GOOGLE_OAUTH_CLIENT_SECRET。");

            string token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);

            string name = Path.GetFileName(filePath);
            string mime = GuessMime(name);
            byte[] bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);

            // drive/v3 multipart：part1=metadata(json)、part2=內容。metadata.mimeType 設為
            // Google 格式時即觸發匯入轉換（原始檔內容照傳，Drive 端轉檔）。
            object metaObj = convertTo == null ? new { name } : new { name, mimeType = convertTo };
            using var content = new MultipartContent("related");
            var meta = new StringContent(JsonSerializer.Serialize(metaObj), Encoding.UTF8, "application/json");
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(mime);
            content.Add(meta);
            content.Add(file);

            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id,webViewLink")
            { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Drive 上傳失敗（{(int)resp.StatusCode}）：{body}");

            using var doc = JsonDocument.Parse(body);
            return (
                doc.RootElement.GetProperty("id").GetString() ?? "",
                doc.RootElement.TryGetProperty("webViewLink", out var l) ? l.GetString() ?? "" : "");
        }

        /// <summary>常用 Google 格式 mime（轉檔上傳用）。</summary>
        public const string GoogleDocMime = "application/vnd.google-apps.document";
        public const string GoogleSheetMime = "application/vnd.google-apps.spreadsheet";

        /// <summary>列出本 App 建立的 Drive 檔案（drive.file 範圍只看得到自己建的）。回傳 (id, name, mime)。</summary>
        public async Task<IReadOnlyList<(string Id, string Name, string Mime)>> ListFilesAsync(CancellationToken ct, int pageSize = 25)
        {
            string token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://www.googleapis.com/drive/v3/files?q=trashed%3Dfalse&orderBy=modifiedTime%20desc&pageSize={pageSize}&fields=files(id,name,mimeType)");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Drive 列檔失敗（{(int)resp.StatusCode}）：{body}");

            var result = new List<(string, string, string)>();
            using var doc = JsonDocument.Parse(body);
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
                result.Add((
                    f.GetProperty("id").GetString() ?? "",
                    f.GetProperty("name").GetString() ?? "",
                    f.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : ""));
            return result;
        }

        /// <summary>下載 Drive 檔案內容到本機路徑（附件匯入用）。</summary>
        public async Task DownloadFileAsync(string fileId, string savePath, CancellationToken ct)
        {
            string token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Drive 下載失敗（{(int)resp.StatusCode}）。");

            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            await using var fs = File.Create(savePath);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        // ===== token 管理 =====

        private async Task<string> EnsureAccessTokenAsync(CancellationToken ct)
        {
            if (_accessToken != null && DateTime.UtcNow < _accessTokenExpiresUtc - TimeSpan.FromMinutes(2))
                return _accessToken;

            string? refresh = LoadRefreshToken();
            if (refresh == null)
                refresh = await RunInteractiveAuthAsync(ct).ConfigureAwait(false);

            var form = new System.Collections.Generic.Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["refresh_token"] = refresh,
                ["grant_type"] = "refresh_token",
            };
            using var resp = await _http.PostAsync(
                "https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // refresh token 失效（被撤銷等）→ 清掉，下次重新授權。
                try { File.Delete(_tokenPath); } catch { }
                throw new InvalidOperationException($"Google token 換發失敗（{(int)resp.StatusCode}）：{body}");
            }

            using var doc = JsonDocument.Parse(body);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            int expires = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expires);
            return _accessToken!;
        }

        /// <summary>互動式授權（開瀏覽器 + loopback 收 code），成功後 refresh token 落地並回傳。</summary>
        private async Task<string> RunInteractiveAuthAsync(CancellationToken ct)
        {
            // 1) 127.0.0.1 臨時埠 listener
            var listener = new HttpListener();
            int port = GetFreePort();
            string redirect = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(redirect);
            listener.Start();

            try
            {
                // 2) 開瀏覽器到授權頁
                string authUrl =
                    "https://accounts.google.com/o/oauth2/v2/auth" +
                    $"?client_id={Uri.EscapeDataString(ClientId)}" +
                    $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                    "&response_type=code" +
                    $"&scope={Uri.EscapeDataString(Scope)}" +
                    "&access_type=offline&prompt=consent";
                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

                // 3) 等 code 回來（3 分鐘逾時）
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                var ctxTask = listener.GetContextAsync();
                using (timeout.Token.Register(() => { try { listener.Stop(); } catch { } }))
                {
                    var ctx = await ctxTask.ConfigureAwait(false);
                    string? code = ctx.Request.QueryString["code"];

                    byte[] page = Encoding.UTF8.GetBytes(
                        "<html><meta charset='utf-8'><body style='font-family:sans-serif;text-align:center;padding-top:80px'>" +
                        (code != null ? "<h2>✅ 授權完成</h2><p>可以關閉此分頁，回到 cat5201-pro。</p>"
                                      : "<h2>❌ 授權失敗</h2><p>請回到程式重試。</p>") +
                        "</body></html>");
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.OutputStream.WriteAsync(page, ct).ConfigureAwait(false);
                    ctx.Response.Close();

                    if (code == null)
                        throw new InvalidOperationException("Google 授權被拒或未回傳 code。");

                    // 4) code 換 token
                    var form = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["client_id"] = ClientId,
                        ["client_secret"] = ClientSecret,
                        ["code"] = code,
                        ["redirect_uri"] = redirect,
                        ["grant_type"] = "authorization_code",
                    };
                    using var resp = await _http.PostAsync(
                        "https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"Google code 換 token 失敗（{(int)resp.StatusCode}）：{body}");

                    using var doc = JsonDocument.Parse(body);
                    string refresh = doc.RootElement.GetProperty("refresh_token").GetString()
                        ?? throw new InvalidOperationException("Google 未回傳 refresh_token（請確認 prompt=consent）。");
                    _accessToken = doc.RootElement.GetProperty("access_token").GetString();
                    _accessTokenExpiresUtc = DateTime.UtcNow.AddMinutes(50);

                    SaveRefreshToken(refresh);
                    return refresh;
                }
            }
            finally
            {
                try { listener.Close(); } catch { }
            }
        }

        // refresh token 比照 ApiKeyStore：DPAPI CurrentUser 加密落地，不上傳。
        private void SaveRefreshToken(string refresh)
        {
            try
            {
                byte[] enc = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(refresh), null, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
                File.WriteAllBytes(_tokenPath, enc);
            }
            catch (Exception ex) { AppLog.Warn("GoogleDrive", "refresh token 落地失敗", ex); }
        }

        private string? LoadRefreshToken()
        {
            try
            {
                if (!File.Exists(_tokenPath)) return null;
                byte[] dec = ProtectedData.Unprotect(
                    File.ReadAllBytes(_tokenPath), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch (Exception ex)
            {
                AppLog.Warn("GoogleDrive", "refresh token 讀取失敗（將重新授權）", ex);
                return null;
            }
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static string GuessMime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".mp4" => "video/mp4",
            ".txt" or ".md" => "text/plain",
            _ => "application/octet-stream",
        };
    }
}
