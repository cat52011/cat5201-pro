using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 稽查資料的誠實性：顯示出來的數字與紀錄，必須就是當時真的發生的事。
    /// 這兩條都是 2026-10-27 第三方審查實際重現出來的問題。
    /// </summary>
    // UsageMeter.RecordLlm 會寫進全域 SpendLedger（Commit → SpendLedger.Add），
    // 所以必須跟其他碰帳本的測試在同一個集合裡循序跑，否則會干擾它們的金額斷言。
    [Collection("spend-ledger")]
    public class AuditIntegrityTests
    {
        // ───────── token 口徑 ─────────

        /// <summary>
        /// 「實際 token」就是實際處理過的量；快取的計價倍率不可以混進去。
        /// 原本：輸入 100 ＋ 快取讀 1,000 ＋ 輸出 10 會顯示成「實際 210 tokens」——
        /// 210 是計費等值數，跨模型比較與節省比例全都會算錯。
        /// </summary>
        [Fact]
        public void CacheTokensAreReportedSeparatelyFromRealInput()
        {
            UsageMeter.BeginScope();

            var record = UsageMeter.RecordLlm(
                "claude-sonnet-5",
                actualInputTokens: 100,
                actualOutputTokens: 10,
                cacheReadTokens: 1000,
                cacheWriteTokens: 200);

            Assert.NotNull(record);
            Assert.Equal(100, record!.InputTokens);        // 未命中快取的輸入，原值
            Assert.Equal(10, record.OutputTokens);
            Assert.Equal(1000, record.CacheReadTokens);    // 分開記，不加權
            Assert.Equal(200, record.CacheWriteTokens);
            Assert.Equal(1300, record.TotalInputTokens);   // 實際處理過的輸入總量

            // 計價才套倍率：讀取 0.1 倍、寫入 1.25 倍。
            var price = ModelCostEstimator.FromUsage("claude-sonnet-5", 1, 0).UsdCost;
            double expected =
                100 * price
                + 1000 * price * ModelCostEstimator.CacheReadMultiplier("claude-sonnet-5")
                + 200 * price * ModelCostEstimator.CacheWriteMultiplier
                + ModelCostEstimator.FromUsage("claude-sonnet-5", 0, 10).UsdCost;

            Assert.Equal(expected, record.UsdCost, precision: 6);
        }

        /// <summary>摘要層的總量也要算進快取，否則「用了多少」會看起來比實際少。</summary>
        [Fact]
        public void SummaryCountsCacheTokensInTheTotal()
        {
            var records = new List<UsageRecord>
            {
                new()
                {
                    Kind = UsageKinds.Llm, ModelId = "claude-sonnet-5",
                    InputTokens = 100, OutputTokens = 10,
                    CacheReadTokens = 1000, CacheWriteTokens = 200, UsdCost = 0.01,
                },
            };

            var summary = UsageSummary.From(records);

            Assert.Equal(100, summary.InputTokens);
            Assert.Equal(1000, summary.CacheReadTokens);
            Assert.Equal(200, summary.CacheWriteTokens);
            Assert.Equal(1310, summary.TotalTokens);
        }

        // ───────── 歷史紀錄 ─────────

        private static MethodInfo Mapper(string name)
        {
            var m = typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return m!;
        }

        /// <summary>
        /// 執行紀錄是「當時發生了什麼」。把舊世代模型 ID 轉成後繼模型是路由用的正規化，
        /// 套在歷史上就是竄改稽查資料——而且逐筆用量仍保留原 ID，同一份紀錄會自相矛盾。
        /// </summary>
        [Theory]
        [InlineData("gpt-5.5")]
        [InlineData("claude-sonnet-4-6")]
        [InlineData("claude-opus-5")]
        public void LoadingHistoryKeepsTheModelThatActuallyRan(string historicalModel)
        {
            // 先確認這個 ID 真的會被路由正規化改掉——否則這個測試什麼也沒驗到。
            Assert.NotEqual(historicalModel, AiModelHelper.NormalizeNodeModel(historicalModel));

            var entry = new AiExecutionLogEntry
            {
                NodeId = "n1",
                RequestedModelId = historicalModel,
                PlannedModelId = historicalModel,
                ActualModelId = historicalModel,
                CapabilityRequestedModelId = historicalModel,
                CapabilityResolvedModelId = historicalModel,
                EffectivePreferences = "回答用繁體中文",
                OutputIntentSummary = "簡報",
                MemoryRecall = MemoryRecallStats.Empty,
            };

            var state = Mapper("ToExecutionLogState").Invoke(null, new object[] { entry });
            var loaded = (AiExecutionLogEntry)Mapper("ToExecutionLogEntry").Invoke(null, new[] { state })!;

            Assert.Equal(historicalModel, loaded.ActualModelId);
            Assert.Equal(historicalModel, loaded.RequestedModelId);
            Assert.Equal(historicalModel, loaded.PlannedModelId);
            Assert.Equal(historicalModel, loaded.CapabilityRequestedModelId);
            Assert.Equal(historicalModel, loaded.CapabilityResolvedModelId);
        }

        /// <summary>
        /// 召回的記憶、當時生效的偏好、輸出意圖判斷：執行當下就產生了，存檔卻沒帶上，
        /// 重開專案後決策窗就少了這三塊。對主打可追溯的產品，這是核心資料遺失。
        /// </summary>
        [Fact]
        public void AuditFieldsSurviveSaveAndLoad()
        {
            var entry = new AiExecutionLogEntry
            {
                NodeId = "n1",
                ActualModelId = "claude-sonnet-5",
                EffectivePreferences = "回答用繁體中文；簡潔一點",
                OutputIntentSummary = "判定為簡報產出（LLM 判斷，信心 0.9）",
            };

            var state = Mapper("ToExecutionLogState").Invoke(null, new object[] { entry });
            var loaded = (AiExecutionLogEntry)Mapper("ToExecutionLogEntry").Invoke(null, new[] { state })!;

            Assert.Equal(entry.EffectivePreferences, loaded.EffectivePreferences);
            Assert.Equal(entry.OutputIntentSummary, loaded.OutputIntentSummary);
            Assert.NotNull(loaded.MemoryRecall);
        }
    }
}
