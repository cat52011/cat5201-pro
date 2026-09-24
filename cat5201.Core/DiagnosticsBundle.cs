using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 「回報問題」一鍵打包：最近 7 天日誌 + 環境資訊 → 一個 zip。
    /// 試用者只會說「它壞了」；有了這包，開發者才查得到發生什麼事。
    ///
    /// 隱私底線：API 金鑰與 Bearer token 一律遮蔽、Windows 使用者名稱從路徑移除；
    /// 不收集對話內容、專案檔、影片提示詞（影片任務只給狀態計數）。
    /// </summary>
    public static class DiagnosticsBundle
    {
        private static readonly TimeSpan LogWindow = TimeSpan.FromDays(7);

        private static readonly Regex[] SecretPatterns =
        {
            new(@"sk-(?:ant-|proj-)?[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled),            // OpenAI / Anthropic
            new(@"AIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled),                          // Google
            new(@"pplx-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled),                         // Perplexity
            new(@"(?i)bearer\s+[A-Za-z0-9._\-]{16,}", RegexOptions.Compiled),
            new(@"(?i)(api[_-]?key|x-goog-api-key|x-api-key|access_token|refresh_token)(\W{1,4})[A-Za-z0-9._\-]{12,}", RegexOptions.Compiled),
        };

        /// <summary>遮蔽金鑰與使用者名稱。公開給測試釘住。</summary>
        public static string Scrub(string? text)
        {
            string s = text ?? "";

            foreach (var pattern in SecretPatterns)
            {
                s = pattern.Replace(s, m =>
                    m.Groups.Count > 2 && m.Groups[1].Success
                        ? $"{m.Groups[1].Value}{m.Groups[2].Value}[已遮蔽]"
                        : "[已遮蔽]");
            }

            // C:\Users\<名字>\... → C:\Users\<user>\...（名字常是真名）
            s = Regex.Replace(s, @"(?i)([A-Z]:\\Users\\)[^\\/:*?""<>|\r\n]+", "$1<user>");
            return s;
        }

        /// <summary>建立 zip，回傳完整路徑。outputDir 不存在會自動建立。</summary>
        public static string Create(string outputDir, string appVersion, IEnumerable<string>? extraInfo = null)
        {
            Directory.CreateDirectory(outputDir);
            string zipPath = Path.Combine(outputDir, $"cat5201-pro_回報_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            AddText(zip, "診斷資訊.txt", BuildInfo(appVersion, extraInfo));

            string? logDir = AppLog.DirectoryPath;
            if (!string.IsNullOrWhiteSpace(logDir) && Directory.Exists(logDir))
            {
                var cutoff = DateTime.Now - LogWindow;
                foreach (var file in new DirectoryInfo(logDir).GetFiles("*.log")
                             .Where(f => f.LastWriteTime >= cutoff)
                             .OrderBy(f => f.Name))
                {
                    try
                    {
                        // 日誌可能正被寫入：共用讀取，不鎖住主程式。
                        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        AddText(zip, "logs/" + file.Name, Scrub(reader.ReadToEnd()));
                    }
                    catch (Exception ex)
                    {
                        AddText(zip, "logs/" + file.Name + ".unreadable.txt", $"無法讀取：{ex.Message}");
                    }
                }
            }

            return zipPath;
        }

        private static string BuildInfo(string appVersion, IEnumerable<string>? extraInfo)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"產生時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss}（{TimeZoneInfo.Local.DisplayName}）");
            sb.AppendLine($"App 版本：{appVersion}");
            sb.AppendLine($"作業系統：{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）");
            sb.AppendLine($".NET：{RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"語系：{CultureInfo.CurrentUICulture.Name}");
            sb.AppendLine($"今日花費：{SpendLedger.TodayDisplay()}");

            var jobs = VideoJobJournal.GetStatusCounts();
            if (jobs.Count > 0)
                sb.AppendLine("影片任務：" + string.Join("、", jobs.Select(kv => $"{kv.Key} {kv.Value}")));

            foreach (var line in extraInfo ?? Array.Empty<string>())
                sb.AppendLine(Scrub(line));

            sb.AppendLine();
            sb.AppendLine("（本檔與日誌已自動遮蔽 API 金鑰與 Windows 使用者名稱；不含對話內容與專案檔。）");
            return sb.ToString();
        }

        private static void AddText(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.Write(content);
        }
    }
}
