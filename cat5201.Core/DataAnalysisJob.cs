using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 附了資料檔、又要交報告／表格的任務：在 Claude 的沙箱裡實際執行程式，交出有真實結果的檔案。
    ///
    /// 2026-11-03 機器學習作業：使用者附上 115Taipei.csv 與作業範本，要交「作業檔案」和「程式碼檔案」。
    /// 原本的流程只能交出「一支要使用者自己跑的程式」加上沒有數字的說明：
    /// 系統不會執行程式，而且 1.2 MB 的 CSV 被截到前 6 萬字。
    /// 那一趟還花了約 US$2.4、6.5 分鐘——主回覆每一輪續寫都重送整份 CSV（約 11 萬 token），
    /// 報告再由 Claude 另外寫一次，Word 技能又自己注入 159 萬 token。
    ///
    /// 這條路一次做完：資料檔整份放進沙箱（不截斷），Claude 讀資料、跑分析、畫圖、照範本寫報告、
    /// 另存可重現的 .py，一趟交出來。不掛 Word 技能——沙箱預裝 python-docx、pandas、scikit-learn、matplotlib。
    /// 沙箱沒有網路，也沒有中文字型：圖上的字用英文，報告內文照樣用中文。
    /// </summary>
    public static class DataAnalysisJob
    {
        /// <summary>要放進沙箱實際讀取的資料檔。</summary>
        private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csv", ".tsv", ".xlsx", ".xls", ".json", ".parquet",
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp",
        };

        /// <summary>範本與說明文件：文字放進提示，Word 檔也放進沙箱（照範本的格式填寫時用得到）。</summary>
        private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".docx", ".pdf", ".md", ".txt",
        };

        /// <summary>每份說明文件放進提示的最多字數（範本與欄位說明通常很短；整本 PDF 不需要全文）。</summary>
        internal const int MaxDocumentChars = 6000;

        /// <summary>單一檔案超過這個大小就不放進沙箱（Files API 的上限之內，留一點餘裕）。</summary>
        public const long MaxUploadBytes = 200L * 1024 * 1024;

        /// <summary>這一趟最多花多少（預留額度與費用上限）。</summary>
        public const double JobBudgetUsd = 3.0;

        /// <summary>
        /// 這一趟最多跑多久。2026-11-03 成功的那次 5.3 分鐘；跑到 12 分鐘還沒交檔，多半是在做提示沒要求的事
        /// （大範圍調參、反覆重跑），再等下去只是多花錢。
        /// </summary>
        public static readonly TimeSpan TimeBudget = TimeSpan.FromMinutes(12);

        /// <summary>逾時後給使用者的說明（不退回原本流程，見 AgentRuntime.TryRunDataAnalysisAsync）。</summary>
        public static string TimedOutMessage(TimeSpan budget, double spentUsd) =>
            $"在沙箱執行分析超過 {budget.TotalMinutes:0} 分鐘還沒完成，已經停止（這一趟約花 US${spentUsd:0.00}）。\n\n" +
            "沒有改用一般流程補一份報告：那樣做出來的報告沒有實際執行的數字。\n" +
            "可以直接再執行一次；如果作業內容很多，也可以拆成幾個節點，例如先做資料清理與視覺化，再做模型。";

        /// <summary>沙箱做完了、補交一次也沒拿到檔案時給使用者的說明。</summary>
        public static string NoFilesMessage(double spentUsd) =>
            $"在沙箱裡完成了分析，但沒有拿回檔案，已經停止（這一趟約花 US${spentUsd:0.00}）。\n\n" +
            "沒有改用一般流程補一份報告：那樣做出來的報告沒有實際執行的數字。\n" +
            "可以直接再執行一次。";

        public static bool IsDataFile(string? fileName) => DataExtensions.Contains(Path.GetExtension(fileName ?? ""));
        public static bool IsImage(string? fileName) => ImageExtensions.Contains(Path.GetExtension(fileName ?? ""));
        public static bool IsDocument(string? fileName) => DocumentExtensions.Contains(Path.GetExtension(fileName ?? ""));

        /// <summary>有資料檔、而且要交報告或表格，才走沙箱執行。只要純文字回答的，照舊。</summary>
        public static bool Applies(IEnumerable<string>? attachmentNames, OutputIntent? intent)
            => intent != null && (intent.WantsReport || intent.WantsTable) &&
               (attachmentNames ?? Array.Empty<string>()).Any(IsDataFile);

        /// <summary>
        /// 要收回來的檔案：報告、結果表、可重現的程式。
        /// 結果表即使沒被要求也收：2026-11-03 那份報告寫著「完整表格見 hw1_results.xlsx」，沙箱也做出來了，
        /// 卻因為不在清單上被丟掉，使用者拿到的報告指向一個不存在的檔案。
        /// </summary>
        public static IReadOnlyCollection<string> WantedExtensions(bool wantsReport, bool wantsTable)
        {
            var exts = new List<string>();
            if (wantsReport) exts.Add(".docx");
            exts.Add(".xlsx");
            exts.Add(".py");
            return exts;
        }

        public const string SystemPrompt =
            "你在一個可以執行 Python 的沙箱裡，替使用者實際完成一份資料分析工作。\n" +
            "沙箱已預裝 pandas、numpy、scipy、scikit-learn、statsmodels、matplotlib、seaborn、python-docx、openpyxl；沒有網路，不能 pip install。\n" +
            "\n" +
            "工作規則：\n" +
            "1. 先用程式讀取上傳的資料檔，檢查筆數、欄位、型別與缺值，再決定怎麼清理。\n" +
            "2. 報告裡的每一個數字、表格與圖，都必須來自你在沙箱裡實際執行的結果，不得估計、不得編造、不得寫「請自行填入」。\n" +
            "   報告的表格裡不要出現 nan、None、inf：不適用的格子寫「—」，並在表下用一句話說明為什麼不適用。\n" +
            "3. 圖表用 matplotlib 或 seaborn 產生並嵌入報告。沙箱沒有中文字型：圖上的標題、座標軸、圖例一律用英文，報告內文照常用使用者的語言。\n" +
            "4. 有範本時，照範本的章節順序與標題寫；範本裡的姓名、學號等個人資料一律留空白讓使用者自己填，不要填任何人名。\n" +
            "   範本裡寫給學生的說明、提示與示範文字不要照抄進報告：例如標題後括號裡的「(參考程式碼Part 1)」、\n" +
            "   「根據圖表進行說明分析：上圖是…」這類範例句。標題只保留章節名稱，內文寫你自己的分析。\n" +
            "5. 另外把整份分析寫成一支可以獨立重現的 .py：從讀取資料到產生所有結果，資料檔用相對路徑，\n" +
            "   檔名用使用者電腦上的原檔名（提示裡有列出對照；在沙箱裡先把資料檔複製一份成原檔名）。\n" +
            "   讓使用者把 .py 和資料檔放在同一個資料夾就能直接執行。.py 開頭用註解列出需要安裝的套件。\n" +
            "   使用者電腦上的套件可能比沙箱新（例如 pandas 3）：除了第一個參數，其餘參數一律用關鍵字傳入\n" +
            "   （例如 df.to_excel(writer, sheet_name=\"x\")），不要用已棄用的寫法。\n" +
            "6. Word 報告內同時設定東亞字型（East Asian）為 Microsoft JhengHei，讓中文在 Windows 上正確顯示。\n" +
            "\n" +
            "分析方法規則（作業另有規定時以作業為準）：\n" +
            "- 作業列出的每個項目都要做到，不可為了省時間省略。\n" +
            "- 報告裡描述數字和做法的句子（筆數、缺值數、刪除筆數、門檻、選中的方法與參數、檢定結果），要由程式依最後實際使用的資料算出的變數產生，不要手打數字。\n" +
            "- 報告寫的做法要和程式實際做的一致：用了哪些資料、篩選條件、前處理、加權、公式與單位，有做的照實寫，沒做的不要寫。\n" +
            "- 沒有建立預測模型的分析（描述統計、交叉表、假設檢定、問卷分析等）不需要切訓練集與測試集。\n" +
            "\n" +
            "有建立預測或分類模型、或要比較多個模型時，另外遵守：\n" +
            "- 測試集只用來報告最後結果；挑模型、挑超參數、選「最佳模型」都只能用交叉驗證或驗證集，不可以看測試集的成績來選。\n" +
            "- 離群值門檻、補值、標準化都只用訓練集決定，再把同一組門檻與參數套到測試集。\n" +
            "- 類別變數做 One-Hot 時，以最常見的類別當基準組（被省略的那一類）。\n" +
            "- 0/1 或 One-Hot 欄位沒有標準化時，不可宣稱正規化的懲罰「對各特徵公平」；照實寫哪些欄位有標準化、哪些沒有。\n" +
            "- 對主要預測變數（例如面積、規模、金額）做截尾或設上限時，報告要寫明門檻，並在誤差分析中檢查誤差最大的案件\n" +
            "  是否落在截尾範圍外（模型會把它們當成門檻值來預測，無法外推）。解釋誤差來源時，先檢查前處理\n" +
            "  （截尾、補值、離群值處理）造成的影響，再歸因到模型本身或對數還原。\n" +
            "- 作業沒有指定模型時，模型最多 4 個；RidgeCV、LassoCV 這類使用內建路徑選參數的正規化變體，\n" +
            "  和對應的線性模型算同一族，不受 4 個模型的限制。\n" +
            "\n" +
            "自我檢查（寫在 .py 裡、產生報告之前執行，檢查不過就讓程式報錯，修好再跑）：\n" +
            "- 每個由原始欄位換算或解析出來的變數（日期拆出的年月日、年齡或年資、比例、單位換算、從文字解析出的數字等），\n" +
            "  用 assert 檢查範圍合理，並印出最小值、最大值。例如：月份在 1～12、比例在 0～1、百分比在 0～100、\n" +
            "  計數與金額不為負、年齡或屋齡在合理年數內、分項加總等於合計。\n" +
            "  日期先看實際格式與位數再拆（民國日期常見 7 碼 yyymmdd 或 5 碼 yyymm）。\n" +
            "- 建模或做統計檢定前，刪除整欄都是同一個值的變數（例如沒有任何一筆屬於的 One-Hot「其他」欄位），報告裡的變數數用刪除後的數字。\n" +
            "\n" +
            "執行環境紀律（違反會讓整個任務失敗；整個任務有時間上限，約 10 分鐘內要交出檔案）：\n" +
            "- 做法固定：先用一兩個簡短指令看資料，然後把分析、圖表、報告全部寫進那一支 .py，執行一次就產出所有檔案。\n" +
            "  不要先在互動指令裡做一遍分析、再另外寫程式重做一遍；也不要為了驗證把整支程式再跑一次。\n" +
            "- 執行出錯時只修出錯的地方再跑；同一個錯誤不要反覆嘗試超過兩次。\n" +
            "- 不要把整份資料印出來：看資料用 head()、info()、describe()，每個指令的輸出控制在 50 行以內。\n" +
            "- 不要印出二進位檔或圖片的內容。\n" +
            "- 沙箱只有 1 顆 CPU：不做格狀搜尋或隨機搜尋（GridSearchCV、RandomizedSearchCV），用合理的預設參數；\n" +
            "  樹模型的樹不超過 200 棵，交叉驗證最多 5 折。\n" +
            "- 沙箱的套件版本比使用者電腦上的舊，在這裡跑得過、在新版會報錯的寫法不會出現警告。一律用新舊版本都能跑的寫法：\n" +
            "  RidgeCV／LassoCV／ElasticNetCV 的 alpha 傳入明確的陣列（例如 alphas=np.logspace(-4, 1, 60)），不要用 n_alphas；\n" +
            "  RMSE 用 np.sqrt(mean_squared_error(...))，不要用 squared=False；OneHotEncoder 用 sparse_output；\n" +
            "  不要用 np.float、np.int 這類已移除的別名，也不要用 pandas 的 iteritems()。\n" +
            "\n" +
            "交付規則：\n" +
            "- 每種檔案只交付一個最終版本（報告 .docx、結果表 .xlsx、程式 .py）。提示有指定檔名時一律照用，\n" +
            "  .py 產生的檔案、報告裡引用的檔名也都用同一組名稱；沒有指定時用有意義的名稱。\n" +
            "- 報告裡提到的每一個檔案都要交付；沒有交付的檔案不要在報告裡引用。\n" +
            ClaudeDocumentSkillsService.DeliverRule + "\n" +
            "- 做完後用三到六句話回覆使用者：做了什麼、最重要的幾個結果數字、交付了哪些檔案。不要在回覆裡貼程式碼。";

        public sealed record Request
        {
            public string UserInput { get; init; } = "";
            public string PreferenceBlock { get; init; } = "";
            public IReadOnlyList<string> DataFiles { get; init; } = Array.Empty<string>();
            public IReadOnlyList<string> Images { get; init; } = Array.Empty<string>();
            public IReadOnlyList<(string FileName, string Text)> Documents { get; init; } = Array.Empty<(string, string)>();
            /// <summary>也放進沙箱的範本檔（沙箱裡的檔名）。</summary>
            public IReadOnlyList<string> Templates { get; init; } = Array.Empty<string>();
            /// <summary>沙箱檔名 → 使用者電腦上的原檔名（上傳時因字元限制改過名的才有）。</summary>
            public IReadOnlyDictionary<string, string> OriginalNames { get; init; } = new Dictionary<string, string>();
            public bool WantsReport { get; init; }
            public bool WantsTable { get; init; }
            public string Language { get; init; } = "";
            /// <summary>報告交付時的檔名（含 .docx）。平台用這個名稱存檔，報告與 .py 裡也要用同一個名稱。</summary>
            public string ReportFileName { get; init; } = "";
            /// <summary>結果表交付時的檔名（含 .xlsx）。</summary>
            public string TableFileName { get; init; } = "";
        }

        /// <summary>
        /// 結果表存檔用的標題。交付時（AgentRuntime.TryRunDataAnalysisAsync）和告訴沙箱的檔名都從這裡來，兩邊才會一樣。
        /// 2026-11-04：原本沙箱跑完才決定檔名，.py 和報告裡寫的檔名跟交付的檔名對不上。
        /// </summary>
        public static string TableTitle(string title, bool wantsTable)
            => title + (wantsTable ? "（表格）" : "（結果表）");

        public static string BuildUserPrompt(Request r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【使用者的要求】");
            sb.AppendLine((r.UserInput ?? "").Trim());
            sb.AppendLine();

            if (r.DataFiles.Count > 0)
            {
                sb.AppendLine("【已放進沙箱的資料檔】（完整內容，沒有截斷）");
                foreach (var f in r.DataFiles) sb.AppendLine("- " + WithOriginal(r, f));
                sb.AppendLine();
            }

            if (r.Images.Count > 0)
            {
                sb.AppendLine("【附上的圖片】（作業說明、截圖等，已附在這則訊息裡）");
                foreach (var f in r.Images) sb.AppendLine("- " + WithOriginal(r, f));
                sb.AppendLine();
            }

            if (r.Templates.Count > 0)
            {
                sb.AppendLine("【也放進沙箱的範本檔】（照它的格式填寫）");
                foreach (var f in r.Templates) sb.AppendLine("- " + WithOriginal(r, f));
                sb.AppendLine();
            }

            foreach (var (name, text) in r.Documents)
            {
                string t = (text ?? "").Trim();
                if (t.Length == 0) continue;
                if (t.Length > MaxDocumentChars) t = t[..MaxDocumentChars] + "\n…（以下省略）";
                sb.AppendLine($"【附件：{name}】");
                sb.AppendLine(t);
                sb.AppendLine();
            }

            static string Named(string fileName) => string.IsNullOrWhiteSpace(fileName) ? "" : $"檔名必須是「{fileName}」；";

            sb.AppendLine("【需要交付的檔案】");
            if (r.WantsReport)
                sb.AppendLine($"- 報告 .docx：{Named(r.ReportFileName)}依要求（有範本就照範本）完成，內含實際執行得到的數字、表格與圖。");
            sb.AppendLine(r.WantsTable
                ? $"- 表格 .xlsx：{Named(r.TableFileName)}整理好的結果表。"
                : $"- 結果表 .xlsx（選擇性）：{Named(r.TableFileName)}報告引用了整理好的結果表時才交付。");
            sb.AppendLine("- 程式 .py：可獨立重現所有結果的完整程式。");
            if (!string.IsNullOrWhiteSpace(r.ReportFileName) || !string.IsNullOrWhiteSpace(r.TableFileName))
                sb.AppendLine("- 上面指定的檔名就是交到使用者手上的檔名：.py 存檔時用同樣的檔名，報告裡提到這些檔案時也寫同樣的檔名。");

            if (!string.IsNullOrWhiteSpace(r.Language))
                sb.AppendLine().AppendLine($"報告內文使用：{r.Language}。");

            if (!string.IsNullOrWhiteSpace(r.PreferenceBlock))
                sb.AppendLine().AppendLine(r.PreferenceBlock.Trim());

            return sb.ToString();
        }

        private static string WithOriginal(Request r, string sandboxName)
            => r.OriginalNames.TryGetValue(sandboxName, out var original) && original != sandboxName
                ? $"{sandboxName}（使用者電腦上的原檔名：{original}）"
                : sandboxName;

        /// <summary>上傳到 Files API 用的 MIME。</summary>
        public static string MimeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".json" => "application/json",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls" => "application/vnd.ms-excel",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        };

        /// <summary>沙箱的回覆照規定不該有程式碼；萬一有，交給程式碼存檔那一步處理，這裡只補一句圖表語言的說明。</summary>
        public const string ChartLanguageNote =
            "ℹ 報告裡的圖表文字是英文：執行程式的沙箱沒有中文字型。";
    }
}
