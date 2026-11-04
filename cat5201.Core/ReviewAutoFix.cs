using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 審查後自動修正一輪（個人化，預設關）。
    ///
    /// 2026-11-04 使用者要求。起因：HW1 迴歸作業的獨立審查找到 5 個問題，其中 4 個屬實，
    /// 但審查只列清單、不改報告，交出去的作業還是有錯——審查的價值留在清單裡，沒有變成更好的成品。
    ///
    /// 做法（只做一輪，不會一直來回）：
    ///   1. 審查找到「高」或「中」的問題時，把上一趟的 .py、資料檔和問題清單交回同一個撰寫模型，
    ///      在沙箱裡只修清單上的問題、重跑一次。
    ///   2. 修正成功：修正後的報告、結果表、程式用原本的檔名放在原位（報告裡不會出現「修正版」字樣，可以直接交）；
    ///      修正前的檔案移進程式資料夾裡的「修正前」子資料夾，不再列成 chip。
    ///   3. 修正版再審查一次；兩次審查和修正說明合併成一份「審查清單」（同一個檔名，外面的檔案數和沒修正時一樣）。
    ///   4. 修正沒完成：什麼都不動，原本的檔案照舊。
    ///
    /// 2026-11-04 第一次實測：4 項「中」全部修好、其他數字不變、再審查 0 項，多花約 4 分鐘；
    /// 但原版與修正版各一組、共 8 個檔案，修正版報告裡也寫著「（修正版）」檔名——所以改成上面的做法。
    ///
    /// 目前只支援「附資料檔、在沙箱執行」的分析報告：那條路有可以重跑的程式，
    /// 數字改了會由程式重新算出來，不是手改文字。其他報告照舊只列清單。
    /// </summary>
    public static class ReviewAutoFix
    {
        /// <summary>沙箱分析成功後放進 workspace 的項目類型（存重跑需要的東西）。</summary>
        public const string ContextItemType = "data_analysis_fix_context";

        /// <summary>修正這一趟最多花多少（美元）。原本整份分析約 US$0.5～1，修正通常更少。</summary>
        public const double BudgetUsd = 1.5;

        /// <summary>修正這一趟最多跑多久。只是改程式、重跑一次，不需要原本的 12 分鐘。</summary>
        public static readonly TimeSpan TimeBudget = TimeSpan.FromMinutes(8);

        /// <summary>修正前的檔案放在程式資料夾裡的這個子資料夾。</summary>
        public const string ArchiveFolderName = "修正前";

        /// <summary>重跑一次沙箱需要的東西：用哪個模型、哪些已上傳的檔案、原本的任務與交付檔名。</summary>
        public sealed record SandboxContext(
            string Model,
            IReadOnlyList<ClaudeDocumentSkillsService.ContainerFile> Uploads,
            DataAnalysisJob.Request Job,
            string Title,
            string CodeFilePath,
            string SourceSummary);

        /// <summary>要修的問題：審查模型的「高」「中」，以及程式檢查在報告裡找到的「中」。「低」只列清單、不修。</summary>
        public static IReadOnlyList<ReportReview.Issue> FixableIssues(ReportReview.Result review)
        {
            if (review == null) return Array.Empty<ReportReview.Issue>();
            return review.ModelIssues.Where(i => i.Severity is "高" or "中")
                .Concat(review.ProgramIssues.Where(i => i.Severity is "高" or "中"))
                .ToList();
        }

        /// <summary>
        /// 修正這一趟的提示。修正規則放在最前面、優先於原本的任務說明；原本的任務只供參考（知道作業要什麼、檔案叫什麼）。
        /// 交付檔名和上一趟相同：修正版要取代原本的檔案，報告裡也不該出現「修正版」。
        /// </summary>
        public static string BuildFixPrompt(
            DataAnalysisJob.Request job,
            string codeSandboxName,
            string originalCodeName,
            IReadOnlyList<ReportReview.Issue> issues)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【這一趟是修正，以下規則優先於後面的原始任務說明】");
            sb.AppendLine("上一趟已經交付了報告。獨立審查（沒有參與撰寫的模型）找到下面這些問題，請只修這些問題，重新產出檔案。");
            sb.AppendLine();
            sb.AppendLine($"- 沙箱裡的「{codeSandboxName}」是上一趟交付的完整程式（使用者電腦上的檔名：{originalCodeName}）；資料檔也已經放進沙箱。");
            sb.AppendLine("- 做法：跟上一趟一樣先把資料檔複製成原檔名，然後修改這支程式、執行一次，產出所有檔案。不要從頭重寫。");
            sb.AppendLine("- 只改和下面清單有關的地方。不要換分析方法、模型或參數，不要加新的分析；和清單無關的數字應該維持不變。");
            sb.AppendLine("- 審查者也可能判斷錯：先對照程式和資料，確認問題真的存在再改；不存在就不要改，並在回覆裡說明為什麼。");
            sb.AppendLine("- 如果問題出在程式算錯，改程式讓報告裡受影響的數字由程式重新算出，不要只改報告文字。");
            sb.AppendLine("- 改寫報告文字時，用一般報告的語氣直接寫正確的內容：不要寫成回應審查的說法（例如「並非……而是……」），" +
                          "也不要為了交代而寫出筆數為 0、沒有發生的處理。");
            sb.AppendLine($"- 檔名和上一趟完全相同：程式仍存成「{originalCodeName}」，報告與結果表照下面「需要交付的檔案」裡的檔名。" +
                          "這一份會取代原本的檔案交給使用者，報告裡不要出現「修正」「修正版」等字樣。");
            sb.AppendLine("- 回覆使用者時，逐項列出每個問題的處理結果，一項一行，格式：「第 n 項：已修正（改了什麼）」或「第 n 項：未修正（原因）」。不要貼程式碼。");
            sb.AppendLine();
            sb.AppendLine("【審查找到的問題】");
            int n = 0;
            foreach (var i in issues ?? Array.Empty<ReportReview.Issue>())
            {
                n++;
                sb.AppendLine($"{n}. ［{i.Severity}］{Clean(i.Where)}：{Clean(i.Problem)}");
                if (!string.IsNullOrWhiteSpace(i.Evidence)) sb.AppendLine($"   依據：{Clean(i.Evidence)}");
                if (!string.IsNullOrWhiteSpace(i.Suggestion)) sb.AppendLine($"   建議：{Clean(i.Suggestion)}");
            }
            sb.AppendLine();
            sb.AppendLine("【原始任務說明（僅供參考：作業要求、資料檔與交付檔名）】");
            sb.Append(DataAnalysisJob.BuildUserPrompt(job));
            return sb.ToString();
        }

        // ── 修正前的檔案 ─────────────────────────────────────────

        /// <summary>修正前的檔案要放的資料夾：程式資料夾裡的「修正前」；已存在就接 _2、_3…（不覆蓋任何東西）。</summary>
        public static string ArchiveDirFor(string codeFilePath)
        {
            string parent = Path.GetDirectoryName(codeFilePath) ?? "";
            string dir = Path.Combine(parent, ArchiveFolderName);
            for (int k = 2; Directory.Exists(dir) || File.Exists(dir); k++)
                dir = Path.Combine(parent, $"{ArchiveFolderName}_{k}");
            return dir;
        }

        /// <summary>
        /// 把修正前的檔案移進 archiveDir。中途失敗就把已經移過去的搬回原位再丟出例外，
        /// 呼叫端看到例外就當作修正沒完成——原本的檔案一定還在原位。
        /// </summary>
        public static IReadOnlyList<(string From, string To)> MoveToArchive(IEnumerable<string> paths, string archiveDir)
        {
            var moved = new List<(string From, string To)>();
            try
            {
                Directory.CreateDirectory(archiveDir);
                foreach (var from in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(from)) continue;
                    string to = Path.Combine(archiveDir, Path.GetFileName(from));
                    File.Move(from, to);
                    moved.Add((from, to));
                }
                return moved;
            }
            catch
            {
                Restore(moved);
                throw;
            }
        }

        /// <summary>把移進「修正前」的檔案搬回原位（修正後半段失敗時用）。原位已經有檔案就覆蓋——那是寫到一半的修正版。</summary>
        public static void Restore(IEnumerable<(string From, string To)> moved)
        {
            foreach (var (from, to) in moved.Reverse())
            {
                try
                {
                    if (File.Exists(to)) File.Move(to, from, overwrite: true);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("AutoFix", $"修正前的檔案搬不回原位：{to}", ex);
                }
            }
        }

        /// <summary>給使用者看的「修正前」位置：產出資料夾底下的相對路徑，例如「機器學習迴歸分析作業\修正前」。</summary>
        public static string DisplayPath(string archiveDir, string generatedDir)
        {
            try
            {
                string rel = Path.GetRelativePath(generatedDir, archiveDir);
                return rel.StartsWith("..", StringComparison.Ordinal) ? archiveDir : rel;
            }
            catch { return archiveDir; }
        }

        // ── 新問題的來源 ──────────────────────────────────────────

        /// <summary>引用的原文要多長才拿來比對（去掉空白與分隔符號之後）。太短的片段到處都有，比對不出意義。</summary>
        internal const int MinQuoteChars = 8;

        /// <summary>
        /// 再審查新發現的問題，是修正前就存在（上一輪漏看）、修正後才出現，還是無法判斷。
        /// 由程式比對：問題引用的原文（「…」或『…』裡的字）出現在修正前的報告或程式 → 修正前就存在；
        /// 只出現在修正後的版本 → 修正後才出現；都找不到（例如引用的是表格、或審查者改寫過原文）→ 無法判斷。
        /// 2026-11-04 實測：修正只改了一句話，再審查卻「新發現」一項——那段程式修正前就有，是第一次審查漏看。
        /// 不分開的話，會被誤讀成修正把東西改壞了。不花 AI 費用。
        /// </summary>
        public static IReadOnlyList<string> ClassifyOrigins(
            IReadOnlyList<ReportReview.Issue> issues, string beforeText, string afterText)
        {
            string before = Normalize(beforeText), after = Normalize(afterText);
            var list = new List<string>();
            foreach (var issue in issues ?? Array.Empty<ReportReview.Issue>())
            {
                var quotes = Quotes(issue.Evidence).Select(Normalize).Where(q => q.Length >= MinQuoteChars).ToList();
                if (quotes.Count == 0) list.Add(ReportReview.OriginUnknown);
                else if (quotes.Any(q => before.Contains(q, StringComparison.Ordinal))) list.Add(ReportReview.OriginBefore);
                else if (quotes.Any(q => after.Contains(q, StringComparison.Ordinal))) list.Add(ReportReview.OriginAfter);
                else list.Add(ReportReview.OriginUnknown);
            }
            return list;
        }

        private static readonly System.Text.RegularExpressions.Regex QuoteRx =
            new("「([^「」]+)」|『([^『』]+)』", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static IEnumerable<string> Quotes(string? evidence)
        {
            foreach (System.Text.RegularExpressions.Match m in QuoteRx.Matches(evidence ?? ""))
            {
                string q = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                // 審查者常用「…」代表中間省略：拆成幾段各自比對
                foreach (var part in q.Split(new[] { "…", "..." }, StringSplitOptions.RemoveEmptyEntries))
                    yield return part;
            }
        }

        /// <summary>比對前去掉空白與表格分隔符號：報告抽成文字時的表格格式，和審查者引用的寫法常常不一樣。</summary>
        private static string Normalize(string? s)
        {
            var sb = new StringBuilder((s ?? "").Length);
            foreach (char c in s ?? "")
                if (!char.IsWhiteSpace(c) && c is not ('|' or '／' or '｜')) sb.Append(c);
            return sb.ToString();
        }

        // ── 合併的審查清單 ────────────────────────────────────────

        /// <summary>
        /// 修正成功後的審查清單：修正前的審查、每一項怎麼處理、修正後的再審查，依序合成一份。
        /// 檔名和沒修正時一樣是「（報告名）（審查清單）」，外面的檔案數不變。
        /// </summary>
        public static string CombinedRecord(
            string reportName, string archiveDisplay, string firstReviewMarkdown, string? fixReply, string? recheckMarkdown)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 審查紀錄：{reportName}").AppendLine();
            sb.AppendLine("這份報告經過獨立審查後，自動修正了一輪。外面的報告、結果表與程式是修正後的版本；" +
                          $"修正前的檔案在「{archiveDisplay}」資料夾，可以對照修正前後。").AppendLine();

            sb.AppendLine("## 一、修正前的審查").AppendLine();
            sb.AppendLine(Demote(firstReviewMarkdown)).AppendLine();

            sb.AppendLine("## 二、修正說明").AppendLine();
            sb.AppendLine("只修「高」「中」的問題；「低」只列在上面的清單，不自動修改。").AppendLine();
            string reply = (fixReply ?? "").Trim();
            sb.AppendLine(reply.Length > 0 ? reply : "（撰寫模型這次沒有逐項說明。）").AppendLine();

            sb.AppendLine("## 三、修正後的再審查").AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(recheckMarkdown)
                ? "（再審查沒有完成，修正後的版本沒有經過第二次審查。）"
                : Demote(recheckMarkdown!));
            return sb.ToString();
        }

        /// <summary>單次審查清單的標題降兩級，放進合併的紀錄裡（# → ###、## → ####）。</summary>
        internal static string Demote(string markdown)
        {
            var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n')
                .Select(l => l.StartsWith("#", StringComparison.Ordinal) ? "##" + l : l);
            return string.Join("\n", lines).Trim();
        }

        // ── 答案裡的說明 ─────────────────────────────────────────

        /// <summary>修正的回覆最多放多少字進答案（逐項處理結果，通常幾行）。</summary>
        internal const int MaxFixReplyChars = 1200;

        public static string NotSupportedLine =>
            "審查後自動修正：這份報告不是附資料檔、在沙箱執行產出的，目前只支援這種報告，這次只列審查清單。";

        public static string NothingToFixLine =>
            "審查後自動修正：沒有「高」或「中」的問題，不需要修正。";

        /// <summary>審查模型沒有完成、程式檢查也沒找到要修的：沒有可依據的清單，不能說「不需要修正」。</summary>
        public static string ReviewIncompleteLine =>
            "審查後自動修正：審查模型這次沒有完成，沒有可依據的問題清單，所以沒有修正。";

        public static string FailedLine(string reason) =>
            $"審查後自動修正沒有完成（{(string.IsNullOrWhiteSpace(reason) ? "原因不明" : reason.Trim())}），原本的檔案不受影響。";

        /// <summary>修正成功後接在答案最後的說明。</summary>
        public static string SuccessText(
            int fixableCount, string archiveDisplay, double spentUsd, string? fixReply, ReportReview.Result? recheck)
        {
            var sb = new StringBuilder();
            sb.Append($"審查後自動修正：針對 {fixableCount} 項「高」「中」的問題修改程式、重跑一次。" +
                      $"報告、結果表與程式已換成修正後的版本（檔名不變），修正前的檔案移到「{archiveDisplay}」，完整經過記在「審查清單」檔");
            sb.Append(spentUsd > 0 ? $"（約 US${spentUsd:0.00}）。" : "。");

            string reply = (fixReply ?? "").Trim();
            if (reply.Length > 0)
            {
                if (reply.Length > MaxFixReplyChars) reply = reply[..MaxFixReplyChars] + "…";
                sb.AppendLine().AppendLine("修正說明：").Append(reply);
            }

            sb.AppendLine().Append(RecheckLine(fixableCount, recheck));
            return sb.ToString();
        }

        /// <summary>
        /// 修正後再審查的結論。只有再審查真的完成時才說原問題的處理結果；
        /// 原問題逐項結案（已修正／仍存在／無法確認），新發現的問題另外算。
        /// </summary>
        public static string RecheckLine(int fixableCount, ReportReview.Result? recheck)
        {
            if (recheck == null)
                return "修正後再審查沒有完成，修正後的版本沒有經過第二次核對。";

            var status = recheck.EffectiveStatus;
            if (status != ReportReview.ReviewStatus.Completed)
                return $"修正後再審查：{ReportReview.StatusText(status)}，修正後的版本沒有經過第二次核對，" +
                       "原本的問題是否修好無法確認。";

            var prev = recheck.Previous ?? Array.Empty<ReportReview.PreviousStatus>();
            int done = prev.Count(p => p.Status == ReportReview.StatusFixed);
            int remains = prev.Count(p => p.Status == ReportReview.StatusRemains);
            int unknown = prev.Count(p => p.Status == ReportReview.StatusUnknown) + Math.Max(0, fixableCount - prev.Count);
            int fresh = recheck.ModelIssues.Count + recheck.ProgramIssues.Count;
            int high = recheck.ModelIssues.Count(i => i.Severity == "高");
            int mid = recheck.ModelIssues.Count(i => i.Severity == "中") + recheck.ProgramIssues.Count(i => i.Severity == "中");

            string line = $"修正後再審查：原本 {fixableCount} 項中，已修正 {done} 項、仍存在 {remains} 項、無法確認 {unknown} 項；";
            if (fresh == 0)
                line += "沒有新發現的問題。";
            else
            {
                line += $"另外新發現 {fresh} 項（高 {high}、中 {mid}）";
                var origins = recheck.IssueOrigins;
                if (origins != null)
                {
                    int before = origins.Count(o => o == ReportReview.OriginBefore);
                    int after = origins.Count(o => o == ReportReview.OriginAfter);
                    int unsure = origins.Count(o => o == ReportReview.OriginUnknown);
                    // 程式檢查的項目沒有比對來源，不算在這三類裡
                    line += $"，其中審查模型找到的 {origins.Count} 項：修正前就存在 {before} 項（上一輪漏看）、修正後才出現 {after} 項、無法判斷 {unsure} 項";
                }
                line += "。";
            }
            if (remains + unknown + fresh > 0)
                line += "細節在「審查清單」檔；這一次不再自動修正。";
            return line;
        }

        private static string Clean(string? s) => (s ?? "").Replace("\r", "").Replace("\n", " ").Trim();
    }
}
