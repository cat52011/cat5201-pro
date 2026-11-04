using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-04（GPT 審查本專案）指出的三件事：
    ///   1. 審查沒有完成（空白、非 JSON、{}）時，摘要仍寫「沒有找到問題」；
    ///   2. 截斷與沒送審的檔案沒有讓使用者知道；
    ///   3. 自動修正後的再審查沒有逐項確認原本的問題。
    /// </summary>
    public class ReviewStatusTests
    {
        private static readonly ReportReview.Issue[] None = Array.Empty<ReportReview.Issue>();

        [Theory]
        [InlineData("")]
        [InlineData("我覺得報告寫得不錯。")]
        [InlineData("{}")]
        [InlineData("{\"checked\":\"都看過了\"}")]
        [InlineData("{\"issues\":\"沒有\"}")]
        [InlineData("[1,2]")]
        public void RepliesWithoutAnIssuesArray_AreNotAccepted(string reply)
            => Assert.Null(ReportReview.ParseReply(reply));

        [Fact]
        public void EmptyIssuesArray_IsAGenuineZero()
        {
            var parsed = ReportReview.ParseReply("{\"issues\":[],\"checked\":\"核對了所有數字\"}");
            Assert.NotNull(parsed);
            Assert.Empty(parsed!.Value.Issues);
        }

        [Fact]
        public void NonObjectItems_AreSkipped_InsteadOfThrowing()
        {
            var parsed = ReportReview.ParseReply("{\"issues\":[\"文字\",{\"severity\":\"中\",\"problem\":\"p\"}]}");
            Assert.Single(parsed!.Value.Issues);
        }

        [Theory]
        [InlineData(ReportReview.ReviewStatus.NotRun, "這次沒有呼叫審查模型")]
        [InlineData(ReportReview.ReviewStatus.Failed, "審查模型這次沒有回應")]
        [InlineData(ReportReview.ReviewStatus.Unparseable, "審查回覆無法解析")]
        public void IncompleteReview_NeverSaysNothingWasFound(ReportReview.ReviewStatus status, string expected)
        {
            var r = new ReportReview.Result(None, None, "gpt", "原因", "", "", status);

            string summary = ReportReview.Summary(r);
            string md = ReportReview.ToMarkdown("HW1.docx", Array.Empty<string>(), r);

            Assert.Contains(expected, summary);
            Assert.Contains("不代表報告已通過數字與文字核對", summary);
            Assert.DoesNotContain("沒有找到數字不符", summary);
            Assert.Contains(expected, md);
            Assert.Contains("不代表報告已通過數字與文字核對", md);
            Assert.DoesNotContain("沒有找到數字不符", md);
        }

        [Fact]
        public void ResultWithoutStatus_StillDetectsUnparseableOrMissingReviewer()
        {
            // 舊的呼叫方式（沒帶狀態）也不能被說成零問題
            Assert.Equal(ReportReview.ReviewStatus.Unparseable,
                new ReportReview.Result(None, None, "gpt", "", "", "{}").EffectiveStatus);
            Assert.Equal(ReportReview.ReviewStatus.NotRun,
                new ReportReview.Result(None, None, "", "", "", "").EffectiveStatus);
            Assert.Equal(ReportReview.ReviewStatus.Completed,
                new ReportReview.Result(None, None, "gpt", "", "", "{\"issues\":[]}").EffectiveStatus);
        }

        [Fact]
        public void CompletedWithZeroIssues_SaysSo()
        {
            var r = new ReportReview.Result(None, None, AiModels.OpenAi_Gpt56, "n", "c", "{\"issues\":[]}");
            Assert.Contains("沒有找到數字不符或前後矛盾", ReportReview.Summary(r));
        }

        [Fact]
        public void Coverage_ListsTruncationAndUnreviewedFiles()
        {
            string longReport = new string('字', ReportReview.MaxReportChars + 500);
            // 很多張「小」表合計超過上限（每張都不超過列數上限，所以是合計被截）
            var tables = Enumerable.Range(0, 60).Select(s => (Name: $"results.xlsx／表{s}",
                    Rows: (IReadOnlyList<IReadOnlyList<string>>)Enumerable.Range(0, 20)
                        .Select(i => (IReadOnlyList<string>)new[] { "列" + i, "1234567890" }).ToList()))
                .ToList();
            var code = new List<(string Name, string Code)> { ("hw1.py", new string('x', ReportReview.MaxCodeChars + 1000)) };

            var notes = ReportReview.Coverage(longReport, tables, code, new[] { "簡報.pptx" });

            Assert.Contains(notes, n => n.Contains($"報告超過 {ReportReview.MaxReportChars:N0} 字") && n.Contains("500"));
            Assert.Contains(notes, n => n.StartsWith($"結果表合計超過 {ReportReview.MaxTableChars:N0} 字"));
            Assert.Contains(notes, n => n.StartsWith($"程式碼合計超過 {ReportReview.MaxCodeChars:N0} 字"));
            Assert.Contains(notes, n => n.Contains("「簡報.pptx」沒有送審"));

            Assert.Empty(ReportReview.Coverage("短報告", null, null));

            // 截斷說明會出現在摘要與清單裡
            var r = new ReportReview.Result(None, None, AiModels.OpenAi_Gpt56, "n", "c", "{\"issues\":[]}",
                ReportReview.ReviewStatus.Completed, notes);
            Assert.Contains("部分內容沒有送審", ReportReview.Summary(r));
            string md = ReportReview.ToMarkdown("HW1.docx", Array.Empty<string>(), r);
            Assert.Contains("## 檢查範圍", md);
            Assert.Contains("「簡報.pptx」沒有送審", md);
        }

        private static (string Name, IReadOnlyList<IReadOnlyList<string>> Rows) Sheet(string name, int dataRows, string prefix = "ROW_") =>
            (name, Enumerable.Range(0, dataRows + 1)
                .Select(i => (IReadOnlyList<string>)new[] { i == 0 ? "header" : prefix + i, "1234567890" }).ToList());

        [Fact]
        public void WhenTablesDoNotFit_TheLongestSheetIsTrimmed_AndSummarySheetsStayWhole()
        {
            // 2026-11-04 HW1：逐筆預測表佔掉結果表的額度；第 4 次實測又把 51 列的係數表一起截到 30 列
            var tables = new List<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>
            {
                Sheet("results.xlsx／test_errors", 2000),
                Sheet("results.xlsx／coef", 51, "COEF_"),
                ("results.xlsx／model_comparison", new List<IReadOnlyList<string>>
                    { new[] { "Model", "R2" }, new[] { "M7", "0.874" } }),
            };

            string prompt = ReportReview.BuildPrompt("HW1.docx", "報告", tables, null);
            var notes = ReportReview.Coverage("報告", tables, null);

            Assert.Contains($"ROW_{ReportReview.MaxRowsPerSheet} ", prompt);
            Assert.DoesNotContain($"ROW_{ReportReview.MaxRowsPerSheet + 1} ", prompt);
            Assert.Contains($"這張表共 2,000 列，只送前 {ReportReview.MaxRowsPerSheet} 列", prompt);
            Assert.Contains("COEF_51 ", prompt);                                     // 51 列的摘要表整份送出
            Assert.Contains("M7 | 0.874", prompt);                                   // 排在後面的摘要表也送到了
            Assert.Contains(notes, n => n.Contains("「results.xlsx／test_errors」共 2,000 列") &&
                                        n.Contains($"只送了前 {ReportReview.MaxRowsPerSheet} 列"));
            Assert.DoesNotContain(notes, n => n.Contains("results.xlsx／coef」"));
            Assert.DoesNotContain(notes, n => n.StartsWith("結果表合計超過"));
        }

        [Fact]
        public void WhenTablesFit_NothingIsTrimmed()
        {
            // 第 4 次實測的結果表：最大一張 581 列，其他是 50 列上下的摘要表——整份都放得下就不截
            var tables = new List<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)>
            {
                Sheet("results.xlsx／M1_OLS係數", 51), Sheet("results.xlsx／RF特徵重要度", 51), Sheet("results.xlsx／原始缺值", 33),
            };

            Assert.Empty(ReportReview.Coverage("報告", tables, null));
            Assert.DoesNotContain("只送前", ReportReview.BuildPrompt("HW1.docx", "報告", tables, null));
        }

        [Fact]
        public void RowCount_ComesFromTheRealTotal_NotFromTheRowsThatWereRead()
        {
            // 2026-11-04 第 4 次實測的 bug：讀檔只讀 200 列，卻告訴審查「這張表共 199 列」（實際 581 列），
            // 審查兩輪都回報「測試集預測列數和測試集筆數不一致」——一個不存在的問題。
            var tables = new List<(string Name, IReadOnlyList<IReadOnlyList<string>> Rows)> { Sheet("results.xlsx／測試集預測", 199) };
            var total = new Dictionary<string, int> { ["results.xlsx／測試集預測"] = 582 };   // 含標題列

            string prompt = ReportReview.BuildPrompt("HW1.docx", "報告", tables, null, totalRows: total);
            var notes = ReportReview.Coverage("報告", tables, null, totalRows: total);

            Assert.Contains("這張表共 581 列，只送前 199 列", prompt);
            Assert.DoesNotContain("共 199 列", prompt);
            Assert.Contains(notes, n => n.Contains("「results.xlsx／測試集預測」共 581 列，只送了前 199 列"));
        }

        [Fact]
        public void SkippedSheets_AreListedInTheCoverage()
        {
            var notes = ReportReview.Coverage("報告", null, null, skippedSheets: new[] { "results.xlsx／第31張" });
            Assert.Contains(notes, n => n.Contains("工作表「results.xlsx／第31張」沒有送審") && n.Contains("張數超過讀取上限"));
        }

        [Fact]
        public void XlsxReader_ReportsTheRealRowCount_AndSheetsBeyondTheLimit()
        {
            string path = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N") + ".xlsx");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                WriteXlsx(path, ("預測", 582), ("摘要", 5), ("多的", 3));

                var book = ArtifactTextExtractor.ExtractXlsxSheetsWithCounts(path, maxSheets: 2, maxRowsPerSheet: 200);

                Assert.Equal(new[] { "預測", "摘要" }, book.Sheets.Select(s => s.Name));
                Assert.Equal(200, book.Sheets[0].Rows.Count);       // 只讀前 200 列
                Assert.Equal(582, book.TotalRows["預測"]);          // 但回報實際列數
                Assert.Equal(5, book.TotalRows["摘要"]);
                Assert.Equal(new[] { "多的" }, book.SkippedSheets);
                Assert.Equal(2, ArtifactTextExtractor.ExtractXlsxSheets(path, maxSheets: 2).Count);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void WriteXlsx(string path, params (string Name, int Rows)[] sheets)
        {
            using var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(
                path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();
            var list = wb.Workbook.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Sheets());
            uint id = 1;
            foreach (var (name, rows) in sheets)
            {
                var ws = wb.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
                var data = new DocumentFormat.OpenXml.Spreadsheet.SheetData();
                for (int r = 1; r <= rows; r++)
                {
                    var row = new DocumentFormat.OpenXml.Spreadsheet.Row { RowIndex = (uint)r };
                    row.Append(new DocumentFormat.OpenXml.Spreadsheet.Cell
                    {
                        CellReference = "A" + r,
                        DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString,
                        InlineString = new DocumentFormat.OpenXml.Spreadsheet.InlineString(
                            new DocumentFormat.OpenXml.Spreadsheet.Text("列" + r)),
                    });
                    data.Append(row);
                }
                ws.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(data);
                list.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
                {
                    Id = wb.GetIdOfPart(ws), SheetId = id++, Name = name,
                });
            }
        }

        [Fact]
        public void RecheckPrompt_IncludesTheWritersNote_ShortenedAndOnlyWithPreviousIssues()
        {
            // 2026-11-04 第 4 次實測：撰寫者查證後說「第 4 項問題不存在」，再審查看不到這段理由，又判「仍存在」
            var previous = new[] { new ReportReview.Issue("中", "測試集預測", "列數不一致", "", "") };
            const string note = "第 1 項：未修正，因為問題不存在。工作表讀回來是 581 列。";

            string p = ReportReview.BuildPrompt("HW1.docx", "報告", null, null, null, previous, note);
            Assert.Contains("=== 撰寫者對各項的處理說明（僅供參考", p);
            Assert.Contains(note, p);
            Assert.Contains("理由成立就標「已修正」", p);
            Assert.True(p.IndexOf(note, StringComparison.Ordinal) > p.IndexOf("=== 上一輪審查的問題", StringComparison.Ordinal));

            string longNote = new string('字', ReportReview.MaxWriterNoteChars + 100);
            string cut = ReportReview.BuildPrompt("HW1.docx", "報告", null, null, null, previous, longNote);
            Assert.Contains(new string('字', ReportReview.MaxWriterNoteChars) + "…", cut);
            Assert.DoesNotContain(new string('字', ReportReview.MaxWriterNoteChars + 1), cut);

            // 第一次審查沒有上一輪問題：說明不會出現
            Assert.DoesNotContain("撰寫者對各項的處理說明", ReportReview.BuildPrompt("HW1.docx", "報告", null, null, null, null, note));
        }

        [Theory]
        [InlineData(35_000)]   // 第 3 次實測的 HW1 程式
        [InlineData(50_000)]   // 第 4 次實測的 HW1 程式（原本上限 4 萬字，後段約 1 萬字沒送審）
        public void TypicalHomeworkCode_IsSentWhole(int chars)
        {
            var code = new List<(string Name, string Code)> { ("hw1.py", new string('x', chars)) };
            Assert.Empty(ReportReview.Coverage("報告", null, code));
        }

        [Fact]
        public void FullyReviewed_SaysNothingWasTruncated()
        {
            var r = new ReportReview.Result(None, None, AiModels.OpenAi_Gpt56, "n", "c", "{\"issues\":[]}");
            Assert.Contains("完整送審，沒有截斷", ReportReview.ToMarkdown("HW1.docx", Array.Empty<string>(), r));
            Assert.DoesNotContain("部分內容沒有送審", ReportReview.Summary(r));
        }

        [Fact]
        public void RecheckPrompt_ListsPreviousIssuesWithStableNumbers()
        {
            var previous = new[]
            {
                new ReportReview.Issue("中", "表 1", "缺值比例小數位不一致", "0.000", "改成 0.029"),
                new ReportReview.Issue("中", "第 8 節", "RidgeCV 寫成 5 折", "", ""),
            };

            string p = ReportReview.BuildPrompt("HW1.docx", "報告", null, null, null, previous);

            Assert.Contains("=== 上一輪審查的問題", p);
            Assert.Contains("1. ［中］表 1：缺值比例小數位不一致", p);
            Assert.Contains("2. ［中］第 8 節：RidgeCV 寫成 5 折", p);
            Assert.Contains("\"previous\":[{\"n\":編號", p);
            Assert.Contains("不要在 issues 重複列", p);
            Assert.DoesNotContain("上一輪審查的問題", ReportReview.BuildPrompt("HW1.docx", "報告", null, null));
        }

        [Fact]
        public void PreviousStatuses_MissingOrInvalidItems_AreUnknown_NotFixed()
        {
            var previous = new[] { new ReportReview.Issue("中", "a", "問題一", "", ""), new ReportReview.Issue("中", "b", "問題二", "", ""),
                                   new ReportReview.Issue("高", "c", "問題三", "", "") };
            string reply = "{\"issues\":[],\"previous\":[" +
                           "{\"n\":1,\"status\":\"已修正\",\"evidence\":\"現在寫 0.029%\"}," +
                           "{\"n\":2,\"status\":\"大概好了\",\"evidence\":\"\"}" +       // 不是三種狀態之一
                           "]}";                                                               // 第 3 項沒回

            var s = ReportReview.ParsePrevious(reply, previous);

            Assert.Equal(3, s.Count);
            Assert.Equal((1, "問題一", ReportReview.StatusFixed), (s[0].N, s[0].Problem, s[0].Status));
            Assert.Equal(ReportReview.StatusUnknown, s[1].Status);
            Assert.Equal(ReportReview.StatusUnknown, s[2].Status);
            Assert.Contains("沒有回覆", s[2].Evidence);
        }

        [Fact]
        public void RecheckList_ShowsPerIssueStatus_AndNewIssuesSeparately()
        {
            var r = new ReportReview.Result(
                new[] { new ReportReview.Issue("中", "表 3", "新的不一致", "e", "s") }, None,
                AiModels.OpenAi_Gpt56, "n", "c", "{\"issues\":[]}", ReportReview.ReviewStatus.Completed, null,
                new[]
                {
                    new ReportReview.PreviousStatus(1, "問題一", ReportReview.StatusFixed, "現在寫 0.029%"),
                    new ReportReview.PreviousStatus(2, "問題二", ReportReview.StatusRemains, "仍寫 5 折"),
                });

            string md = ReportReview.ToMarkdown("HW1.docx", Array.Empty<string>(), r);

            Assert.Contains("## 上一輪問題的處理狀態", md);
            Assert.Contains("已修正 1 項、仍存在 1 項、無法確認 0 項", md);
            Assert.Contains("| 2 | 問題二 | 仍存在 | 仍寫 5 折 |", md);
            Assert.Contains("## 這一輪新發現的問題", md);
            Assert.Contains("新的不一致", md);
        }

        [Fact]
        public void Summary_AfterAutoFix_DoesNotSayTheReportWasUnchanged()
        {
            var r = new ReportReview.Result(new[] { new ReportReview.Issue("中", "a", "p", "", "") }, None,
                AiModels.OpenAi_Gpt56, "n", "c", "{\"issues\":[]}");
            Assert.Contains("沒有被修改", ReportReview.Summary(r));
            Assert.DoesNotContain("沒有被修改", ReportReview.Summary(r, reportUnchanged: false));
        }
    }
}
