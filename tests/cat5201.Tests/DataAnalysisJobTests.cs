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
        public void SystemPrompt_SaysNotToCopyTemplateInstructions()
        {
            // 2026-11-04 HW1：標題照抄了範本給學生的提示「1. 資料清理與視覺化圖表 (參考程式碼Part 1)」
            Assert.Contains("寫給學生的說明、提示與示範文字不要照抄進報告", DataAnalysisJob.SystemPrompt);
            Assert.Contains("(參考程式碼Part 1)", DataAnalysisJob.SystemPrompt);
            Assert.Contains("標題只保留章節名稱", DataAnalysisJob.SystemPrompt);
        }

        [Fact]
        public void SystemPrompt_AsksForCodeThatRunsOnNewerLibraries()
        {
            // 2026-11-04 GPT 審查 HW1：LassoCV(n_alphas=60) 在沙箱跑得過，scikit-learn 1.9 會報錯
            string p = DataAnalysisJob.SystemPrompt;
            Assert.Contains("沙箱的套件版本比使用者電腦上的舊", p);
            Assert.Contains("alphas=np.logspace(-4, 1, 60)", p);
            Assert.Contains("不要用 n_alphas", p);
            Assert.Contains("np.sqrt(mean_squared_error(...))", p);
        }

        [Fact]
        public void SystemPrompt_AsksToCheckWhetherClippingCausesTheBiggestErrors()
        {
            // 2026-11-04 GPT 審查 HW1：面積截在 497 ㎡，9 筆大物件佔 M2 測試誤差平方和 84%，報告卻歸因於對數還原
            string p = DataAnalysisJob.SystemPrompt;
            Assert.Contains("對主要預測變數（例如面積、規模、金額）做截尾或設上限時", p);
            Assert.Contains("誤差最大的案件", p);
            Assert.Contains("先檢查前處理", p);
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

        // ── 2026-11-04：HW1 實測後補的方法規則與交付檔名 ──

        [Fact]
        public void SystemPrompt_KeepsTheTestSetForTheFinalScoreOnly_AndFitsPreprocessingOnTrainingData()
        {
            // HW1 用測試集 RMSE 選最佳模型；報告寫懲罰對各特徵公平，但 0/1 欄沒有標準化。
            string p = DataAnalysisJob.SystemPrompt;
            Assert.Contains("測試集只用來報告最後結果", p);
            Assert.Contains("只能用交叉驗證或驗證集", p);
            Assert.Contains("離群值門檻、補值、標準化都只用訓練集決定", p);
            Assert.Contains("最常見的類別當基準組", p);
            Assert.Contains("由程式依最後實際使用的資料算出的變數產生", p);
            Assert.Contains("不可宣稱正規化的懲罰「對各特徵公平」", p);
        }

        [Fact]
        public void SystemPrompt_DoesEveryListedItem_AndRegularizedVariantsDoNotCountTowardTheModelLimit()
        {
            // HW1 因為「模型最多 4 個」省略了作業要求的 Ridge。
            string p = DataAnalysisJob.SystemPrompt;
            Assert.Contains("作業列出的每個項目都要做到", p);
            Assert.Contains("RidgeCV、LassoCV", p);
            Assert.Contains("不受 4 個模型的限制", p);
            Assert.DoesNotContain("作業沒有指定時，模型最多 4 個", p);
        }

        [Fact]
        public void SystemPrompt_AsksForSanityChecks_OnDerivedFeatures_AndDropsConstantColumns()
        {
            // 2026-11-04 第三次 HW1：交易年月日乘 100 再拆，屋齡變成約 11,400 年、月份最大 31；
            // 另有整欄為 0 的「鄉鎮市區=其他」進了 OLS。兩個都是審查才抓到，沙箱自己檢查一下就能擋下。
            string p = DataAnalysisJob.SystemPrompt;
            Assert.Contains("用 assert 檢查範圍合理", p);
            Assert.Contains("月份在 1～12、比例在 0～1、百分比在 0～100", p);
            Assert.Contains("分項加總等於合計", p);
            Assert.Contains("7 碼 yyymmdd", p);
            Assert.Contains("刪除整欄都是同一個值的變數", p);
            Assert.Contains("檢查不過就讓程式報錯，修好再跑", p);
        }

        [Fact]
        public void SystemPrompt_ModelingRulesApplyOnlyWhenThereIsAModel()
        {
            // 2026-11-04 使用者要求規則要適用其他作業：描述統計、問卷分析這類作業不該被逼著切訓練集與測試集。
            string p = DataAnalysisJob.SystemPrompt;
            int general = p.IndexOf("分析方法規則", System.StringComparison.Ordinal);
            int modeling = p.IndexOf("有建立預測或分類模型、或要比較多個模型時，另外遵守", System.StringComparison.Ordinal);
            Assert.True(general >= 0 && modeling > general);

            // 一般規則在建模規則之前；測試集、One-Hot、4 個模型的規則都在建模那一段裡。
            Assert.InRange(p.IndexOf("作業列出的每個項目都要做到", System.StringComparison.Ordinal), general, modeling);
            Assert.InRange(p.IndexOf("報告寫的做法要和程式實際做的一致", System.StringComparison.Ordinal), general, modeling);
            Assert.InRange(p.IndexOf("不需要切訓練集與測試集", System.StringComparison.Ordinal), general, modeling);
            Assert.True(p.IndexOf("測試集只用來報告最後結果", System.StringComparison.Ordinal) > modeling);
            Assert.True(p.IndexOf("以最常見的類別當基準組", System.StringComparison.Ordinal) > modeling);
            Assert.True(p.IndexOf("不受 4 個模型的限制", System.StringComparison.Ordinal) > modeling);
        }

        [Fact]
        public void Prompt_TellsTheSandboxTheDeliveredFileNames()
        {
            string prompt = DataAnalysisJob.BuildUserPrompt(new DataAnalysisJob.Request
            {
                UserInput = "完成此作業",
                DataFiles = new[] { "file_1.csv" },
                WantsReport = true,
                ReportFileName = "臺北市房價迴歸.docx",
                TableFileName = "臺北市房價迴歸（結果表）.xlsx",
            });

            Assert.Contains("檔名必須是「臺北市房價迴歸.docx」", prompt);
            Assert.Contains("檔名必須是「臺北市房價迴歸（結果表）.xlsx」", prompt);
            Assert.Contains(".py 存檔時用同樣的檔名", prompt);
        }

        [Fact]
        public void Prompt_WithoutFileNames_DoesNotMentionThem()
        {
            string prompt = DataAnalysisJob.BuildUserPrompt(new DataAnalysisJob.Request
            {
                UserInput = "完成此作業",
                DataFiles = new[] { "file_1.csv" },
                WantsReport = true,
            });

            Assert.DoesNotContain("檔名必須是", prompt);
            Assert.Contains("- 報告 .docx：依要求", prompt);
        }

        [Fact]
        public void PlannedFileNames_MatchWhatTheWriterSaves()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cat5201-tests", System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string table = DataAnalysisJob.TableTitle("HW1/房價", wantsTable: false);
                Assert.Equal("HW1/房價（結果表）", table);
                Assert.Equal("HW1（表格）", DataAnalysisJob.TableTitle("HW1", wantsTable: true));

                string plannedDocx = GeneratedFileWriter.PlannedFileName("HW1/房價", ".docx");
                string plannedXlsx = GeneratedFileWriter.PlannedFileName(table, ".xlsx");
                Assert.Equal("HW1_房價.docx", plannedDocx);

                var docx = GeneratedFileWriter.WriteDocx(dir, "HW1/房價", new byte[] { 1 }, "");
                var xlsx = GeneratedFileWriter.WriteXlsx(dir, table, new byte[] { 1 }, "");
                Assert.Equal(plannedDocx, docx.FileName);
                Assert.Equal(plannedXlsx, xlsx.FileName);
            }
            finally
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void UnusedTitle_SkipsNamesAlreadyTaken_SoTheWriterDoesNotRenameLater()
        {
            // 同一份作業再跑一次：沙箱被告知的檔名不能是已經存在的檔案，否則寫檔時會被改成 _2，又和報告對不上。
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cat5201-tests", System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                System.Collections.Generic.IEnumerable<string> Names(string t) => new[]
                {
                    GeneratedFileWriter.PlannedFileName(t, ".docx"),
                    GeneratedFileWriter.PlannedFileName(DataAnalysisJob.TableTitle(t, false), ".xlsx"),
                };

                Assert.Equal("HW1", GeneratedFileWriter.UnusedTitle(dir, "HW1", Names));

                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "HW1（結果表）.xlsx"), new byte[] { 1 });
                string title = GeneratedFileWriter.UnusedTitle(dir, "HW1", Names);
                Assert.Equal("HW1_2", title);

                var xlsx = GeneratedFileWriter.WriteXlsx(dir, DataAnalysisJob.TableTitle(title, false), new byte[] { 1 }, "");
                Assert.Equal("HW1_2（結果表）.xlsx", xlsx.FileName);
            }
            finally
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }
}
