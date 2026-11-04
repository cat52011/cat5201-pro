using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// File Generation v1：把報告內容安全寫到 _generated 資料夾，回傳 GeneratedFilePayload。
    /// 負責：檔名清理、時間戳避免覆蓋、UTF-8 (含 BOM) 寫入以確保中文在外部編輯器正常顯示。
    /// </summary>
    public static class GeneratedFileWriter
    {
        // UTF-8 with BOM：避免 Windows 記事本等工具把中文判讀成亂碼。
        private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        public static GeneratedFilePayload WriteMarkdown(
            string outputDir,
            string title,
            string content,
            string sourceSummary,
            string extension = ".md")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDir))
                {
                    return Failed("輸出資料夾未設定。", title);
                }

                Directory.CreateDirectory(outputDir);

                string baseName = SanitizeFileName(title);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "report";

                string fileName = $"{baseName}{extension}";
                string fullPath = Path.GetFullPath(Path.Combine(outputDir, fileName));

                // 確保最終路徑仍在 outputDir 之內（防止清理後殘留的路徑分隔符逃逸）。
                string rootFull = Path.GetFullPath(outputDir);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    return Failed("輸出路徑超出允許範圍。", title);
                }

                fullPath = UniquePath(fullPath);
                fileName = Path.GetFileName(fullPath);

                content ??= "";
                File.WriteAllText(fullPath, content, Utf8Bom);

                NotifyWritten(fullPath);
                return new GeneratedFilePayload
                {
                    Format = extension == ".txt" ? "text" : "markdown",
                    FileName = fileName,
                    FilePath = fullPath,
                    Title = title ?? "",
                    CharacterCount = content.Length,
                    ByteCount = Utf8Bom.GetByteCount(content),
                    Success = true,
                    SourceSummary = sourceSummary ?? ""
                };
            }
            catch (Exception ex)
            {
                return Failed(ex.Message, title);
            }
        }

        public static GeneratedFilePayload WriteDocx(
            string outputDir,
            string title,
            byte[] content,
            string sourceSummary)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDir))
                    return Failed("輸出資料夾未設定。", title);

                Directory.CreateDirectory(outputDir);

                string baseName = SanitizeFileName(title);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "report";

                string fileName = $"{baseName}.docx";
                string fullPath = Path.GetFullPath(Path.Combine(outputDir, fileName));

                string rootFull = Path.GetFullPath(outputDir);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return Failed("輸出路徑超出允許範圍。", title);


                fullPath = UniquePath(fullPath);
                fileName = Path.GetFileName(fullPath);
                File.WriteAllBytes(fullPath, content);

                NotifyWritten(fullPath);
                return new GeneratedFilePayload
                {
                    Format = "docx",
                    FileName = fileName,
                    FilePath = fullPath,
                    Title = title ?? "",
                    CharacterCount = 0,
                    ByteCount = content.Length,
                    Success = true,
                    SourceSummary = sourceSummary ?? ""
                };
            }
            catch (Exception ex)
            {
                return Failed(ex.Message, title);
            }
        }

        public static GeneratedFilePayload WritePdf(
            string outputDir,
            string title,
            byte[] content,
            string sourceSummary)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDir))
                    return Failed("輸出資料夾未設定。", title);

                if (content == null || content.Length == 0)
                    return Failed("PDF 內容為空。", title);

                Directory.CreateDirectory(outputDir);

                string baseName = SanitizeFileName(title);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "report";

                string fileName = $"{baseName}.pdf";
                string fullPath = Path.GetFullPath(Path.Combine(outputDir, fileName));

                string rootFull = Path.GetFullPath(outputDir);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return Failed("輸出路徑超出允許範圍。", title);


                fullPath = UniquePath(fullPath);
                fileName = Path.GetFileName(fullPath);
                File.WriteAllBytes(fullPath, content);

                NotifyWritten(fullPath);
                return new GeneratedFilePayload
                {
                    Format = "pdf",
                    FileName = fileName,
                    FilePath = fullPath,
                    Title = title ?? "",
                    CharacterCount = 0,
                    ByteCount = content.Length,
                    Success = true,
                    SourceSummary = sourceSummary ?? ""
                };
            }
            catch (Exception ex)
            {
                return Failed(ex.Message, title);
            }
        }

        public static GeneratedFilePayload WritePptx(
            string outputDir,
            string title,
            byte[] content,
            string sourceSummary)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDir))
                    return Failed("輸出資料夾未設定。", title);

                if (content == null || content.Length == 0)
                    return Failed("簡報內容為空。", title);

                Directory.CreateDirectory(outputDir);

                string baseName = SanitizeFileName(title);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "presentation";

                string fileName = $"{baseName}.pptx";
                string fullPath = Path.GetFullPath(Path.Combine(outputDir, fileName));

                string rootFull = Path.GetFullPath(outputDir);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return Failed("輸出路徑超出允許範圍。", title);


                fullPath = UniquePath(fullPath);
                fileName = Path.GetFileName(fullPath);
                File.WriteAllBytes(fullPath, content);

                NotifyWritten(fullPath);
                return new GeneratedFilePayload
                {
                    Format = "pptx",
                    FileName = fileName,
                    FilePath = fullPath,
                    Title = title ?? "",
                    CharacterCount = 0,
                    ByteCount = content.Length,
                    Success = true,
                    SourceSummary = sourceSummary ?? ""
                };
            }
            catch (Exception ex)
            {
                return Failed(ex.Message, title);
            }
        }

        public static GeneratedFilePayload WriteXlsx(
            string outputDir,
            string title,
            byte[] content,
            string sourceSummary)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDir))
                    return Failed("輸出資料夾未設定。", title);

                if (content == null || content.Length == 0)
                    return Failed("試算表內容為空。", title);

                Directory.CreateDirectory(outputDir);

                string baseName = SanitizeFileName(title);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "report";

                string fileName = $"{baseName}.xlsx";
                string fullPath = Path.GetFullPath(Path.Combine(outputDir, fileName));

                string rootFull = Path.GetFullPath(outputDir);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return Failed("輸出路徑超出允許範圍。", title);


                fullPath = UniquePath(fullPath);
                fileName = Path.GetFileName(fullPath);
                File.WriteAllBytes(fullPath, content);

                NotifyWritten(fullPath);
                return new GeneratedFilePayload
                {
                    Format = "xlsx",
                    FileName = fileName,
                    FilePath = fullPath,
                    Title = title ?? "",
                    CharacterCount = 0,
                    ByteCount = content.Length,
                    Success = true,
                    SourceSummary = sourceSummary ?? ""
                };
            }
            catch (Exception ex)
            {
                return Failed(ex.Message, title);
            }
        }

        public static GeneratedFilePayload WriteImage(
            string outputDir,
            string title,
            byte[] content,
            string sourceSummary,
            string extension = ".png")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDir))
                    return Failed("輸出資料夾未設定。", title);

                if (content == null || content.Length == 0)
                    return Failed("圖片內容為空。", title);

                Directory.CreateDirectory(outputDir);

                string baseName = SanitizeFileName(title);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "image";

                string fileName = $"{baseName}{extension}";
                string fullPath = Path.GetFullPath(Path.Combine(outputDir, fileName));

                string rootFull = Path.GetFullPath(outputDir);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return Failed("輸出路徑超出允許範圍。", title);


                fullPath = UniquePath(fullPath);
                fileName = Path.GetFileName(fullPath);
                File.WriteAllBytes(fullPath, content);

                NotifyWritten(fullPath);
                return new GeneratedFilePayload
                {
                    Format = "image",
                    FileName = fileName,
                    FilePath = fullPath,
                    Title = title ?? "",
                    CharacterCount = 0,
                    ByteCount = content.Length,
                    Success = true,
                    SourceSummary = sourceSummary ?? ""
                };
            }
            catch (Exception ex)
            {
                return Failed(ex.Message, title);
            }
        }

        public static GeneratedFilePayload WriteVideo(
            string outputDir,
            string title,
            byte[] content,
            string sourceSummary,
            string extension = ".mp4")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDir))
                    return Failed("輸出資料夾未設定。", title);

                if (content == null || content.Length == 0)
                    return Failed("影片內容為空。", title);

                Directory.CreateDirectory(outputDir);

                string baseName = SanitizeFileName(title);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "video";

                string fileName = $"{baseName}{extension}";
                string fullPath = Path.GetFullPath(Path.Combine(outputDir, fileName));

                string rootFull = Path.GetFullPath(outputDir);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return Failed("輸出路徑超出允許範圍。", title);


                fullPath = UniquePath(fullPath);
                fileName = Path.GetFileName(fullPath);
                File.WriteAllBytes(fullPath, content);

                NotifyWritten(fullPath);
                return new GeneratedFilePayload
                {
                    Format = "video",
                    FileName = fileName,
                    FilePath = fullPath,
                    Title = title ?? "",
                    CharacterCount = 0,
                    ByteCount = content.Length,
                    Success = true,
                    SourceSummary = sourceSummary ?? ""
                };
            }
            catch (Exception ex)
            {
                return Failed(ex.Message, title);
            }
        }

        /// <summary>
        /// 程式碼檔：一次執行的所有檔案放進同一個子資料夾，保留模型取的檔名（含子目錄）。
        ///
        /// 不像其他產出那樣在檔名後面加時間戳：index.html 裡寫的是 &lt;link href="style.css"&gt;，
        /// 檔名一改，預覽與直接執行就接不起來了。撞名的問題交給「每次一個新資料夾」解決。
        /// 寫成不含 BOM 的 UTF-8：BOM 會讓 #! 開頭的腳本與部分直譯器出錯。
        /// </summary>
        public static IReadOnlyList<GeneratedFilePayload> WriteCodeFiles(
            string outputDir,
            string title,
            IReadOnlyList<CodeFileExtractor.CodeFile> files,
            string sourceSummary)
        {
            var results = new List<GeneratedFilePayload>();
            if (files == null || files.Count == 0)
                return results;

            if (string.IsNullOrWhiteSpace(outputDir))
            {
                results.Add(Failed("輸出資料夾未設定。", title));
                return results;
            }

            string folder;
            try
            {
                string baseName = SanitizeFileName(title);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "code";
                folder = Path.GetFullPath(Path.Combine(outputDir, $"{baseName}"));
                for (int k = 2; Directory.Exists(folder); k++)
                    folder = Path.GetFullPath(Path.Combine(outputDir, $"{baseName}_{k}"));
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                results.Add(Failed(ex.Message, title));
                return results;
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            foreach (var file in files)
            {
                try
                {
                    string? relative = CodeFileExtractor.SafeRelativePath(file.FileName);
                    if (relative == null)
                    {
                        results.Add(Failed("檔名不合法：" + file.FileName, file.FileName));
                        continue;
                    }

                    string fullPath = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!fullPath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(Failed("輸出路徑超出允許範圍。", file.FileName));
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllText(fullPath, file.Content ?? "", utf8);
                    NotifyWritten(fullPath);

                    results.Add(new GeneratedFilePayload
                    {
                        Format = "code",
                        FileName = relative,
                        FilePath = fullPath,
                        Title = title ?? "",
                        CharacterCount = (file.Content ?? "").Length,
                        ByteCount = utf8.GetByteCount(file.Content ?? ""),
                        Success = true,
                        SourceSummary = sourceSummary ?? "",
                    });
                }
                catch (Exception ex)
                {
                    results.Add(Failed(ex.Message, file.FileName));
                }
            }

            return results;
        }

        /// <summary>
        /// §18：檔案成功落地後的通知鉤子（由 MainWindow 設定；目前用於 Drive 自動上傳）。
        /// 鉤子失敗絕不影響產檔主流程。
        /// </summary>
        public static Action<string>? PostWriteHook { get; set; }

        private static void NotifyWritten(string fullPath)
        {
            try { PostWriteHook?.Invoke(fullPath); }
            catch (Exception ex) { AppLog.Warn("GeneratedFileWriter", "PostWriteHook 失敗（不影響產檔）", ex); }
        }

        private static GeneratedFilePayload Failed(string error, string title)
        {
            return new GeneratedFilePayload
            {
                Success = false,
                Status = "failed",
                ErrorMessage = error ?? "未知錯誤",
                Title = title ?? ""
            };
        }

        /// <summary>WriteDocx／WriteXlsx 會用的檔名（不含撞名時加的 _2）。要先告訴產檔的一方用什麼檔名時用。</summary>
        public static string PlannedFileName(string title, string extension)
        {
            string baseName = SanitizeFileName(title);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "report";
            return baseName + extension;
        }

        /// <summary>
        /// 在 outputDir 裡找一個還沒用過的標題：fileNamesFor(標題) 列出的檔案都不存在才用，撞名就接 _2、_3…。
        /// 檔名要在產檔前先定下來（例如沙箱裡的報告會引用自己的檔名），寫檔時才不會再被 UniquePath 改名。
        /// </summary>
        public static string UnusedTitle(string outputDir, string title, Func<string, IEnumerable<string>> fileNamesFor)
        {
            if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
                return title;

            for (int n = 1; n < 1000; n++)
            {
                string candidate = n == 1 ? title : $"{title}_{n}";
                if (!fileNamesFor(candidate).Any(f => File.Exists(Path.Combine(outputDir, f))))
                    return candidate;
            }
            return title;
        }

        /// <summary>
        /// 讓檔名唯一。檔名時間戳只到秒，同一秒內寫兩份同標題的檔案會撞名，
        /// 後寫的那份會把先寫的覆蓋掉——覆蓋掉的是使用者已經拿到的產出。
        /// 撞到就加 -2、-3…，不動既有檔案的名字（chip 已經指著它們了）。
        /// </summary>
        private static string UniquePath(string fullPath)
        {
            if (!File.Exists(fullPath))
                return fullPath;

            string dir = Path.GetDirectoryName(fullPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(fullPath);
            string ext = Path.GetExtension(fullPath);

            for (int n = 2; n < 1000; n++)
            {
                string candidate = Path.Combine(dir, $"{stem}_{n}{ext}");
                if (!File.Exists(candidate))
                    return candidate;
            }
            return fullPath;
        }

        private static string SanitizeFileName(string name)
        {
            string text = (name ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return "";

            // 移除標題尾端可能加上的省略號，並濾掉檔名非法字元與路徑分隔符。
            text = text.TrimEnd('…', '.', ' ');

            var invalid = Path.GetInvalidFileNameChars()
                .Concat(new[] { '/', '\\' })
                .Distinct()
                .ToArray();

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                sb.Append(invalid.Contains(c) ? '_' : c);
            }

            string cleaned = sb.ToString().Trim();

            // 限制長度，避免超長路徑。
            if (cleaned.Length > 50)
                cleaned = cleaned.Substring(0, 50).Trim();

            return cleaned;
        }
    }
}
