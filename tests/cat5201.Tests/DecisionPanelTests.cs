using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    public class DecisionPanelTests
    {
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
