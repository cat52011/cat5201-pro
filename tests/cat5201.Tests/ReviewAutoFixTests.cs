using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 審查後自動修正一輪（2026-11-04 使用者要求，個人化預設關）：
    /// 只修「高」「中」；修正版沿用原本的檔名、修正前的檔案移進「修正前」（失敗就搬回原位）；
    /// 兩次審查與修正說明合併成一份清單；再審查逐項結案，沒完成就不能說修好了。
    /// </summary>
    public class ReviewAutoFixTests
    {
        private static ReportReview.Issue I(string severity, string problem = "p") =>
            new(severity, "第 8 節", problem, "原文", "改成 x");

        private static ReportReview.Result R(
            IReadOnlyList<ReportReview.Issue> model, IReadOnlyList<ReportReview.Issue>? program = null,
            ReportReview.ReviewStatus status = ReportReview.ReviewStatus.Completed,
            IReadOnlyList<ReportReview.PreviousStatus>? previous = null) =>
            new(model, program ?? Array.Empty<ReportReview.Issue>(), "gpt", "note", "checked", "", status, null, previous);

        [Fact]
        public void OnlyHighAndMediumAreFixed_LowIsListedOnly()
        {
            var r = R(new[] { I("高", "a"), I("中", "b"), I("低", "c") },
                      new[] { I("中", "nan"), I("低", "結果檔 nan") });

            Assert.Equal(new[] { "a", "b", "nan" }, ReviewAutoFix.FixableIssues(r).Select(i => i.Problem));
        }

        [Fact]
        public void NoHighOrMedium_NothingToFix()
        {
            Assert.Empty(ReviewAutoFix.FixableIssues(R(new[] { I("低") })));
            Assert.Empty(ReviewAutoFix.FixableIssues(R(Array.Empty<ReportReview.Issue>())));
        }

        [Fact]
        public void FixPrompt_KeepsTheOriginalFileNames_PutsRulesFirst_AndAsksForAPlainReportTone()
        {
            var job = new DataAnalysisJob.Request
            {
                UserInput = "做好這個作業",
                DataFiles = new[] { "115Taipei.csv" },
                WantsReport = true,
                ReportFileName = "HW1.docx",
                TableFileName = "HW1（結果表）.xlsx",
            };
            var issues = new[]
            {
                new ReportReview.Issue("高", "第 8 節", "說懲罰對各特徵公平", "0/1 欄位沒有標準化", "刪掉這句"),
                new ReportReview.Issue("中", "表 2-1", "偏度 11.35 對 11.36", "", ""),
            };

            string p = ReviewAutoFix.BuildFixPrompt(job, "file_90.py", "HW1.py", issues);

            int rules = p.IndexOf("【這一趟是修正", StringComparison.Ordinal);
            int task = p.IndexOf("【原始任務說明", StringComparison.Ordinal);
            Assert.True(rules >= 0 && task > rules, "修正規則要在原始任務說明前面");

            Assert.Contains("「file_90.py」是上一趟交付的完整程式（使用者電腦上的檔名：HW1.py）", p);
            Assert.Contains("只改和下面清單有關的地方", p);
            Assert.Contains("審查者也可能判斷錯", p);
            Assert.Contains("由程式重新算出", p);
            // 檔名不變、報告裡不寫「修正版」：修正後的檔案要直接取代原本的檔案交出去（2026-11-04 實測後的要求）
            Assert.Contains("檔名和上一趟完全相同：程式仍存成「HW1.py」", p);
            Assert.Contains("不要出現「修正」「修正版」", p);
            Assert.Contains("「HW1.docx」", p);
            Assert.DoesNotContain("（修正版）.docx", p);
            // 語氣：2026-11-04 實測修正後的句子像在回應審查者（「並非…而是…」、筆數為 0 的處理）
            Assert.Contains("不要寫成回應審查的說法", p);
            Assert.Contains("筆數為 0", p);

            Assert.Contains("1. ［高］第 8 節：說懲罰對各特徵公平", p);
            Assert.Contains("依據：0/1 欄位沒有標準化", p);
            Assert.Contains("2. ［中］表 2-1：偏度 11.35 對 11.36", p);
            Assert.Contains("做好這個作業", p);
            Assert.Contains("第 n 項：已修正", p);
        }

        [Fact]
        public void Originals_AreMovedIntoTheArchiveFolder_AndCanBeRestored()
        {
            string root = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            string codeDir = Path.Combine(root, "HW1");
            Directory.CreateDirectory(codeDir);
            try
            {
                string report = Path.Combine(root, "HW1.docx");
                string code = Path.Combine(codeDir, "HW1.py");
                File.WriteAllText(report, "原本的報告");
                File.WriteAllText(code, "print(1)");

                string archive = ReviewAutoFix.ArchiveDirFor(code);
                Assert.Equal(Path.Combine(codeDir, "修正前"), archive);
                Assert.Equal(Path.Combine("HW1", "修正前"), ReviewAutoFix.DisplayPath(archive, root));

                var moved = ReviewAutoFix.MoveToArchive(new[] { report, code, Path.Combine(root, "不存在.xlsx") }, archive);

                Assert.Equal(2, moved.Count);
                Assert.False(File.Exists(report));
                Assert.Equal("原本的報告", File.ReadAllText(Path.Combine(archive, "HW1.docx")));
                Assert.True(File.Exists(Path.Combine(archive, "HW1.py")));

                // 已經有「修正前」就不覆蓋，改用 _2
                Assert.Equal(Path.Combine(codeDir, "修正前_2"), ReviewAutoFix.ArchiveDirFor(code));

                // 修正後半段失敗：寫到一半的修正版被原本的檔案蓋回去
                File.WriteAllText(report, "寫到一半的修正版");
                ReviewAutoFix.Restore(moved);
                Assert.Equal("原本的報告", File.ReadAllText(report));
                Assert.Equal("print(1)", File.ReadAllText(code));
                Assert.False(File.Exists(Path.Combine(archive, "HW1.docx")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void CombinedRecord_HasBothReviewsAndTheFixNotes_InOrder()
        {
            string first = "# 獨立審查清單：HW1.docx\n\n## 審查模型找到的問題\n\n| 1 | 中 | … |";
            string recheck = "# 獨立審查清單：HW1.docx\n\n## 上一輪問題的處理狀態\n\n已修正 1 項";

            string md = ReviewAutoFix.CombinedRecord("HW1.docx", @"HW1\修正前", first, "第 1 項：已修正（改了缺值比例）", recheck);

            int a = md.IndexOf("## 一、修正前的審查", StringComparison.Ordinal);
            int b = md.IndexOf("## 二、修正說明", StringComparison.Ordinal);
            int c = md.IndexOf("## 三、修正後的再審查", StringComparison.Ordinal);
            Assert.True(a > 0 && b > a && c > b);
            Assert.Contains(@"「HW1\修正前」", md);
            Assert.Contains("第 1 項：已修正（改了缺值比例）", md);
            Assert.Contains("### 獨立審查清單：HW1.docx", md);     // 單次清單的標題降兩級
            Assert.Contains("#### 上一輪問題的處理狀態", md);
            Assert.Contains("「低」只列在上面的清單", md);

            string noRecheck = ReviewAutoFix.CombinedRecord("HW1.docx", "x", first, null, null);
            Assert.Contains("再審查沒有完成", noRecheck);
            Assert.Contains("沒有逐項說明", noRecheck);
        }

        [Fact]
        public void SuccessText_SaysFileNamesAreKept_AndWhereTheOriginalsAre()
        {
            var recheck = R(Array.Empty<ReportReview.Issue>(), previous: new[]
            {
                new ReportReview.PreviousStatus(1, "a", ReportReview.StatusFixed, "現在寫 0.029%"),
                new ReportReview.PreviousStatus(2, "b", ReportReview.StatusFixed, "…"),
            });

            string t = ReviewAutoFix.SuccessText(2, @"HW1\修正前", 0.42, "第 1 項：已修正\n第 2 項：已修正", recheck);

            Assert.Contains("針對 2 項", t);
            Assert.Contains("檔名不變", t);
            Assert.Contains(@"「HW1\修正前」", t);
            Assert.Contains("US$0.42", t);
            Assert.Contains("原本 2 項中，已修正 2 項、仍存在 0 項、無法確認 0 項；沒有新發現的問題。", t);
            Assert.DoesNotContain("不再自動修正", t);      // 全部修好就不用提
        }

        [Fact]
        public void Recheck_CountsRemainingUnknownAndNewIssuesSeparately()
        {
            var recheck = R(new[] { I("中", "新的") }, previous: new[]
            {
                new ReportReview.PreviousStatus(1, "a", ReportReview.StatusFixed, ""),
                new ReportReview.PreviousStatus(2, "b", ReportReview.StatusRemains, ""),
            });

            // 原本 3 項，審查者只回了 2 項 → 第 3 項算無法確認，不會因為沒回就當成修好
            string line = ReviewAutoFix.RecheckLine(3, recheck);

            Assert.Contains("原本 3 項中，已修正 1 項、仍存在 1 項、無法確認 1 項", line);
            Assert.Contains("另外新發現 1 項（高 0、中 1）", line);
            Assert.Contains("不再自動修正", line);
        }

        [Theory]
        [InlineData(ReportReview.ReviewStatus.Failed, "審查模型這次沒有回應")]
        [InlineData(ReportReview.ReviewStatus.Unparseable, "審查回覆無法解析")]
        [InlineData(ReportReview.ReviewStatus.NotRun, "這次沒有呼叫審查模型")]
        public void Recheck_ThatDidNotComplete_NeverClaimsTheIssuesWereFixed(ReportReview.ReviewStatus status, string expected)
        {
            string line = ReviewAutoFix.RecheckLine(2, R(Array.Empty<ReportReview.Issue>(), status: status));

            Assert.Contains(expected, line);
            Assert.Contains("沒有經過第二次核對", line);
            Assert.DoesNotContain("已修正", line);
            Assert.DoesNotContain("沒有新發現", line);
            Assert.Contains("沒有完成", ReviewAutoFix.RecheckLine(2, null));
        }

        [Fact]
        public void NewIssues_AreClassified_ByWhetherTheQuotedTextExistedBeforeTheFix()
        {
            // 2026-11-04 實測：修正只改了一句話，再審查「新發現」的特徵重要性問題其實修正前就在程式裡（上一輪漏看）
            string before = "上表為樹模型特徵重要性前 8 名。\nimp_df = pd.DataFrame(imp).sort_values(\"M6 RandomForest (log price)\", ascending=False)";
            string after = "樹模型理論上不需標準化，但本程式為共用前處理流程，仍對連續特徵依序進行補值、截尾與標準化。\n" + before;

            var issues = new[]
            {
                new ReportReview.Issue("中", "Part III", "重要性表說明", "報告：「上表為樹模型特徵重要性前 8 名。」／程式：「imp_df = pd.DataFrame(imp).sort_values(\"M6 RandomForest (log price)\", ascending=False)」", ""),
                new ReportReview.Issue("中", "Part III", "新句子有誤", "報告：「樹模型理論上不需標準化，但本程式為共用前處理流程」", ""),
                new ReportReview.Issue("低", "表 1", "引用改寫過", "報告：「這段文字兩個版本都沒有」", ""),
                new ReportReview.Issue("低", "表 2", "沒有引號", "總樓層數 1 0.000", ""),
                new ReportReview.Issue("低", "表 3", "片段太短", "「0.029」", ""),
            };

            var origins = ReviewAutoFix.ClassifyOrigins(issues, before, after);

            Assert.Equal(new[]
            {
                ReportReview.OriginBefore,      // 修正前就存在（上一輪漏看）
                ReportReview.OriginAfter,       // 只在修正後的版本
                ReportReview.OriginUnknown,     // 兩邊都找不到
                ReportReview.OriginUnknown,     // 沒有引號可比對
                ReportReview.OriginUnknown,     // 太短，不比對
            }, origins);
        }

        [Fact]
        public void Classification_IgnoresWhitespaceAndTableSeparators_AndSplitsEllipses()
        {
            string before = "| 總樓層數 | 1 | 0.000 |\n報告的第一段文字很長很長，後面還有別的內容";
            var issues = new[]
            {
                new ReportReview.Issue("中", "表", "p", "報告：「總樓層數 ／ 1 ／ 0.000」", ""),
                new ReportReview.Issue("中", "段", "p", "報告：「報告的第一段文字很長很長…後面還有別的內容」", ""),
            };

            Assert.All(ReviewAutoFix.ClassifyOrigins(issues, before, ""), o => Assert.Equal(ReportReview.OriginBefore, o));
        }

        [Fact]
        public void RecheckLine_SplitsNewIssuesByOrigin()
        {
            var recheck = R(new[] { I("中", "a"), I("低", "b") }, previous: new[]
                { new ReportReview.PreviousStatus(1, "x", ReportReview.StatusFixed, "") }) with
            {
                Origins = new[] { ReportReview.OriginBefore, ReportReview.OriginUnknown },
            };

            string line = ReviewAutoFix.RecheckLine(1, recheck);
            Assert.Contains("另外新發現 2 項（高 0、中 1），其中審查模型找到的 2 項：修正前就存在 1 項（上一輪漏看）、修正後才出現 0 項、無法判斷 1 項", line);

            string md = ReportReview.ToMarkdown("HW1.docx", Array.Empty<string>(), recheck);
            Assert.Contains("| # | 嚴重度 | 來源 | 位置 |", md);
            Assert.Contains("| 1 | 中 | 修正前就存在（上一輪漏看） |", md);
            Assert.Contains("由程式比對問題引用的原文判斷", md);
        }

        [Fact]
        public void FixReply_IsShortenedInTheAnswer()
        {
            string longReply = new string('字', ReviewAutoFix.MaxFixReplyChars + 50);
            string t = ReviewAutoFix.SuccessText(1, "a", 0, longReply, null);
            Assert.Contains(new string('字', ReviewAutoFix.MaxFixReplyChars) + "…", t);
            Assert.DoesNotContain(new string('字', ReviewAutoFix.MaxFixReplyChars + 1), t);
        }

        [Fact]
        public void FailedLine_AlwaysSaysTheOriginalIsUnaffected()
        {
            Assert.Contains("原本的檔案不受影響", ReviewAutoFix.FailedLine("逾時"));
            Assert.Contains("原因不明", ReviewAutoFix.FailedLine(""));
        }
    }
}
