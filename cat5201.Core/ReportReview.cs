using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 獨立審查：報告做完之後，由沒有參與撰寫的一方對照數字與文字、找矛盾，列出修改清單。不改報告本身。
    ///
    /// 2026-11-03 使用者要求。起因是機器學習作業的沙箱報告：內容完整，但比較表裡有 nan、
    /// 有「影響為 改善」這種程式拼接留下的句子、也有「兩個尺度結論不一定相同」但結果其實相同的前後矛盾。
    /// 寫的人自己檢查容易看不到，所以分成兩層：
    ///   1. 程式檢查（免費、不經 AI）：nan、None、#DIV/0!、沒填完的模板字。
    ///   2. 審查模型：只核對數字、找矛盾，每一項都要引用原文當依據；不評論文筆、不提新分析。
    ///      有同一趟產出的程式碼時，也核對報告描述的做法和程式是否一致（2026-11-04）。
    /// </summary>
    public static class ReportReview
    {
        public sealed record Issue(string Severity, string Where, string Problem, string Evidence, string Suggestion);

        public sealed record Result(
            IReadOnlyList<Issue> ModelIssues,
            IReadOnlyList<Issue> ProgramIssues,
            string ReviewerModel,
            string ReviewerNote,
            string Checked,
            string RawReply);

        /// <summary>送給審查模型的報告全文上限（字）。一般報告一萬多字，留餘裕。</summary>
        internal const int MaxReportChars = 30000;

        /// <summary>送給審查模型的結果檔上限（字）。報告裡多半已有同樣的表，結果檔只是對照用。</summary>
        internal const int MaxTableChars = 10000;

        /// <summary>
        /// 送審的程式檔（核對做法描述用）。不限 Python：R、Jupyter、MATLAB、SQL、Java、C++ 寫的作業也要看得到程式。
        /// HTML、CSS 這類版面檔不算。
        /// </summary>
        internal static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".py", ".ipynb", ".r", ".rmd", ".qmd", ".m", ".jl", ".sql", ".sas", ".do", ".stan",
            ".js", ".mjs", ".ts", ".java", ".kt", ".scala", ".cs", ".c", ".h", ".cpp", ".cc", ".hpp",
            ".go", ".rs", ".swift", ".rb", ".php", ".lua", ".dart", ".sh", ".ps1",
        };

        public static bool IsCodeFile(string? path) => CodeExtensions.Contains(System.IO.Path.GetExtension(path ?? ""));

        /// <summary>讀程式檔給審查看。.ipynb 只取程式碼格（輸出、圖片和 Markdown 說明不送）。</summary>
        public static string ReadCode(string path)
        {
            string text = System.IO.File.ReadAllText(path);
            return string.Equals(System.IO.Path.GetExtension(path), ".ipynb", StringComparison.OrdinalIgnoreCase)
                ? NotebookCode(text)
                : text;
        }

        /// <summary>Jupyter notebook 的程式碼格，依序接起來，每格前面標「# ── 第 N 格 ──」。解析不了回空字串。</summary>
        internal static string NotebookCode(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
                    return "";

                var sb = new StringBuilder();
                int n = 0;
                foreach (var cell in cells.EnumerateArray())
                {
                    if (!cell.TryGetProperty("cell_type", out var type) || type.GetString() != "code") continue;
                    if (!cell.TryGetProperty("source", out var src)) continue;
                    string code = src.ValueKind switch
                    {
                        JsonValueKind.String => src.GetString() ?? "",
                        JsonValueKind.Array => string.Concat(src.EnumerateArray().Select(s => s.GetString() ?? "")),
                        _ => "",
                    };
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    sb.AppendLine($"# ── 第 {++n} 格 ──").AppendLine(code.TrimEnd());
                }
                return sb.ToString();
            }
            catch (JsonException)
            {
                return "";
            }
        }

        /// <summary>
        /// 送給審查模型的程式碼上限（字，所有程式檔合計）。只用來核對「報告說的做法」和「程式做的」是否一致。
        /// 2026-11-04：HW1 的審查看不到程式，漏了「報告說懲罰對各特徵公平，但 0/1 欄沒標準化」
        /// 和「用測試集 RMSE 選最佳模型」。
        /// </summary>
        internal const int MaxCodeChars = 15000;

        internal const int MaxProgramIssues = 30;

        // ── 程式檢查 ──────────────────────────────────────────────

        /// <summary>表格儲存格整格是這些值，就是沒算出來或沒處理好的值。</summary>
        private static readonly HashSet<string> InvalidCells = new(StringComparer.Ordinal)
        {
            "nan", "NaN", "NAN", "None", "null", "NULL", "inf", "-inf", "Infinity", "-Infinity",
            "#DIV/0!", "#N/A", "#VALUE!", "#REF!", "#NAME?", "#NUM!",
        };

        /// <summary>內文裡的無效值（只抓不會是正常英文字的寫法）。</summary>
        private static readonly Regex InvalidInText = new(
            @"(?<![A-Za-z])(nan|NaN|-?inf|#DIV/0!|#N/A|#VALUE!|#REF!)(?![A-Za-z])", RegexOptions.Compiled);

        /// <summary>沒有被程式填進去的模板：{variable}、{x:.2f}；以及明寫待補的字。</summary>
        private static readonly Regex Placeholder = new(
            @"\{[A-Za-z_][A-Za-z0-9_\.\[\]'""]*(?::[^{}\s]{1,12})?\}|請自行填入|待補充|待填入|\bTODO\b|\bTBD\b",
            RegexOptions.Compiled);

        /// <summary>
        /// 不經 AI 的檢查。reportMarkdown 是報告抽成的 Markdown（ArtifactTextExtractor.ExtractDocx），
        /// tables 是結果檔每個工作表的列（第一列為標題）。
        /// </summary>
        public static IReadOnlyList<Issue> ProgramChecks(
            string? reportMarkdown,
            IReadOnlyList<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>? tables = null)
        {
            var issues = new List<Issue>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(Issue i)
            {
                if (issues.Count < MaxProgramIssues && seen.Add(i.Where + "|" + i.Problem))
                    issues.Add(i);
            }

            int paragraph = 0, table = 0, row = 0;
            bool inTable = false;
            foreach (var raw in (reportMarkdown ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { inTable = false; continue; }
                if (line.StartsWith("![", StringComparison.Ordinal)) continue;   // 內嵌圖片

                if (line.StartsWith("|", StringComparison.Ordinal))
                {
                    if (!inTable) { table++; row = 0; inTable = true; }
                    if (Regex.IsMatch(line, @"^\|[\s:\-|]+\|$")) continue;      // 分隔列
                    row++;
                    var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToList();
                    foreach (var cell in cells.Where(c => InvalidCells.Contains(c)))
                        Add(new Issue("中", $"報告第 {table} 個表格第 {row} 列",
                            $"儲存格是「{cell}」，不是有效的值",
                            Shorten(line, 160),
                            "確認這格為什麼沒有算出來；若是刻意不適用，改成「—」並在表下說明原因。"));
                    foreach (Match m in Placeholder.Matches(line))
                        Add(new Issue("中", $"報告第 {table} 個表格第 {row} 列",
                            $"有沒填完的模板字「{m.Value}」", Shorten(line, 160), "換成實際內容或刪除。"));
                    continue;
                }

                inTable = false;
                paragraph++;
                string text = line.TrimStart('#', ' ', '-', '*');
                foreach (Match m in InvalidInText.Matches(text))
                    Add(new Issue("中", $"內文第 {paragraph} 段",
                        $"文字裡出現「{m.Value}」，不是有效的數字",
                        Around(text, m.Index, m.Length),
                        "換成實際數值，或說明為什麼無法計算。"));
                foreach (Match m in Placeholder.Matches(text))
                    Add(new Issue("中", $"內文第 {paragraph} 段",
                        $"有沒填完的模板字「{m.Value}」",
                        Around(text, m.Index, m.Length),
                        "換成實際內容或刪除。"));
            }

            foreach (var (name, rows) in tables ?? Array.Empty<(string, IReadOnlyList<IReadOnlyList<string>>)>())
            {
                for (int r = 0; r < rows.Count; r++)
                {
                    var cells = rows[r];
                    foreach (var cell in cells.Select(c => (c ?? "").Trim()).Where(c => InvalidCells.Contains(c)))
                        Add(new Issue("低", $"結果檔「{name}」第 {r + 1} 列",
                            $"儲存格是「{cell}」",
                            Shorten(string.Join(" | ", cells), 160),
                            "若報告引用了這一列，確認報告裡的寫法；結果檔本身可以保留。"));
                }
            }

            return issues;
        }

        // ── 審查模型 ──────────────────────────────────────────────

        public const string Instructions =
            "你是獨立的審查者。你沒有參與這份報告的撰寫，也不會修改它。你的工作只有四件：\n" +
            "1. 數字核對：報告文字裡提到的數字，是否和報告裡的表格、以及附上的結果檔一致（容許四捨五入）。\n" +
            "2. 前後矛盾：例如前面說 A 最好、後面說 B 最好；說「兩者結論不一定相同」，但列出的結果其實相同。\n" +
            "3. 無效或殘留內容：nan、None、空白欄位、沒填完的模板字句、明顯是程式拼接留下的句子（例如「影響為 改善」）。\n" +
            "4. 做法描述核對（有附程式碼時，任何程式語言都一樣）：報告描述的做法是否和程式實際做的一致，例如：" +
            "用了哪些資料、資料範圍與篩選條件；缺值、離群值與其他前處理怎麼做（含是否標準化、由哪部分資料決定）；" +
            "公式、計算方式、加權與單位；缺值數、刪除筆數與各數字是根據多少筆、哪個範圍的資料算的；" +
            "怎麼比較或挑選方案（例如用哪一份資料挑模型、挑參數、選「最佳模型」）。不一致時，evidence 要同時引用報告原文和程式片段，" +
            "寫成「報告：『…』／程式：『…』」。這一項只核對事實是否相符，不評論做法好不好。\n" +
            "\n" +
            "「報告」也包括簡報：簡報給的是逐張投影片的文字，位置用「投影片 N」表示。\n" +
            "\n" +
            "規則：\n" +
            "- 只回報有具體依據的問題，每一項都要引用報告、結果檔或程式碼裡的原文。找不到依據就不要報，寧可少報也不要猜。\n" +
            "- 不評論文筆、段落結構、分析方法的選擇，也不提出新的分析。第 4 項只問「報告說的」和「程式做的」是否相同。\n" +
            "- 每一項給一個具體的修改建議（改成什麼），不要改寫整段。\n" +
            "- 最多 15 項，依嚴重度排序：\n" +
            "  高＝影響結論或主要指標：主要結果的數字錯、結論互相矛盾、程式算錯而讓結果不可信（例如日期拆錯讓年齡變成上千年）、" +
            "報告描述的做法和程式不同而影響結論（例如說用交叉驗證選模型，程式其實用測試集；說排除了某些樣本或做了加權，程式其實沒有；公式或單位用錯）；\n" +
            "  中＝不影響結論的不一致：無效值、說明和表格對不上、只差一、兩筆的小數字不一致、其他做法描述和程式不符（報告寫的和程式做的不同）；\n" +
            "  低＝殘留字句、用字；以及報告漏寫、但沒有寫錯的做法細節（程式多做了一步，報告沒提到）。\n" +
            "- 姓名、學號、系級這類個人資料欄位是刻意留白、讓使用者自己填的，不算問題。\n" +
            "- 報告裡的「" + ImageMarker + "」是系統替換的記號，代表那裡有一張已經嵌入的圖片（圖片本身沒有附上）；" +
            "它不是殘留或沒填完的內容，不要回報。\n" +
            "- 下面「程式已找到的問題」已經列入清單，不要重複回報。\n" +
            "\n" +
            "只輸出 JSON，不要任何其他文字：\n" +
            "{\"issues\":[{\"severity\":\"高|中|低\",\"where\":\"位置（章節、表格或程式）\",\"problem\":\"問題\"," +
            "\"evidence\":\"引用的原文\",\"suggestion\":\"建議修改\"}],\"checked\":\"一句話說明你核對了哪些內容\"}";

        /// <summary>
        /// 審查模型收到的全文。codeFiles 是同一趟產出的程式碼（檔名、內容），只用來核對做法描述；
        /// 合計超過 MaxCodeChars 就截斷。
        /// </summary>
        public static string BuildPrompt(
            string reportName,
            string reportMarkdown,
            IReadOnlyList<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>? tables,
            IReadOnlyList<Issue>? programIssues,
            IReadOnlyList<(string Name, string Code)>? codeFiles = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Instructions).AppendLine();

            string report = WithoutImages(reportMarkdown ?? "");
            if (report.Length > MaxReportChars)
                report = report[..MaxReportChars] + "\n…（報告後段省略）";
            sb.AppendLine($"=== 報告：{reportName} ===").AppendLine(report).AppendLine();

            var tableText = new StringBuilder();
            foreach (var (name, rows) in tables ?? Array.Empty<(string, IReadOnlyList<IReadOnlyList<string>>)>())
            {
                tableText.AppendLine($"--- 結果檔：{name} ---");
                foreach (var r in rows)
                    tableText.AppendLine(string.Join(" | ", r));
            }
            if (tableText.Length > 0)
            {
                string t = tableText.ToString();
                if (t.Length > MaxTableChars) t = t[..MaxTableChars] + "\n…（以下省略）";
                sb.AppendLine("=== 程式輸出的結果檔（核對報告數字用） ===").AppendLine(t).AppendLine();
            }

            var codeText = new StringBuilder();
            foreach (var (name, code) in codeFiles ?? Array.Empty<(string, string)>())
            {
                if (string.IsNullOrWhiteSpace(code)) continue;
                codeText.AppendLine($"--- 程式：{name} ---").AppendLine(code.Replace("\r\n", "\n").TrimEnd());
            }
            if (codeText.Length > 0)
            {
                string c = codeText.ToString();
                if (c.Length > MaxCodeChars) c = c[..MaxCodeChars] + "\n…（程式後段省略）";
                sb.AppendLine("=== 程式碼（核對做法描述用） ===").AppendLine(c).AppendLine();
            }

            if (programIssues is { Count: > 0 })
            {
                sb.AppendLine("=== 程式已找到的問題（不要重複回報） ===");
                foreach (var i in programIssues)
                    sb.AppendLine($"- {i.Where}：{i.Problem}");
            }

            return sb.ToString();
        }

        /// <summary>解析審查模型的 JSON。解析不了回 null（呼叫端照實寫「審查回覆無法解析」，並附上原文）。</summary>
        public static (IReadOnlyList<Issue> Issues, string Checked)? ParseReply(string? reply)
        {
            string text = reply ?? "";
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;

            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                var root = doc.RootElement;
                var issues = new List<Issue>();
                if (root.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        string Get(string key) =>
                            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

                        string problem = Get("problem");
                        if (problem.Length == 0) continue;
                        string severity = Get("severity") is "高" or "中" or "低" ? Get("severity") : "中";
                        issues.Add(new Issue(severity, Get("where"), problem, Get("evidence"), Get("suggestion")));
                    }
                }

                string checkedText = root.TryGetProperty("checked", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()!.Trim() : "";
                return (Order(issues).Take(15).ToList(), checkedText);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ── 審查者的選擇 ──────────────────────────────────────────

        /// <summary>這家廠商有沒有設定金鑰。測試換成固定答案，結果才不會隨執行的電腦而變。</summary>
        public static Func<AiProviderKind, bool> HasKey { get; set; } = kind => kind switch
        {
            AiProviderKind.OpenAI => ApiKeyStore.HasKey("OPENAI_API_KEY"),
            AiProviderKind.Claude => ApiKeyStore.HasKey("ANTHROPIC_API_KEY"),
            _ => false,
        };

        /// <summary>
        /// 誰來審查。
        /// Auto：換一家廠商（撰寫者是 Claude 就由 GPT 審查，反之亦然），對方沒有金鑰才用同一家。
        /// 手動：只用使用者選的那個模型（2026-10-29 使用者指示：手動時不該有選擇之外的模型參與）。
        ///       同一個模型，但是另一次獨立的呼叫，看不到撰寫過程。
        /// 回傳 null＝不呼叫模型，只做程式檢查。
        /// </summary>
        public static (string? Model, string Note) PickReviewer(
            ModelScope scope, string? writerModel, Func<AiProviderKind, bool> hasKey)
        {
            if (scope.IsManual)
            {
                string? helper = scope.HelperModel(AiModels.OpenAi_Gpt56);
                return helper == null
                    ? (null, "手動模式選的模型不適合做審查，只做程式檢查")
                    : (helper, "手動模式只用你選的模型，所以和撰寫者是同一個模型；審查是另一次獨立的呼叫，看不到撰寫過程");
            }

            var writer = AiModelHelper.GetProviderKind(writerModel ?? "");
            if (writer == AiProviderKind.Claude && hasKey(AiProviderKind.OpenAI))
                return (AiModels.OpenAi_Gpt56, "和撰寫者不同家的模型，沒有參與撰寫");
            if (writer != AiProviderKind.Claude && hasKey(AiProviderKind.Claude))
                return (AiModels.Claude_Sonnet5, "和撰寫者不同家的模型，沒有參與撰寫");
            if (hasKey(AiProviderKind.Claude))
                return (AiModels.Claude_Sonnet5, "只有 Anthropic 金鑰，所以和撰寫者同一家；審查是另一次獨立的呼叫，看不到撰寫過程");
            if (hasKey(AiProviderKind.OpenAI))
                return (AiModels.OpenAi_Gpt56, "只有 OpenAI 金鑰，所以和撰寫者同一家；審查是另一次獨立的呼叫，看不到撰寫過程");
            return (null, "沒有可用的審查模型，只做程式檢查");
        }

        // ── 輸出 ──────────────────────────────────────────────────

        public static string ToMarkdown(string reportName, IReadOnlyList<string> tableNames, Result r)
        {
            var all = r.ModelIssues;
            int high = all.Count(i => i.Severity == "高");
            int mid = all.Count(i => i.Severity == "中");
            int low = all.Count(i => i.Severity == "低");

            var sb = new StringBuilder();
            sb.AppendLine($"# 獨立審查清單：{reportName}").AppendLine();
            sb.AppendLine($"- 審查者：{(string.IsNullOrEmpty(r.ReviewerModel) ? "（無，只做程式檢查）" : DisplayName(r.ReviewerModel))}。{r.ReviewerNote}。");
            sb.AppendLine($"- 審查內容：{reportName}" + (tableNames.Count > 0 ? "，對照 " + string.Join("、", tableNames) : "") + "。");
            if (!string.IsNullOrWhiteSpace(r.Checked))
                sb.AppendLine($"- 審查者說明：{r.Checked}");
            sb.AppendLine($"- 結果：審查模型找到 {all.Count} 項（高 {high}、中 {mid}、低 {low}），程式檢查找到 {r.ProgramIssues.Count} 項。");
            sb.AppendLine("- 這份清單只列出問題與建議，沒有修改報告。是否採用由你決定。");
            sb.AppendLine();

            sb.AppendLine("## 審查模型找到的問題").AppendLine();
            if (all.Count == 0)
                sb.AppendLine(string.IsNullOrEmpty(r.ReviewerModel)
                    ? "（這次沒有呼叫審查模型。）"
                    : string.IsNullOrEmpty(r.RawReply) || ParseReply(r.RawReply) != null
                        ? "沒有找到數字不符或前後矛盾的地方。"
                        : "審查回覆無法解析，原文附在最後。");
            else
                AppendTable(sb, all, withSeverity: true);

            sb.AppendLine().AppendLine("## 程式自動檢查（不經 AI）").AppendLine();
            sb.AppendLine("檢查無效值（nan、None、#DIV/0! 等）與沒填完的模板字。").AppendLine();
            if (r.ProgramIssues.Count == 0)
                sb.AppendLine("沒有找到。");
            else
                AppendTable(sb, r.ProgramIssues, withSeverity: false);

            if (all.Count == 0 && !string.IsNullOrWhiteSpace(r.RawReply) && ParseReply(r.RawReply) == null)
                sb.AppendLine().AppendLine("## 審查者原始回覆").AppendLine().AppendLine(Shorten(r.RawReply, 4000));

            return sb.ToString();
        }

        /// <summary>接在答案最後的一句話。</summary>
        public static string Summary(Result r)
        {
            int count = r.ModelIssues.Count + r.ProgramIssues.Count;
            int high = r.ModelIssues.Count(i => i.Severity == "高");
            string who = string.IsNullOrEmpty(r.ReviewerModel)
                ? "程式檢查"
                : $"{DisplayName(r.ReviewerModel)}，沒有參與撰寫";
            return count == 0
                ? $"獨立審查（{who}）：沒有找到數字不符或前後矛盾的地方。"
                : $"獨立審查（{who}）：找到 {count} 項需要確認的地方" + (high > 0 ? $"（其中 {high} 項較嚴重）" : "") +
                  "，修改清單在「審查清單」檔，報告本身沒有被修改。";
        }

        private static void AppendTable(StringBuilder sb, IReadOnlyList<Issue> issues, bool withSeverity)
        {
            sb.AppendLine(withSeverity
                ? "| # | 嚴重度 | 位置 | 問題 | 依據（原文） | 建議修改 |\n|---|---|---|---|---|---|"
                : "| # | 位置 | 問題 | 原文 | 建議 |\n|---|---|---|---|---|");
            int n = 0;
            foreach (var i in issues)
            {
                n++;
                sb.AppendLine(withSeverity
                    ? $"| {n} | {Cell(i.Severity)} | {Cell(i.Where)} | {Cell(i.Problem)} | {Cell(i.Evidence)} | {Cell(i.Suggestion)} |"
                    : $"| {n} | {Cell(i.Where)} | {Cell(i.Problem)} | {Cell(i.Evidence)} | {Cell(i.Suggestion)} |");
            }
        }

        private static IEnumerable<Issue> Order(IEnumerable<Issue> issues)
            => issues.OrderBy(i => i.Severity switch { "高" => 0, "中" => 1, _ => 2 });

        private static string Cell(string? s)
            => (s ?? "").Replace("\r", "").Replace("\n", " ").Replace("|", "／").Trim();

        private static string DisplayName(string model)
        {
            try { return AiModelHelper.GetDefinition(model).DisplayName; }
            catch { return model; }
        }

        /// <summary>
        /// 送審時，報告裡內嵌的圖片換成這個記號（圖片不送給審查模型）。
        /// 2026-11-04：HW1 審查沒被告知這個記號的意思，把每張圖都當成「殘留的佔位字」回報。
        /// </summary>
        internal const string ImageMarker = "［圖］";

        internal static string WithoutImages(string markdown)
            => Regex.Replace(markdown, @"!\[[^\]]*\]\(data:[^)]*\)", ImageMarker);

        private static string Around(string text, int index, int length)
        {
            int from = Math.Max(0, index - 30);
            int to = Math.Min(text.Length, index + length + 30);
            return (from > 0 ? "…" : "") + text[from..to] + (to < text.Length ? "…" : "");
        }

        private static string Shorten(string text, int max)
            => text.Length <= max ? text : text[..max] + "…";
    }
}
