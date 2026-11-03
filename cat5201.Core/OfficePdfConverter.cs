using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// 用本機的 LibreOffice 把 Word 檔轉成 PDF，給預覽顯示「跟實際一樣」的版面。
    ///
    /// 2026-11-03 使用者：Word 預覽能不能做得跟實際看起來一樣？
    /// 抽文字重排的 HTML 只對得上內容，Claude 用表格排出來的卡片、分欄、底色、字型都看不到。
    /// 有 PDF 配對檔時預覽本來就顯示 PDF；沒有時（例如個人化關了對照 PDF），改用 LibreOffice 當場轉一份。
    ///
    /// 轉出來的 PDF 放在預覽快取（不在產出資料夾、不掛到節點上），同一份檔案（路徑＋大小＋修改時間）只轉一次。
    /// 電腦上沒有 LibreOffice，或轉檔失敗、逾時，回 null，呼叫端照舊用文字版預覽。
    /// </summary>
    public static class OfficePdfConverter
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        /// <summary>找 LibreOffice 的 soffice.exe；找不到回 null。</summary>
        public static string? FindSoffice()
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string candidate = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>快取檔名：同一份檔案（路徑＋大小＋修改時間）對到同一個 PDF；檔案改過就是新的。</summary>
        internal static string CacheKey(string sourcePath)
        {
            var fi = new FileInfo(sourcePath);
            string identity = $"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
        }

        public static async Task<string?> ToPdfAsync(string sourcePath, string cacheDir, CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(sourcePath)) return null;
                string? soffice = FindSoffice();
                if (soffice == null) return null;

                Directory.CreateDirectory(cacheDir);
                string cached = Path.Combine(cacheDir, CacheKey(sourcePath) + ".pdf");
                if (File.Exists(cached) && new FileInfo(cached).Length > 0)
                    return cached;

                // 每次轉檔用獨立的暫存資料夾與 LibreOffice 設定檔：
                // 使用者自己開著 LibreOffice 時，共用設定檔會讓 headless 轉檔卡住或直接結束。
                string work = Path.Combine(Path.GetTempPath(), "cat5201-preview", Guid.NewGuid().ToString("N"));
                string profile = Path.Combine(work, "profile");
                Directory.CreateDirectory(profile);
                // 檔名可能有中文與空白：先複製成英數檔名再轉，避開命令列與編碼問題。
                string input = Path.Combine(work, "source" + Path.GetExtension(sourcePath));
                File.Copy(sourcePath, input);

                var psi = new ProcessStartInfo(soffice)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-env:UserInstallation=" + new Uri(profile).AbsoluteUri);
                psi.ArgumentList.Add("--headless");
                psi.ArgumentList.Add("--norestore");
                psi.ArgumentList.Add("--convert-to");
                psi.ArgumentList.Add("pdf");
                psi.ArgumentList.Add("--outdir");
                psi.ArgumentList.Add(work);
                psi.ArgumentList.Add(input);

                using var process = Process.Start(psi);
                if (process == null) return null;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    if (ct.IsCancellationRequested) throw;
                    AppLog.Warn("PreviewPdf", $"LibreOffice 轉檔逾時（{Timeout.TotalSeconds:0} 秒），改用文字版預覽：{Path.GetFileName(sourcePath)}");
                    return null;
                }

                string produced = Path.Combine(work, "source.pdf");
                if (!File.Exists(produced) || new FileInfo(produced).Length == 0)
                {
                    AppLog.Warn("PreviewPdf", $"LibreOffice 沒有轉出 PDF（exit {process.ExitCode}），改用文字版預覽：{Path.GetFileName(sourcePath)}");
                    return null;
                }

                File.Move(produced, cached, overwrite: true);
                TryDelete(work);
                return cached;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("PreviewPdf", "轉成預覽用 PDF 失敗，改用文字版預覽", ex);
                return null;
            }
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 暫存檔留著不影響 */ }
        }
    }
}
