using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-03：附了資料檔又要交報告的作業，改在 Claude 的沙箱裡實際執行（DataAnalysisJob）。
    /// 這裡只測不需要連網的部分——沙箱那一趟要花錢，不在單元測試裡跑。
    /// </summary>
    public class DataAnalysisJobTests
    {
        private static readonly OutputIntent Report = new() { WantsReport = true };

        [Fact]
        public void Applies_OnlyWithDataFile_AndADocumentDeliverable()
        {
            Assert.True(DataAnalysisJob.Applies(new[] { "115Taipei.csv", "HW1作業範本.docx" }, Report));
            Assert.True(DataAnalysisJob.Applies(new[] { "sales.xlsx" }, new OutputIntent { WantsTable = true }));

            Assert.False(DataAnalysisJob.Applies(new[] { "HW1作業範本.docx" }, Report));            // 沒有資料檔
            Assert.False(DataAnalysisJob.Applies(new[] { "115Taipei.csv" }, OutputIntent.None));    // 只要文字回答
            Assert.False(DataAnalysisJob.Applies(new[] { "115Taipei.csv" }, null));
        }

        [Fact]
        public void Deliverables_AlwaysIncludeTheProgram_AndTheResultTable()
        {
            // 結果表沒被要求也收：報告會引用它（2026-11-03 報告寫「完整表格見 hw1_results.xlsx」，檔案卻被丟掉）。
            Assert.Equal(new[] { ".docx", ".xlsx", ".py" }, DataAnalysisJob.WantedExtensions(true, false));
            Assert.Equal(new[] { ".docx", ".xlsx", ".py" }, DataAnalysisJob.WantedExtensions(true, true));
            Assert.Equal(new[] { ".xlsx", ".py" }, DataAnalysisJob.WantedExtensions(false, true));
        }

        [Theory]
        [InlineData("115Taipei.csv", 1, "115Taipei.csv")]                          // 本來就安全：不改、不加序號
        [InlineData("HW1作業範本.docx", 2, "HW1_2.docx")]
        [InlineData("HW1欄位說明.docx", 3, "HW1_3.docx")]                            // 跟上一個不會撞名
        [InlineData("螢幕擷取畫面 2026-11-03 154555.png", 4, "2026-11-03_154555_4.png")]
        [InlineData("台北房價.csv", 5, "file_5.csv")]
        public void UploadNames_AreAsciiOnly(string original, int index, string expected)
        {
            // 2026-11-03：中文檔名上傳全被 Files API 拒絕（400 forbidden character），截圖和範本都沒進沙箱。
            Assert.Equal(expected, ClaudeFilesApi.SafeUploadName(original, index));
        }

        [Fact]
        public void Prompt_MapsSandboxNamesBackToTheOriginals()
        {
            string prompt = DataAnalysisJob.BuildUserPrompt(new DataAnalysisJob.Request
            {
                UserInput = "完成此作業",
                DataFiles = new[] { "file_1.csv" },
                Images = new[] { "2026-11-03_154555_3.png" },
                Templates = new[] { "HW1_2.docx" },
                OriginalNames = new Dictionary<string, string>
                {
                    ["file_1.csv"] = "台北房價.csv",
                    ["HW1_2.docx"] = "HW1作業範本.docx",
                    ["2026-11-03_154555_3.png"] = "螢幕擷取畫面 2026-11-03 154555.png",
                },
                WantsReport = true,
            });

            Assert.Contains("- file_1.csv（使用者電腦上的原檔名：台北房價.csv）", prompt);
            Assert.Contains("- HW1_2.docx（使用者電腦上的原檔名：HW1作業範本.docx）", prompt);
            Assert.Contains("【也放進沙箱的範本檔】", prompt);
            Assert.Contains("原檔名：螢幕擷取畫面", prompt);
            Assert.Contains("結果表 .xlsx（選擇性）", prompt);
        }

        [Fact]
        public void SystemPrompt_KeepsTheSandboxRunShort()
        {
            // 2026-11-03 第二次實測：沙箱跑滿 14 分鐘沒做完。一支程式跑一次、不調參。
            Assert.Contains("執行一次就產出所有檔案", DataAnalysisJob.SystemPrompt);
            Assert.Contains("GridSearchCV", DataAnalysisJob.SystemPrompt);
            Assert.DoesNotContain("再執行驗證", DataAnalysisJob.SystemPrompt);
            Assert.True(DataAnalysisJob.TimeBudget < ClaudeDocumentSkillsService.DefaultBudget);
        }

        [Fact]
        public void TimedOutMessage_SaysItStopped_AndWhatItCost()
        {
            string text = DataAnalysisJob.TimedOutMessage(System.TimeSpan.FromMinutes(12), 0.734);
            Assert.Contains("超過 12 分鐘", text);
            Assert.Contains("US$0.73", text);
            Assert.Contains("再執行一次", text);
        }

        [Fact]
        public void NoFilesMessage_SaysItStopped_InsteadOfFallingBack()
        {
            string text = DataAnalysisJob.NoFilesMessage(0.703);
            Assert.Contains("沒有拿回檔案", text);
            Assert.Contains("US$0.70", text);
            Assert.Contains("沒有改用一般流程", text);
        }

        [Fact]
        public void SystemPrompt_AsksForCodeThatRunsOnNewerPandas()
        {
            // 2026-11-03 那支 .py 用 to_excel(w, "sheet") 的舊寫法，在 pandas 3 直接 TypeError。
            Assert.Contains("sheet_name=", DataAnalysisJob.SystemPrompt);
            Assert.Contains("原檔名", DataAnalysisJob.SystemPrompt);
            Assert.Contains("沒有交付的檔案不要在報告裡引用", DataAnalysisJob.SystemPrompt);
        }

        [Fact]
        public void Prompt_CarriesTheRequirements_AndKeepsDocumentsShort()
        {
            string prompt = DataAnalysisJob.BuildUserPrompt(new DataAnalysisJob.Request
            {
                UserInput = "完成此作業並說明",
                DataFiles = new[] { "115Taipei.csv" },
                Images = new[] { "作業說明.png" },
                Documents = new[] { ("HW1作業範本.docx", "Part I: 1. 資料清理與視覺化圖表 " + new string('字', 10000)) },
                WantsReport = true,
            });

            Assert.Contains("完成此作業並說明", prompt);
            Assert.Contains("- 115Taipei.csv", prompt);
            Assert.Contains("沒有截斷", prompt);
            Assert.Contains("- 作業說明.png", prompt);
            Assert.Contains("【附件：HW1作業範本.docx】", prompt);
            Assert.Contains("Part I: 1. 資料清理與視覺化圖表", prompt);
            Assert.True(prompt.Length < 10000, "範本內容要截短，不是整份放進提示");
            Assert.Contains("報告 .docx", prompt);
            Assert.Contains("程式 .py", prompt);
        }

        [Fact]
        public void SystemPrompt_ForbidsMadeUpNumbers_AndExplainsTheFontLimit()
        {
            Assert.Contains("不得估計、不得編造", DataAnalysisJob.SystemPrompt);
            Assert.Contains("圖上的標題、座標軸、圖例一律用英文", DataAnalysisJob.SystemPrompt);
            Assert.Contains("個人資料一律留空白", DataAnalysisJob.SystemPrompt);
            Assert.Contains("不要出現 nan", DataAnalysisJob.SystemPrompt);   // 2026-11-03 兩次實測比較表都有 nan
            Assert.Contains(ClaudeDocumentSkillsService.DeliverRule, DataAnalysisJob.SystemPrompt);
        }

        [Fact]
        public void ImagesAreShownToClaude_DataGoesIntoTheContainer()
        {
            var content = ClaudeDocumentSkillsService.BuildFirstMessageContent("做分析", null, new[]
            {
                new ClaudeDocumentSkillsService.ContainerFile("file_csv", "115Taipei.csv"),
                new ClaudeDocumentSkillsService.ContainerFile("file_png", "作業說明.png", AsImage: true),
            });

            var blocks = content.OfType<JsonObject>().ToList();
            Assert.Equal("container_upload", (string?)blocks[0]["type"]);
            Assert.Equal("file_csv", (string?)blocks[0]["file_id"]);
            Assert.Equal("image", (string?)blocks[1]["type"]);
            Assert.Equal("file_png", (string?)blocks[1]["source"]!["file_id"]);
            Assert.Equal("text", (string?)blocks[2]["type"]);
        }

        [Fact]
        public void LargeCsv_IsTrimmedToHeaderAndFirstRows_WithTheTotalRowCount()
        {
            string csv = "鄉鎮市區,總價元\n" + string.Join("\n", Enumerable.Range(1, 3440).Select(i => $"大安區,{i * 10000}"));

            string trimmed = AttachmentTextCache.TrimDataFile(csv);

            Assert.StartsWith("鄉鎮市區,總價元\n", trimmed);
            Assert.True(trimmed.Length < AttachmentTextCache.MaxCharsForDataFile + 200);
            Assert.Contains("整份共 3441 行", trimmed);
            Assert.Contains("不能拿來計算整份資料的統計數字", trimmed);
        }

        [Fact]
        public void SmallCsv_IsUnchanged()
        {
            string csv = "a,b\n1,2\n3,4";
            Assert.Equal(csv, AttachmentTextCache.TrimDataFile(csv));
        }
    }
}
