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

        /// <summary>
        /// 審查模型這一次有沒有真的完成。只有 Completed 才能說「沒有找到問題」。
        /// 2026-11-04（GPT 審查本專案）：空白回覆、非 JSON、{} 都被當成零問題，摘要寫「沒有找到」——
        /// 使用者會以為報告已通過核對，自動修正後的再審查也一樣。
        /// </summary>
        public enum ReviewStatus
        {
            /// <summary>審查模型回了可解析、含 issues 陣列的 JSON。</summary>
            Completed,
            /// <summary>沒有呼叫審查模型（沒有可用模型、超過預算），只做程式檢查。</summary>
            NotRun,
            /// <summary>呼叫了但沒有回應（逾時、錯誤）。</summary>
            Failed,
            /// <summary>有回覆，但不是含 issues 陣列的 JSON。</summary>
            Unparseable,
            /// <summary>
            /// issues 陣列裡有項目讀不出問題內容（不是物件、也沒有任何可當問題的欄位）。
            /// 讀得出的照列，讀不出的附原文；不能當成「審查完成」，更不能說零問題。
            /// 2026-11-04（GPT 審查本專案）：{"issues":["營收單位錯誤"]}、用 description 不用 problem 的回覆，
            /// 原本都被略過，解析成 0 項，摘要寫「沒有找到問題」。
            /// </summary>
            PartiallyParsed,
        }

        /// <summary>上一輪的問題在這一輪的處理狀態（自動修正後的再審查用）：已修正、仍存在、無法確認。</summary>
        public sealed record PreviousStatus(int N, string Problem, string Status, string Evidence);

        public sealed record Result(
            IReadOnlyList<Issue> ModelIssues,
            IReadOnlyList<Issue> ProgramIssues,
            string ReviewerModel,
            string ReviewerNote,
            string Checked,
            string RawReply,
            ReviewStatus Status = ReviewStatus.Completed,
            IReadOnlyList<string>? Coverage = null,
            IReadOnlyList<PreviousStatus>? Previous = null,
            IReadOnlyList<string>? Origins = null)
        {
            /// <summary>
            /// 再審查時每個新問題引用的內容是新是舊（和 ModelIssues 一一對應）：OriginBefore／OriginAfter／OriginUnknown。
            /// 由程式比對原文判斷（見 ReviewAutoFix.ClassifyOrigins），不是審查模型說的。沒有對應時為 null。
            /// </summary>
            public IReadOnlyList<string>? IssueOrigins =>
                Origins != null && Origins.Count == ModelIssues.Count ? Origins : null;

            /// <summary>
            /// 實際狀態。標成 Completed 但沒有審查者，當成沒有執行；有回覆卻解析不了，當成無法解析——
            /// 不管呼叫端有沒有帶狀態，都不能把這兩種情況說成零問題。
            /// </summary>
            public ReviewStatus EffectiveStatus =>
                Status != ReviewStatus.Completed ? Status
                : string.IsNullOrEmpty(ReviewerModel) ? ReviewStatus.NotRun
                : string.IsNullOrWhiteSpace(RawReply) ? ReviewStatus.Completed
                : ParseReply(RawReply) is not { } p ? ReviewStatus.Unparseable
                : p.Unparsed > 0 ? ReviewStatus.PartiallyParsed
                : ReviewStatus.Completed;

            /// <summary>審查回覆的 issues 裡有幾項讀不出問題內容（見 ReviewStatus.PartiallyParsed）。</summary>
            public int UnparsedCount =>
                string.IsNullOrWhiteSpace(RawReply) ? 0 : ParseReply(RawReply)?.Unparsed ?? 0;

            public IReadOnlyList<string> CoverageNotes => Coverage ?? Array.Empty<string>();
        }

        public const string StatusFixed = "已修正";
        public const string StatusRemains = "仍存在";
        public const string StatusUnknown = "無法確認";

        /// <summary>
        /// 再審查新發現的問題，它引用的原文是新是舊（程式比對字串）。
        /// 只說字串比對證明得了的事：引用的文字修正前就有，不代表問題修正前就有——
        /// 結論那句沒改、但它對照的數字改了，矛盾就是修正後才形成的（2026-11-04 GPT 審查本專案的反例）。
        /// 原本標成「修正前就存在（上一輪漏看）」「修正後才出現」，說得比證據更肯定。
        /// </summary>
        public const string OriginBefore = "引用內容修正前已存在";
        public const string OriginAfter = "引用內容為修正後新增";
        public const string OriginUnknown = "無法判斷";

        /// <summary>送給審查模型的報告全文上限（字）。一般報告一萬多字，留餘裕。</summary>
        internal const int MaxReportChars = 30000;

        /// <summary>
        /// 送給審查模型的結果檔上限（字，所有工作表合計）。報告裡多半已有同樣的表，結果檔只是對照用。
        /// 2026-11-04：原本 1 萬字，HW1 的結果表只送了 27%——被一張 580 列的逐筆預測表佔掉。
        /// 現在逐筆資料表先只取前 MaxRowsPerSheet 列（見 TrimSheet），摘要型的工作表就放得下，上限也放寬到 2 萬字。
        /// </summary>
        internal const int MaxTableChars = 20000;

        /// <summary>
        /// 一張工作表最多送幾列（不含標題列）。超過的多半是逐筆資料（每筆測試案件的預測、誤差），
        /// 報告只會引用其中幾筆或統計值，送前幾十列抽查就夠；被截的列數寫進「檢查範圍」。
        /// </summary>
        internal const int MaxRowsPerSheet = 30;

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
        /// 同日再放寬：原本 1.5 萬字，HW1 的程式約 3.5 萬字，只送了前 42%——產生報告的後半段
        /// （報告怎麼描述做法、表格怎麼排序）審查都看不到。4 萬字約 1.5 萬 token，一般作業的程式放得下。
        /// </summary>
        internal const int MaxCodeChars = 60000;

        /// <summary>再審查時附上的撰寫者說明最多幾字（逐項處理結果，通常幾行）。</summary>
        internal const int MaxWriterNoteChars = 2000;

        internal const int MaxProgramIssues = 30;

        // ── 程式檢查 ──────────────────────────────────────────────

        /// <summary>
        /// 新版套件已經移除、照寫會直接報錯的寫法（只收誤判機率低的）。
        /// 2026-11-04（GPT 審查 HW1）：LassoCV(n_alphas=60) 在沙箱的舊版跑得過，scikit-learn 1.9 已移除這個參數，
        /// 老師的電腦如果是新版，程式跑到建模就當掉。沙箱版本太舊不會出棄用警告，撰寫模型無從得知，所以用程式檢查。
        /// </summary>
        internal static readonly (Regex Pattern, string Problem, string Suggestion)[] RemovedApis =
        {
            (new Regex(@"\bn_alphas\s*=", RegexOptions.Compiled),
                "用了 n_alphas 參數：scikit-learn 1.7 起淘汰、1.9 已移除（LassoCV、ElasticNetCV 等），較新的環境會直接報錯",
                "改成傳入明確的 alpha 陣列，例如 alphas=np.logspace(-4, 1, 60)，新舊版本都能執行"),
            (new Regex(@"\bsquared\s*=\s*False", RegexOptions.Compiled),
                "用了 mean_squared_error(..., squared=False)：scikit-learn 1.6 已移除 squared 參數，較新的環境會直接報錯",
                "改成 np.sqrt(mean_squared_error(y, p))，新舊版本都能執行"),
            (new Regex(@"OneHotEncoder\([^)]*\bsparse\s*=", RegexOptions.Compiled),
                "OneHotEncoder 用了 sparse 參數：scikit-learn 1.4 已移除，較新的環境會直接報錯",
                "改成 sparse_output=False"),
            (new Regex(@"\.iteritems\(", RegexOptions.Compiled),
                "用了 .iteritems()：pandas 2.0 已移除，較新的環境會直接報錯",
                "改成 .items()"),
            // \b 讓 np.float64、np.int_、np.str_ 這些仍可用的寫法不會被抓到
            (new Regex(@"\bnp\.(float|int|bool|object|str)\b", RegexOptions.Compiled),
                "用了 np.float／np.int 這類別名：NumPy 1.24 已移除，較新的環境會直接報錯",
                "改成 Python 內建的 float、int，或 np.float64、np.int64"),
        };

        /// <summary>
        /// 程式碼檢查（不經 AI）：找出新版套件已移除的寫法，列在審查清單；自動修正開著時也會一起修。
        /// 只是字串比對，所以只收誤判機率低的寫法（見 RemovedApis）。
        /// </summary>
        public static IReadOnlyList<Issue> CodeChecks(IReadOnlyList<(string Name, string Code)>? codeFiles)
        {
            var issues = new List<Issue>();
            foreach (var (name, code) in codeFiles ?? Array.Empty<(string, string)>())
            {
                var lines = (code ?? "").Replace("\r\n", "\n").Split('\n');
                foreach (var (pattern, problem, suggestion) in RemovedApis)
                {
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("#", StringComparison.Ordinal) || !pattern.IsMatch(line)) continue;
                        issues.Add(new Issue("中", $"程式「{name}」第 {i + 1} 行", problem, Shorten(trimmed, 160), suggestion));
                        break;   // 同一種寫法每支程式報一次就好
                    }
                    if (issues.Count >= MaxProgramIssues) return issues;
                }
            }
            return issues;
        }

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

        /// <summary>送審內容的三個部分各自實際送出的文字（超過上限已截斷）與被截掉的字數。BuildPrompt 與 Coverage 共用，兩邊才會一致。</summary>
        private sealed record PromptParts(
            string Report, int ReportCut, string Tables, int TablesCut, string Code, int CodeCut,
            IReadOnlyList<(string Sheet, int Rows, int Sent)> TrimmedSheets);

        private static PromptParts Parts(
            string reportMarkdown,
            IReadOnlyList<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>? tables,
            IReadOnlyList<(string Name, string Code)>? codeFiles,
            IReadOnlyDictionary<string, int>? totalRows = null)
        {
            string report = WithoutImages(reportMarkdown ?? "");
            int reportCut = Math.Max(0, report.Length - MaxReportChars);
            if (reportCut > 0) report = report[..MaxReportChars] + "\n…（報告後段省略）";

            // 結果表：放得下就整份送；放不下時，從列數最多的表開始截成標題列＋MaxRowsPerSheet 列，直到放得下。
            // 2026-11-04 第 4 次實測：一律截到 30 列，連 51 列的係數表也被截掉，其實整份都放得下。
            // 「共幾列」用 totalRows 的實際數字（讀檔只讀前 200 列；當時把讀到的 199 列說成「共 199 列」，實際 581 列）。
            var sheetList = (tables ?? Array.Empty<(string, IReadOnlyList<IReadOnlyList<string>>)>()).ToList();
            int TotalData(int i) => Math.Max(0,
                (totalRows != null && totalRows.TryGetValue(sheetList[i].Name, out int n) ? Math.Max(n, sheetList[i].Rows.Count) : sheetList[i].Rows.Count) - 1);
            var keep = sheetList.Select(s => s.Rows.Count).ToArray();

            string Render(int i)
            {
                var (name, rows) = sheetList[i];
                var sb = new StringBuilder();
                sb.AppendLine($"--- 結果檔：{name} ---");
                for (int r = 0; r < keep[i]; r++)
                    sb.AppendLine(string.Join(" | ", rows[r]));
                int sent = Math.Max(0, keep[i] - 1), total = TotalData(i);
                if (sent < total)
                    sb.AppendLine($"…（這張表共 {total:N0} 列，只送前 {sent:N0} 列）");
                return sb.ToString();
            }

            int Length() => Enumerable.Range(0, sheetList.Count).Sum(i => Render(i).Length);
            if (Length() > MaxTableChars)
            {
                foreach (int i in Enumerable.Range(0, sheetList.Count).OrderByDescending(i => sheetList[i].Rows.Count))
                {
                    if (keep[i] <= MaxRowsPerSheet + 1) continue;
                    keep[i] = MaxRowsPerSheet + 1;
                    if (Length() <= MaxTableChars) break;
                }
            }

            var trimmed = new List<(string Sheet, int Rows, int Sent)>();
            var tableText = new StringBuilder();
            for (int i = 0; i < sheetList.Count; i++)
            {
                tableText.Append(Render(i));
                int sent = Math.Max(0, keep[i] - 1), total = TotalData(i);
                if (sent < total) trimmed.Add((sheetList[i].Name, total, sent));
            }
            string t = tableText.ToString();
            int tablesCut = Math.Max(0, t.Length - MaxTableChars);
            if (tablesCut > 0) t = t[..MaxTableChars] + "\n…（以下省略）";

            var codeText = new StringBuilder();
            foreach (var (name, code) in codeFiles ?? Array.Empty<(string, string)>())
            {
                if (string.IsNullOrWhiteSpace(code)) continue;
                codeText.AppendLine($"--- 程式：{name} ---").AppendLine(code.Replace("\r\n", "\n").TrimEnd());
            }
            string c = codeText.ToString();
            int codeCut = Math.Max(0, c.Length - MaxCodeChars);
            if (codeCut > 0) c = c[..MaxCodeChars] + "\n…（程式後段省略）";

            return new PromptParts(report, reportCut, t, tablesCut, c, codeCut, trimmed);
        }

        /// <summary>
        /// 檢查範圍：哪些內容因為超過上限沒有送審、哪些交付檔沒有送審。空清單＝全部送審。
        /// 2026-11-04（GPT 審查本專案）：截斷只寫在送給模型的提示裡，使用者看不到，會以為整批交付都檢查過。
        /// </summary>
        public static IReadOnlyList<string> Coverage(
            string reportMarkdown,
            IReadOnlyList<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>? tables,
            IReadOnlyList<(string Name, string Code)>? codeFiles,
            IReadOnlyList<string>? unreviewedFiles = null,
            IReadOnlyDictionary<string, int>? totalRows = null,
            IReadOnlyList<string>? skippedSheets = null)
        {
            var p = Parts(reportMarkdown, tables, codeFiles, totalRows);
            var notes = new List<string>();
            if (p.ReportCut > 0)
                notes.Add($"報告超過 {MaxReportChars:N0} 字，最後約 {p.ReportCut:N0} 字沒有送審。");
            foreach (var (sheet, rows, sent) in p.TrimmedSheets)
                notes.Add($"結果表「{sheet}」共 {rows:N0} 列，只送了前 {sent:N0} 列（放不下時，列數多的表只抽查前段）。");
            foreach (var s in skippedSheets ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(s))
                    notes.Add($"結果表的工作表「{s}」沒有送審（工作表張數超過讀取上限）。");
            if (p.TablesCut > 0)
                notes.Add($"結果表合計超過 {MaxTableChars:N0} 字，後面約 {p.TablesCut:N0} 字（排在後面的工作表）沒有送審。");
            if (p.CodeCut > 0)
                notes.Add($"程式碼合計超過 {MaxCodeChars:N0} 字，後面約 {p.CodeCut:N0} 字沒有送審，報告描述的做法只核對了前段。");
            foreach (var f in unreviewedFiles ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(f))
                    notes.Add($"「{f}」沒有送審（這一趟只審一份報告或簡報）。");
            return notes;
        }

        /// <summary>
        /// 審查模型收到的全文。codeFiles 是同一趟產出的程式碼（檔名、內容），只用來核對做法描述；
        /// 合計超過 MaxCodeChars 就截斷。
        /// previousIssues：自動修正後的再審查用，上一輪的問題沿用同樣的編號，要求逐項回報處理狀態。
        /// </summary>
        public static string BuildPrompt(
            string reportName,
            string reportMarkdown,
            IReadOnlyList<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>? tables,
            IReadOnlyList<Issue>? programIssues,
            IReadOnlyList<(string Name, string Code)>? codeFiles = null,
            IReadOnlyList<Issue>? previousIssues = null,
            string? writerNote = null,
            IReadOnlyDictionary<string, int>? totalRows = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Instructions).AppendLine();

            var p = Parts(reportMarkdown, tables, codeFiles, totalRows);
            sb.AppendLine($"=== 報告：{reportName} ===").AppendLine(p.Report).AppendLine();

            if (p.Tables.Length > 0)
                sb.AppendLine("=== 程式輸出的結果檔（核對報告數字用） ===").AppendLine(p.Tables).AppendLine();

            if (p.Code.Length > 0)
                sb.AppendLine("=== 程式碼（核對做法描述用） ===").AppendLine(p.Code).AppendLine();

            if (programIssues is { Count: > 0 })
            {
                sb.AppendLine("=== 程式已找到的問題（不要重複回報） ===");
                foreach (var i in programIssues)
                    sb.AppendLine($"- {i.Where}：{i.Problem}");
                sb.AppendLine();
            }

            if (previousIssues is { Count: > 0 })
            {
                sb.AppendLine("=== 上一輪審查的問題（這份報告是針對它們修正後的版本，請逐項確認） ===");
                int n = 0;
                foreach (var i in previousIssues)
                {
                    n++;
                    sb.AppendLine($"{n}. ［{i.Severity}］{i.Where}：{i.Problem}");
                    if (!string.IsNullOrWhiteSpace(i.Evidence)) sb.AppendLine($"   當時的依據：{i.Evidence}");
                }
                sb.AppendLine();

                // 撰寫者可能查證後認為某一項不存在而沒改（2026-11-04 第 4 次實測：審查被錯的列數誤導，
                // 撰寫者查證後拒絕修改，再審查卻看不到這段理由，又判「仍存在」）。附上說明，讓再審查自己判斷誰對。
                string note = (writerNote ?? "").Trim();
                if (note.Length > 0)
                {
                    if (note.Length > MaxWriterNoteChars) note = note[..MaxWriterNoteChars] + "…";
                    sb.AppendLine("=== 撰寫者對各項的處理說明（僅供參考，不一定正確；請用目前的檔案自己查證） ===");
                    sb.AppendLine(note).AppendLine();
                    sb.AppendLine("撰寫者說某一項「問題不存在」時，請核對它的理由：理由成立就標「" + StatusFixed +
                                  "」並在 evidence 說明原本的問題不成立；理由不成立就標「" + StatusRemains + "」並引用反證。");
                }

                sb.AppendLine("對上面每一項，用目前的報告、結果檔與程式判斷：「" + StatusFixed + "」「" + StatusRemains + "」或「" + StatusUnknown +
                              "」（附上的內容不足以判斷時）。每一項都要回覆，evidence 引用目前的原文。");
                sb.AppendLine("回覆的 JSON 另外加上 \"previous\":[{\"n\":編號,\"status\":\"" + StatusFixed + "|" + StatusRemains + "|" + StatusUnknown +
                              "\",\"evidence\":\"依據的原文\"}]。");
                sb.AppendLine("仍存在的問題只在 previous 標「" + StatusRemains + "」，不要在 issues 重複列；issues 只列這次新發現的問題。");
            }

            return sb.ToString();
        }

        /// <summary>問題內容可能放在這些欄位（模型偶爾不用規定的 problem）。依序取第一個有字的。</summary>
        private static readonly string[] ProblemKeys = { "problem", "description", "issue", "text", "message", "content" };

        /// <summary>格式不符、但讀得出內容的問題，位置欄寫這個（嚴重度不明時一律當「中」）。</summary>
        internal const string SalvagedWhere = "（回覆格式不符，未註明位置）";

        /// <summary>
        /// 解析審查模型的 JSON。必須有 issues 陣列才算解析成功（空陣列＝確實沒有問題）；
        /// 解析不了回 null（呼叫端標成 Unparseable，照實寫「審查回覆無法解析」並附上原文）。
        /// 2026-11-04：原本 {} 也被接受，摘要變成「沒有找到問題」。
        ///
        /// 陣列裡的項目：意思讀得出來的就救回來——字串項目當成問題內容、problem 以外的同義欄位（description 等）也認；
        /// 真的讀不出問題內容的（數字、空物件、沒有任何文字欄位）計入 Unparsed，呼叫端標成 PartiallyParsed。
        /// 2026-11-04（GPT 審查本專案）：原本這兩種都直接略過，模型回報了問題，解析結果卻是 0 項。
        /// </summary>
        public static (IReadOnlyList<Issue> Issues, string Checked, int Unparsed)? ParseReply(string? reply)
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
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("issues", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return null;

                var issues = new List<Issue>();
                int unparsed = 0;
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String)
                    {
                        // 只給一句話的問題：照列，位置與嚴重度不明
                        string only = e.GetString()!.Trim();
                        if (only.Length > 0) issues.Add(new Issue("中", SalvagedWhere, only, "", ""));
                        else unparsed++;
                        continue;
                    }
                    if (e.ValueKind != JsonValueKind.Object) { unparsed++; continue; }

                    string Get(string key) =>
                        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

                    string problem = ProblemKeys.Select(Get).FirstOrDefault(s => s.Length > 0) ?? "";
                    if (problem.Length == 0) { unparsed++; continue; }
                    string severity = Get("severity") is "高" or "中" or "低" ? Get("severity") : "中";
                    issues.Add(new Issue(severity, Get("where"), problem, Get("evidence"), Get("suggestion")));
                }

                string checkedText = root.TryGetProperty("checked", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()!.Trim() : "";
                return (Order(issues).Take(15).ToList(), checkedText, unparsed);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// 解析再審查回覆裡的 previous（上一輪每一項的處理狀態）。每一個上一輪的問題都會有一列：
        /// 審查者沒回覆、編號不對或狀態不是三種之一的，一律記成「無法確認」——不會因為沒回就當成已修正。
        /// </summary>
        public static IReadOnlyList<PreviousStatus> ParsePrevious(string? reply, IReadOnlyList<Issue> previousIssues)
        {
            var found = new Dictionary<int, (string Status, string Evidence)>();
            string text = reply ?? "";
            int start = text.IndexOf('{'), end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try
                {
                    using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("previous", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in arr.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.Object) continue;
                            if (!e.TryGetProperty("n", out var nEl) || !nEl.TryGetInt32(out int n)) continue;
                            string status = e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.Trim() : "";
                            string evidence = e.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString()!.Trim() : "";
                            if (!(status is StatusFixed or StatusRemains or StatusUnknown) || found.ContainsKey(n)) continue;
                            // 「已修正」「仍存在」都要有依據才算數；沒附依據的改記「無法確認」——
                            // 2026-11-04（GPT 審查本專案）：{"status":"已修正","evidence":""} 原本照單全收。
                            if (evidence.Length == 0 && status != StatusUnknown)
                                found[n] = (StatusUnknown, $"審查者判定為「{status}」，但沒有附依據");
                            else
                                found[n] = (status, evidence);
                        }
                    }
                }
                catch (JsonException) { }
            }

            var list = new List<PreviousStatus>();
            for (int i = 0; i < (previousIssues?.Count ?? 0); i++)
            {
                int n = i + 1;
                list.Add(found.TryGetValue(n, out var f)
                    ? new PreviousStatus(n, previousIssues![i].Problem, f.Status, f.Evidence)
                    : new PreviousStatus(n, previousIssues![i].Problem, StatusUnknown, "審查者沒有回覆這一項"));
            }
            return list;
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
            var status = r.EffectiveStatus;
            sb.AppendLine(status switch
            {
                ReviewStatus.Completed =>
                    $"- 結果：審查模型找到 {all.Count} 項（高 {high}、中 {mid}、低 {low}），程式檢查找到 {r.ProgramIssues.Count} 項。",
                ReviewStatus.PartiallyParsed =>
                    $"- 結果：審查模型找到 {all.Count} 項（高 {high}、中 {mid}、低 {low}），另有 {r.UnparsedCount} 項回覆格式無法解析（原文附在最後）；" +
                    $"程式檢查找到 {r.ProgramIssues.Count} 項。**格式無法解析的部分沒有列入，這不代表報告已通過核對。**",
                _ =>
                    $"- 結果：{StatusText(status)}，只有程式檢查的結果（找到 {r.ProgramIssues.Count} 項）。**這不代表報告已通過數字與文字核對。**",
            });
            sb.AppendLine("- 這份清單只列出問題與建議，沒有修改報告。是否採用由你決定。");
            sb.AppendLine();

            sb.AppendLine("## 檢查範圍").AppendLine();
            if (r.CoverageNotes.Count == 0)
                sb.AppendLine("上面列出的檔案都完整送審，沒有截斷。");
            else
                foreach (var note in r.CoverageNotes) sb.AppendLine("- " + note);
            sb.AppendLine();

            if (r.Previous is { Count: > 0 })
            {
                sb.AppendLine("## 上一輪問題的處理狀態").AppendLine();
                sb.AppendLine($"已修正 {r.Previous.Count(p => p.Status == StatusFixed)} 項、仍存在 {r.Previous.Count(p => p.Status == StatusRemains)} 項、" +
                              $"無法確認 {r.Previous.Count(p => p.Status == StatusUnknown)} 項（編號和「修正說明」裡的第 n 項相同）。" +
                              "狀態是審查模型的判定，請對照依據；沒有附依據的一律記成「" + StatusUnknown + "」。").AppendLine();
                sb.AppendLine("| # | 上一輪的問題 | 狀態（審查模型判定） | 依據 |\n|---|---|---|---|");
                foreach (var p in r.Previous)
                    sb.AppendLine($"| {p.N} | {Cell(p.Problem)} | {Cell(p.Status)} | {Cell(p.Evidence)} |");
                sb.AppendLine();
            }

            sb.AppendLine(r.Previous is { Count: > 0 } ? "## 這一輪新發現的問題" : "## 審查模型找到的問題").AppendLine();
            bool partial = status == ReviewStatus.PartiallyParsed;
            if (status != ReviewStatus.Completed && !partial)
                sb.AppendLine($"（{StatusText(status)}：{r.ReviewerNote}。）" +
                              (status == ReviewStatus.Unparseable ? "原文附在最後。" : ""));
            else if (all.Count == 0)
                sb.AppendLine(partial
                    ? $"（可解析的部分沒有列出問題；另有 {r.UnparsedCount} 項回覆格式無法解析，原文附在最後。這不代表沒有問題。）"
                    : "沒有找到數字不符或前後矛盾的地方。");
            else
            {
                if (partial)
                    sb.AppendLine($"（另有 {r.UnparsedCount} 項回覆格式無法解析，沒有列在下表，原文附在最後。）").AppendLine();
                var origins = r.IssueOrigins;
                if (origins != null)
                    sb.AppendLine($"「引用內容」欄由程式比對問題引用的原文：{OriginBefore} {origins.Count(o => o == OriginBefore)} 項、" +
                                  $"{OriginAfter} {origins.Count(o => o == OriginAfter)} 項、{OriginUnknown} {origins.Count(o => o == OriginUnknown)} 項。" +
                                  "這只表示引用的文字是新是舊，不代表問題是什麼時候形成的——例如結論那句沒改、但它對照的數字改了，矛盾就是修正後才出現。").AppendLine();
                AppendTable(sb, all, withSeverity: true, origins);
            }

            sb.AppendLine().AppendLine("## 程式自動檢查（不經 AI）").AppendLine();
            sb.AppendLine("檢查無效值（nan、None、#DIV/0! 等）、沒填完的模板字，以及程式裡新版套件已移除、會直接報錯的寫法。").AppendLine();
            if (r.ProgramIssues.Count == 0)
                sb.AppendLine("沒有找到。");
            else
                AppendTable(sb, r.ProgramIssues, withSeverity: false);

            if ((status == ReviewStatus.Unparseable || partial) && !string.IsNullOrWhiteSpace(r.RawReply))
                sb.AppendLine().AppendLine("## 審查者原始回覆").AppendLine().AppendLine(Shorten(r.RawReply, 4000));

            return sb.ToString();
        }

        /// <summary>審查模型沒有完成時的說法（清單與摘要共用）。</summary>
        public static string StatusText(ReviewStatus status) => status switch
        {
            ReviewStatus.NotRun => "這次沒有呼叫審查模型",
            ReviewStatus.Failed => "審查模型這次沒有回應",
            ReviewStatus.Unparseable => "審查回覆無法解析",
            ReviewStatus.PartiallyParsed => "審查回覆有部分格式無法解析",
            _ => "審查完成",
        };

        /// <summary>
        /// 接在答案最後的一句話。只有審查模型真的完成時才說「沒有找到」；
        /// 沒執行、失敗、無法解析都照實寫，並說明這不代表通過核對。
        /// reportUnchanged＝false：自動修正已把外面的報告換成修正版，不能再說「報告本身沒有被修改」。
        /// </summary>
        public static string Summary(Result r, bool reportUnchanged = true)
        {
            int count = r.ModelIssues.Count + r.ProgramIssues.Count;
            int high = r.ModelIssues.Count(i => i.Severity == "高");
            string coverage = r.CoverageNotes.Count > 0 ? "部分內容沒有送審，見清單的「檢查範圍」。" : "";
            var status = r.EffectiveStatus;

            bool partial = status == ReviewStatus.PartiallyParsed;
            if (status != ReviewStatus.Completed && !partial)
                return $"獨立審查：{StatusText(status)}（{r.ReviewerNote}），只做了程式檢查" +
                       (r.ProgramIssues.Count > 0 ? $"，找到 {r.ProgramIssues.Count} 項，清單在「審查清單」檔" : "，沒有找到無效值或殘留字") +
                       "；這不代表報告已通過數字與文字核對。" + coverage;

            string who = $"{DisplayName(r.ReviewerModel)}，沒有參與撰寫";
            string partialNote = partial
                ? $"另有 {r.UnparsedCount} 項審查回覆格式無法解析（原文在清單裡），這部分沒有核對結果。"
                : "";
            if (count == 0)
                return partial
                    ? $"獨立審查（{who}）：審查回覆有 {r.UnparsedCount} 項格式無法解析，沒有可列出的問題；這不代表報告已通過數字與文字核對。" + coverage
                    : $"獨立審查（{who}）：沒有找到數字不符或前後矛盾的地方。" + coverage;
            return $"獨立審查（{who}）：找到 {count} 項需要確認的地方" + (high > 0 ? $"（其中 {high} 項較嚴重）" : "") +
                   (reportUnchanged ? "，修改清單在「審查清單」檔，報告本身沒有被修改。" : "。") + partialNote + coverage;
        }

        private static void AppendTable(StringBuilder sb, IReadOnlyList<Issue> issues, bool withSeverity,
            IReadOnlyList<string>? origins = null)
        {
            bool withOrigin = withSeverity && origins != null && origins.Count == issues.Count;
            sb.AppendLine(withOrigin
                ? "| # | 嚴重度 | 引用內容 | 位置 | 問題 | 依據（原文） | 建議修改 |\n|---|---|---|---|---|---|---|"
                : withSeverity
                    ? "| # | 嚴重度 | 位置 | 問題 | 依據（原文） | 建議修改 |\n|---|---|---|---|---|---|"
                    : "| # | 位置 | 問題 | 原文 | 建議 |\n|---|---|---|---|---|");
            int n = 0;
            foreach (var i in issues)
            {
                n++;
                sb.AppendLine(withOrigin
                    ? $"| {n} | {Cell(i.Severity)} | {Cell(origins![n - 1])} | {Cell(i.Where)} | {Cell(i.Problem)} | {Cell(i.Evidence)} | {Cell(i.Suggestion)} |"
                    : withSeverity
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
