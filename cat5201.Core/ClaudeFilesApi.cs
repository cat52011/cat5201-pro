using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// Anthropic Files API：把檔案上傳給 Anthropic，拿到一個 file_id。
    ///
    /// 用途：讓沙箱看得到我們自己生的圖。
    /// 最佳品質模式由 Claude 在沙箱裡排版，而沙箱裡沒有圖片模型——它只畫得出色塊、
    /// 線條與圖表，永遠生不出照片。所以照片得由我們這邊先生好（gpt-image），
    /// 上傳之後以 container_upload 內容塊送進容器，Claude 再把它們嵌進簡報。
    /// </summary>
    public static class ClaudeFilesApi
    {
        private const string ApiBase = "https://api.anthropic.com/v1";
        private const string ApiVersion = "2023-06-01";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

        /// <summary>上傳一個檔案，回傳 file_id（失敗回 null，呼叫端照常往下走，只是沒有圖）。</summary>
        public static async Task<string?> UploadAsync(
            byte[] bytes, string fileName, string contentType, CancellationToken ct = default)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

            fileName = SafeUploadName(fileName);

            string key = ApiKeyStore.Resolve("ANTHROPIC_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
                return null;

            try
            {
                using var content = new MultipartFormDataContent();
                var filePart = new ByteArrayContent(bytes);
                filePart.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                content.Add(filePart, "file", fileName);

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/files") { Content = content };
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", ApiVersion);

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    AppLog.Warn("Files", $"上傳 {fileName} 失敗（{(int)resp.StatusCode}）：{Shorten(body)}");
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("Files", $"上傳 {fileName} 失敗", ex);
                return null;
            }
        }

        /// <summary>
        /// 上傳用的檔名：只留英數與 . _ -。
        ///
        /// 2026-11-03：「HW1作業範本.docx」「螢幕擷取畫面 2026-11-03 154555.png」上傳全被拒（400 forbidden character）。
        /// .NET 把非 ASCII 檔名編碼成 =?utf-8?B?…?=，裡面的 ? 是 Files API 不收的字元。
        /// 檔名有改時加上序號，避免「HW1作業範本」「HW1欄位說明」都變成「HW1」而撞名；
        /// 原檔名由呼叫端另外告訴模型。
        /// </summary>
        public static string SafeUploadName(string? fileName, int? index = null)
        {
            string name = System.IO.Path.GetFileName(fileName ?? "");
            string ext = Clean(System.IO.Path.GetExtension(name).TrimStart('.'));
            string stem = Clean(System.IO.Path.GetFileNameWithoutExtension(name));
            string dotExt = ext.Length > 0 ? "." + ext : "";

            if (stem.Length > 0 && stem + dotExt == name)
                return name;

            if (stem.Length == 0) stem = "file";
            if (stem.Length > 60) stem = stem[..60];
            return index.HasValue ? $"{stem}_{index.Value}{dotExt}" : stem + dotExt;

            static string Clean(string s)
            {
                var sb = new System.Text.StringBuilder();
                foreach (char c in s)
                {
                    bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';
                    if (ok) sb.Append(c);
                    else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
                }
                return sb.ToString().Trim('_', '.', '-');
            }
        }

        private static string Shorten(string? text, int max = 200)
        {
            string t = (text ?? "").Replace("\n", " ").Trim();
            return t.Length <= max ? t : t.Substring(0, max) + "…";
        }
    }
}
