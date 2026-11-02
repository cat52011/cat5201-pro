using System.Collections.Generic;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    public class DecisionPanelTests
    {
        /// <summary>
        /// 手動選 Claude 的那一次，決策窗不得出現使用者沒選的模型。
        ///
        /// 2026-10-29 使用者回報：關掉 Auto 後「模型」欄仍顯示 Perplexity Sonar。
        /// 修好之後，搜尋產物改掛真正跑的模型 id（anthropic/claude-sonnet-5-5）——
        /// 但那不是名錄裡的 id，查不到會退回預設模型的名字，於是變成「Claude Sonnet 5.5 + GPT-5.6 Sol」，
        /// 同一個問題換一條路重新出現。這條測試釘住整條顯示路徑，而不只是其中一段。
        /// </summary>
        [Fact]
        public void ManualClaudeRun_ShowsOnlyClaude()
        {
            var log = new AiExecutionLogEntry
            {
                SelectionMode = "Manual",
                RequestedModelId = AiModels.Claude_Sonnet5,
                PlannedModelId = AiModels.Claude_Sonnet5,
                ActualModelId = AiModels.Claude_Sonnet5,
                Success = true,
                WorkspaceArtifacts = new List<AgentWorkspaceArtifactRecord>
                {
                    new() { ArtifactKind = "facts", ItemType = "verified_facts", ModelId = "anthropic/claude-sonnet-5-5" },
                    new() { ArtifactKind = "search", ItemType = "search_summary", ModelId = "anthropic/claude-sonnet-5-5" },
                },
                UsageRecords = new List<UsageRecord>
                {
                    new() { Kind = UsageKinds.Llm, ModelId = AiModels.Claude_Sonnet5, Purpose = "輸出意圖判斷", InputTokens = 900, OutputTokens = 60 },
                    new() { Kind = UsageKinds.Llm, ModelId = "anthropic/claude-sonnet-5-5", Purpose = "搜尋研究", InputTokens = 12000, OutputTokens = 2000 },
                    new() { Kind = UsageKinds.Llm, ModelId = AiModels.Claude_Sonnet5, Purpose = "主回覆", InputTokens = 8000, OutputTokens = 1500 },
                },
            };

            var view = NodeDecisionViewBuilder.BuildFromLog(log);

            // 「模型」欄：只有 Claude。
            Assert.Equal("Claude Sonnet 5.5", view.Model);

            // 「參與」欄：還是只有 Claude，並照實寫出搜尋是透過 Perplexity 的服務做的。
            Assert.DoesNotContain("GPT", view.Participants);
            Assert.DoesNotContain("Sonar", view.Participants);
            Assert.DoesNotContain("openai", view.Participants);
            Assert.Contains("Claude Sonnet 5.5 經 Perplexity（搜尋研究）", view.Participants);
        }

        /// <summary>
        /// 決策窗的「模型」欄只是這次選了誰；一份簡報實際上是搜尋 + 撰寫 + 配圖多方參與。
        /// 「這次實際參與」必須照逐筆帳目把每一個模型與它做的事講出來，不能只剩最後那個模型。
        /// </summary>
        [Fact]
        public void ParticipantsRow_ListsEveryModelThatActuallyRan()
        {
            var log = new AiExecutionLogEntry
            {
                PlannedModelId = AiModels.Claude_Sonnet5,
                ActualModelId = AiModels.Claude_Sonnet5,
                Success = true,
                UsageRecords = new List<UsageRecord>
                {
                    new() { Kind = UsageKinds.Search, ModelId = "pplx-sonar", Purpose = "搜尋研究", UsdCost = 0.005 },
                    new() { Kind = UsageKinds.Llm, ModelId = AiModels.Claude_Sonnet5, Purpose = "簡報撰寫", InputTokens = 900, OutputTokens = 600 },
                    new() { Kind = UsageKinds.Llm, ModelId = AiModels.Claude_Sonnet5, Purpose = "配圖企劃", InputTokens = 300, OutputTokens = 120 },
                    new() { Kind = UsageKinds.Image, ModelId = "gpt-image-2", Purpose = "簡報封面圖", Quantity = 1, Unit = "張", UsdCost = 0.25 },
                },
            };

            string participants = NodeDecisionViewBuilder.BuildFromLog(log).Participants;

            Assert.Contains("搜尋研究", participants);
            Assert.Contains("簡報撰寫", participants);
            Assert.Contains("配圖企劃", participants);
            Assert.Contains("gpt-image-2", participants);
            Assert.Contains("簡報封面圖", participants);

            // 同一個模型只列一次，用途合併在後面。
            Assert.Equal(3, participants.Split(" · ").Length);
        }

        [Fact]
        public void ParticipantsRow_IsDashWhenNothingRanYet()
            => Assert.Equal("-", NodeDecisionViewBuilder.BuildFromLog(new AiExecutionLogEntry()).Participants);
    }
}
