using System;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// AI 使用紀錄（2026-11-03 使用者要求）：誰做了哪一段、哪些決策是誰提的。
    /// 內容全由執行紀錄組成，不經 AI 改寫。
    /// </summary>
    public class AiUsageDisclosureTests
    {
        private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        private static AiExecutionLogEntry Log(string mode, params UsageRecord[] records) => new()
        {
            SelectionMode = mode,
            RequestedModelId = AiModels.Claude_Sonnet5,
            PlannedModelId = AiModels.Claude_Sonnet5,
            ActualModelId = AiModels.Claude_Sonnet5,
            Resolver = "API Auto",
            ResolverReason = "需要讀資料檔與寫報告",
            StartedAtUtc = T0,
            DurationMs = 264_000,
            Success = true,
            OutputIntentSummary = "書面報告",
            UsageRecords = records,
            WorkspaceArtifacts = new[]
            {
                new AgentWorkspaceArtifactRecord
                {
                    FilePath = @"C:\out\HW1_20261103.docx", SourceAgentId = "report-agent", ModelId = AiModels.Claude_Sonnet5,
                },
                new AgentWorkspaceArtifactRecord
                {
                    FilePath = @"C:\out\HW1（審查清單）_20261103.md", SourceAgentId = "review-agent", ModelId = AiModels.OpenAi_Gpt56,
                },
            },
            StepInputs = new[]
            {
                new StepInputRecord { Step = "主回覆", ModelId = AiModels.Claude_Sonnet5, Chars = 1200, Sha256 = "abcdef0123456789", ContainsOriginalRequest = true },
            },
        };

        private static UsageRecord Rec(int sec, string purpose, string model, double usd, bool actual = true) => new()
        {
            AtUtc = T0.AddSeconds(sec), Purpose = purpose, ModelId = model, UsdCost = usd, IsActual = actual,
            InputTokens = 1000, OutputTokens = 200,
        };

        [Fact]
        public void AutoRun_SaysTheSystemChoseTheModel_AndWhoDidEachStep()
        {
            var log = Log("API Auto",
                Rec(0, "模型自動選擇", AiModels.OpenAi_Gpt56, 0.004),
                Rec(1, "輸出意圖判斷", AiModels.OpenAi_Gpt56, 0.002),
                Rec(5, "資料分析（沙箱）", AiModels.Claude_Sonnet5, 0.49),
                Rec(270, "獨立審查", AiModels.OpenAi_Gpt56, 0.08, actual: false));

            string md = AiUsageDisclosure.Build(log, "完成此作業並說明", new DateTime(2026, 11, 3, 21, 0, 0));

            Assert.Contains("沒有經過 AI 改寫", md);
            Assert.Contains("> 完成此作業並說明", md);
            Assert.Contains("| 執行模式 | Auto（模型判斷） | 你（節點設定） |", md);
            Assert.Contains("| 回答的模型 | Claude Sonnet", md);
            Assert.Contains("系統（GPT", md);                    // 由哪個模型判斷的
            Assert.Contains("需要讀資料檔與寫報告", md);          // 依據
            Assert.Contains("| 要交出什麼 | 書面報告 |", md);
            Assert.Contains("資料分析（沙箱）", md);
            Assert.Contains("0.0800（估算）", md);
            Assert.Contains("**0.5760**", md);                   // 合計
            Assert.Contains("HW1（審查清單）_20261103.md", md);
            Assert.Contains("| 審查 |", md);
            Assert.Contains("review-agent", md);
            Assert.Contains("| 主回覆 |", md);
            Assert.Contains("abcdef012345", md);
            Assert.Contains("4.4 分鐘", md);
        }

        [Fact]
        public void ManualRun_SaysYouChoseTheModel()
        {
            string md = AiUsageDisclosure.Build(Log("Manual", Rec(0, "主回覆", AiModels.Claude_Sonnet5, 0.02)), "寫一份報告");

            Assert.Contains("| 執行模式 | 手動 |", md);
            Assert.Contains("你（手動選擇）", md);
            Assert.DoesNotContain("系統（GPT", md);
        }

        [Fact]
        public void ModelSwitch_IsAttributedToTheSystem_WithTheReason()
        {
            var log = new AiExecutionLogEntry
            {
                SelectionMode = "Manual",
                RequestedModelId = AiModels.Claude_Sonnet5,
                ActualModelId = AiModels.OpenAi_Gpt56,
                RuntimeFallbackUsed = true,
                RuntimeFallbackSummary = "Claude Sonnet 5.5 逾時，改由 GPT-5.6 完成",
                Success = true,
            };

            string md = AiUsageDisclosure.Build(log, "x");
            Assert.Contains("| 換模型 | Claude Sonnet 5.5 逾時，改由 GPT-5.6 完成 | 系統（原本的模型執行失敗） |", md);
        }

        [Fact]
        public void PipesInContent_DoNotBreakTheTables()
        {
            var log = Log("Manual", Rec(0, "主回覆 | 第二段", AiModels.Claude_Sonnet5, 0.01));
            string md = AiUsageDisclosure.Build(log, "x");
            Assert.Contains("主回覆 ／ 第二段", md);
        }
    }
}
