using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 要求寫程式時，答案裡的程式碼存成檔案，而不是整段貼在輸出區。
    ///
    /// 2026-11-03 使用者要求：程式碼要以檔案呈現、能在畫布內預覽；
    /// 同時要求程式與書面報告時要分類好——程式碼歸程式碼檔，報告只放說明。
    /// 所以這一步排在報告、簡報產生之前：文件拿到的是已經把程式碼換成「檔案參照」的文字，
    /// 不會把整份原始碼抄進 Word 或投影片裡。
    ///
    /// 哪些區塊會變成檔案：
    /// - 有檔名的（info string 寫了檔名、區塊第一行用註解寫檔名、或區塊上一行只有檔名）一律存檔。
    /// - 沒檔名的程式語言區塊，夠長（MinLines 行以上）才存檔，檔名依語言給預設值。
    /// - 指令列（bash、powershell…）短的留在文字裡：「pip install requests」不該變成一個檔案。
    /// - 資料格式（json、yaml…）與輸出結果沒有檔名時留在文字裡：多半是範例輸出，不是要交付的檔案。
    /// </summary>
    public static class CodeFileExtractor
    {
        public sealed record CodeFile(string FileName, string Language, string Content)
        {
            public int LineCount => Content.Length == 0 ? 0 : Content.Split('\n').Length;
        }

        public sealed record Result(IReadOnlyList<CodeFile> Files, string Text);

        /// <summary>沒寫檔名的程式區塊，至少要這麼多行才存成檔案（再短多半是片段示範）。</summary>
        internal const int MinLines = 5;

        /// <summary>沒寫檔名的指令列腳本，至少要這麼多行才算腳本檔（短的是要使用者照打的指令）。</summary>
        internal const int MinShellLines = 10;

        // 語言 → 預設檔名。key 一律小寫。
        private static readonly Dictionary<string, string> DefaultNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["python"] = "main.py", ["py"] = "main.py",
            ["javascript"] = "script.js", ["js"] = "script.js", ["mjs"] = "script.mjs",
            ["typescript"] = "index.ts", ["ts"] = "index.ts",
            ["tsx"] = "App.tsx", ["jsx"] = "App.jsx",
            ["csharp"] = "Program.cs", ["cs"] = "Program.cs", ["c#"] = "Program.cs",
            ["java"] = "Main.java", ["kotlin"] = "Main.kt", ["kt"] = "Main.kt",
            ["go"] = "main.go", ["golang"] = "main.go",
            ["rust"] = "main.rs", ["rs"] = "main.rs",
            ["c"] = "main.c", ["cpp"] = "main.cpp", ["c++"] = "main.cpp", ["cc"] = "main.cpp",
            ["swift"] = "main.swift", ["ruby"] = "main.rb", ["rb"] = "main.rb",
            ["php"] = "index.php", ["sql"] = "query.sql",
            ["html"] = "index.html", ["htm"] = "index.html",
            ["css"] = "style.css", ["scss"] = "style.scss",
            ["r"] = "script.R", ["lua"] = "main.lua", ["dart"] = "main.dart",
            ["vue"] = "App.vue", ["svelte"] = "App.svelte", ["xaml"] = "MainWindow.xaml",
            ["bash"] = "script.sh", ["sh"] = "script.sh", ["shell"] = "script.sh", ["zsh"] = "script.sh",
            ["powershell"] = "script.ps1", ["ps1"] = "script.ps1", ["pwsh"] = "script.ps1",
            ["bat"] = "script.bat", ["cmd"] = "script.bat", ["batch"] = "script.bat",
        };

        private static readonly HashSet<string> ShellLanguages = new(StringComparer.OrdinalIgnoreCase)
        {
            "bash", "sh", "shell", "zsh", "powershell", "ps1", "pwsh", "bat", "cmd", "batch",
        };

        /// <summary>預覽時以程式碼方式顯示的副檔名（含沒有預設檔名、但有檔名時會存的設定檔類）。</summary>
        public static readonly IReadOnlySet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".py", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".cs", ".java", ".kt", ".go", ".rs",
            ".c", ".h", ".cpp", ".hpp", ".cc", ".swift", ".rb", ".php", ".sql", ".css", ".scss",
            ".r", ".lua", ".dart", ".vue", ".svelte", ".xaml", ".xml", ".sh", ".ps1", ".bat", ".cmd",
            ".yaml", ".yml", ".toml", ".ini", ".json", ".csproj", ".gradle", ".dockerfile", ".env.example",
        };

        // ── 要不要把程式碼存成檔案 ──

        private static readonly string[] CodeIntentTerms =
        {
            "程式", "程式碼", "原始碼", "腳本", "函式", "函數", "爬蟲", "網頁", "網站", "前端", "後端",
            "演算法", "實作", "小遊戲", "應用程式",
            "code", "script", "function", "program", "algorithm", "implement",
            "python", "javascript", "typescript", "java", "c#", "c++", "golang", "rust", "html", "css",
            "sql", "react", "vue", "api", "app",
        };

        private static readonly string[] OptOutTerms =
        {
            "不要存成檔案", "不用存成檔案", "不要檔案", "不用檔案", "不要存檔", "不用存檔", "直接貼在這裡",
            "no file", "inline only",
        };

        /// <summary>
        /// 這次的要求是不是要寫程式。任務判成 Code，或要求裡有寫程式的字眼；使用者明說不要檔案時不存。
        /// 判斷寬一點沒關係：答案裡沒有夠格的程式區塊，就什麼都不會存。
        /// </summary>
        public static bool WantsCodeFiles(string? request, NodeTaskMode taskMode)
        {
            string text = request ?? "";
            if (OptOutTerms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return false;

            return taskMode == NodeTaskMode.Code || ContainsCodeTerm(text);
        }

        private static bool ContainsCodeTerm(string text)
        {
            foreach (string term in CodeIntentTerms)
            {
                int i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                while (i >= 0)
                {
                    // 英文詞要是完整的字：「app」不能命中「apple」「happy」，「api」不能命中「capital」。
                    bool ascii = term.All(c => c < 128 && char.IsLetter(c));
                    bool leftOk = !ascii || i == 0 || !char.IsLetter(text[i - 1]);
                    bool rightOk = !ascii || i + term.Length >= text.Length || !char.IsLetter(text[i + term.Length]);
                    if (leftOk && rightOk) return true;
                    i = text.IndexOf(term, i + 1, StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }

        /// <summary>給模型的格式要求：每個檔案一個區塊、第一行寫檔名。檔名對了，預覽與多檔案專案才接得起來。</summary>
        public const string PromptBlock =
            "【程式碼輸出格式】\n" +
            "程式碼會被存成檔案交給使用者，不會整段顯示在對話裡。請照這個格式寫：\n" +
            "1. 每個檔案用一個獨立的程式碼區塊，區塊標上語言。\n" +
            "2. 區塊第一行用該語言的註解寫出檔名，例如 `# app.py`、`// index.js`、`<!-- index.html -->`、`/* style.css */`。" +
            "JSON 這類不能寫註解的檔案，改在區塊上一行單獨寫檔名。\n" +
            "3. 多個檔案互相引用時（例如 HTML 引用 CSS、JS），用同一組相對路徑，讓它們放在同一個資料夾就能直接執行。\n" +
            "4. 安裝或執行指令（例如 pip install、npm run）照常寫在文字裡，不必寫檔名。\n" +
            "5. 程式碼以外的說明照常寫：做了什麼、怎麼執行、需要注意什麼。";

        // ── 抽出程式區塊 ──

        private static readonly Regex OpenFence = new(@"^(?<indent>[ \t]{0,3})(?<fence>`{3,}|~{3,})[ \t]*(?<info>[^`\r\n]*)$", RegexOptions.Compiled);

        // 區塊第一行的檔名註解：# app.py、// src/index.js、<!-- index.html -->、/* style.css */、-- query.sql、檔名：x
        private static readonly Regex CommentName = new(
            @"^\s*(?:#|//|--|;|<!--|/\*|')\s*(?:(?:file(?:name)?|檔名|檔案)\s*[:：]\s*)?(?<name>[\w\-./\\]+\.[A-Za-z0-9]{1,10})\s*(?:-->|\*/)?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 區塊上一行只有檔名：**app.py**、`main.py`:、### src/app.py、檔名：app.py、## 程式碼檔案：`HW1.py`
        // 冒號前允許一小段標籤文字（2026-11-03 機器學習作業的寫法是「## 程式碼檔案：`…py`」）。
        private static readonly Regex LabelName = new(
            @"^\s*(?:#{1,6}\s*)?(?:[-*]\s*)?(?:[^`*:：\n]{0,16}[:：]\s*)?[*_`]*(?<name>[\w\-./\\]+\.[A-Za-z0-9]{1,10})[*_`]*\s*[:：]?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>沒寫檔名的程式區塊，這麼長就算「要交付的程式」，不是示範片段。</summary>
        internal const int DeliverableLines = 30;

        /// <summary>
        /// 要求裡沒提到寫程式，但答案本身就是在交付程式：有寫了檔名的程式區塊，或一段夠長的程式。
        ///
        /// 2026-11-03 機器學習作業：要求只寫「完成此作業並說明」，寫程式的要求在附件裡，
        /// 於是 1,700 行的程式整段貼在輸出區。判斷不能只看使用者打的那一句。
        /// </summary>
        public static bool LooksLikeCodeDeliverable(string? answer)
        {
            var files = Extract(answer).Files;
            var defaults = new HashSet<string>(DefaultNames.Values, StringComparer.OrdinalIgnoreCase);
            // 預設檔名撞名時會變成 main_2.py——那還是預設名，不是模型自己取的檔名。
            static string WithoutNumber(string name) => Regex.Replace(name, @"_\d+(?=\.[^.]+$)", "");
            return files.Any(f => f.LineCount >= DeliverableLines ||
                                  !defaults.Contains(WithoutNumber(System.IO.Path.GetFileName(f.FileName))));
        }

        private static readonly Regex InfoName = new(
            @"(?:title|file(?:name)?|name)\s*=\s*[""']?(?<name>[^""'\s]+)[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 把夠格的程式區塊抽成檔案，原位置換成一行參照。沒有夠格的區塊時，Files 為空、Text 原樣返回。
        /// </summary>
        public static Result Extract(string? markdown)
        {
            string source = (markdown ?? "").Replace("\r\n", "\n");
            var lines = source.Split('\n');
            var output = new List<string>();
            var files = new List<CodeFile>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int i = 0;
            while (i < lines.Length)
            {
                var open = OpenFence.Match(lines[i]);
                if (!open.Success)
                {
                    output.Add(lines[i]);
                    i++;
                    continue;
                }

                string fence = open.Groups["fence"].Value;
                string info = open.Groups["info"].Value.Trim();

                // 找結束 fence（同一種字元、長度不短於開頭、該行沒有其他內容）。
                int close = -1;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string t = lines[j].Trim();
                    if (t.Length >= fence.Length && t.All(c => c == fence[0]))
                    {
                        close = j;
                        break;
                    }
                }

                if (close < 0)
                {
                    // 沒有結尾的區塊（答案被截斷）：原樣保留，不猜。
                    output.AddRange(lines.Skip(i));
                    break;
                }

                var body = lines.Skip(i + 1).Take(close - i - 1).ToList();
                string language = FirstToken(info);
                string? name = NameFromInfo(info);
                bool labelLineUsed = false;

                if (name == null && body.Count > 0)
                {
                    var m = CommentName.Match(body[0]);
                    if (m.Success) name = m.Groups["name"].Value;
                }

                int labelIndex = PreviousNonEmpty(output);
                if (name == null && labelIndex >= 0)
                {
                    var m = LabelName.Match(output[labelIndex]);
                    if (m.Success && LooksLikeFileName(m.Groups["name"].Value))
                    {
                        name = m.Groups["name"].Value;
                        labelLineUsed = true;
                    }
                }

                string? safeName = name == null ? null : SafeRelativePath(name);
                string content = string.Join("\n", body).TrimEnd() + "\n";
                int lineCount = body.Count;

                bool keep = safeName != null || Qualifies(language, lineCount);
                if (!keep)
                {
                    output.AddRange(lines.Skip(i).Take(close - i + 1));
                    i = close + 1;
                    continue;
                }

                string fileName = Unique(safeName ?? DefaultNames[language], usedNames);
                files.Add(new CodeFile(fileName, language, content));

                if (labelLineUsed)
                    output.RemoveAt(labelIndex);
                output.Add($"> 程式碼已存成檔案：`{fileName}`（{lineCount} 行），在節點下方點開即可預覽。");

                i = close + 1;
            }

            if (files.Count == 0)
                return new Result(Array.Empty<CodeFile>(), markdown ?? "");

            return new Result(files, string.Join("\n", output));
        }

        private static bool Qualifies(string language, int lineCount)
        {
            if (!DefaultNames.ContainsKey(language))
                return false;   // 沒標語言、text、json、輸出結果…沒有檔名就不存
            return lineCount >= (ShellLanguages.Contains(language) ? MinShellLines : MinLines);
        }

        private static string FirstToken(string info)
        {
            string first = info.Split(new[] { ' ', '\t', '{' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return first.Trim().ToLowerInvariant();
        }

        private static string? NameFromInfo(string info)
        {
            var m = InfoName.Match(info);
            if (m.Success) return m.Groups["name"].Value;

            // ```python app.py 或 ```app.py：哪個字看起來像檔名就用哪個。
            foreach (string token in info.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token.Trim('"', '\'', '`');
                if (LooksLikeFileName(t))
                    return t;
            }
            return null;
        }

        private static bool LooksLikeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || !s.Contains('.')) return false;
            if (s.Equals(".env.example", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.StartsWith(".", StringComparison.Ordinal)) return false;

            string ext = System.IO.Path.GetExtension(s);
            if (ext.Length < 2 || ext.Length > 11) return false;

            // 副檔名要有字母：「v1.2」「3.14」是版本號與數字，不是檔名。
            return ext.Skip(1).Any(char.IsLetter);
        }

        /// <summary>只允許相對路徑：去掉磁碟機代號、開頭斜線與 ..，避免寫到資料夾外面。</summary>
        internal static string? SafeRelativePath(string name)
        {
            string n = name.Replace('\\', '/').Trim().Trim('/');
            if (n.Length == 0 || n.Contains(':')) return null;
            var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries)
                         .Where(p => p != "." && p != "..")
                         .Select(p => string.Concat(p.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c))))
                         .Where(p => p.Length > 0)
                         .ToList();
            if (parts.Count == 0) return null;
            return string.Join("/", parts);
        }

        private static string Unique(string name, HashSet<string> used)
        {
            if (used.Add(name)) return name;
            string dir = name.Contains('/') ? name[..(name.LastIndexOf('/') + 1)] : "";
            string file = name[dir.Length..];
            string stem = System.IO.Path.GetFileNameWithoutExtension(file);
            string ext = System.IO.Path.GetExtension(file);
            for (int k = 2; ; k++)
            {
                string candidate = $"{dir}{stem}_{k}{ext}";
                if (used.Add(candidate)) return candidate;
            }
        }

        private static int PreviousNonEmpty(List<string> output)
        {
            for (int k = output.Count - 1; k >= 0 && k >= output.Count - 2; k--)
                if (output[k].Trim().Length > 0) return k;
            return -1;
        }
    }
}
