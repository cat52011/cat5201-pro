using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 決策窗說人話 + API 即時狀態（2026-09-24）：
    /// 使用者 2026-09-24 那次失敗只有日誌裡看得到「credit balance is too low」，介面什麼都沒說。
    /// </summary>
    [Collection("SpendLedger")]
    public class ApiStatusTests
    {
        [Theory]
        [InlineData("Your credit balance is too low to access the Anthropic API.", "AI 服務的餘額用完了")]
        [InlineData("401 {\"error\":{\"message\":\"invalid x-api-key\"}}", "金鑰無效或沒有權限")]
        [InlineData("No such host is known. (api.openai.com:443)", "連不上網路或服務暫時無法連線")]
        [InlineData("429 rate limit exceeded", "呼叫太頻繁或額度已達上限")]
        [InlineData("model_not_found: claude-opus-9", "這個模型目前不可用")]
        [InlineData("prompt is too long: 1056837 tokens > 1000000 maximum", "內容長度超過模型上限")]
        public void Explain_TranslatesRealErrors(string raw, string expectedTitle)
        {
            var explained = FailureExplainer.Explain(raw);
            Assert.NotNull(explained);
            Assert.Equal(expectedTitle, explained!.Title);
            Assert.False(string.IsNullOrWhiteSpace(explained.Action));
        }

        [Fact]
        public void Explain_UnknownError_FallsBackToRawText()
        {
            Assert.Null(FailureExplainer.Explain("something entirely unexpected"));
            Assert.Equal("something entirely unexpected", FailureExplainer.Describe("something entirely unexpected"));
            Assert.Equal("", FailureExplainer.Describe(null));
        }

        [Fact]
        public void DescribeAttempts_SaysWhichModelFailedAndWhatTookOver()
        {
            var attempts = new[]
            {
                new AiFallbackAttempt { AttemptIndex = 1, ModelId = "claude-opus-5", Success = false, ErrorMessage = "credit balance is too low" },
                new AiFallbackAttempt { AttemptIndex = 2, ModelId = "gpt-5.6-sol", Success = true },
            };

            var lines = FailureExplainer.DescribeAttempts(attempts, id => id);
            Assert.Single(lines);
            Assert.Contains("claude-opus-5 無法使用", lines[0]);
            Assert.Contains("餘額用完了", lines[0]);
            Assert.Contains("已改用 gpt-5.6-sol", lines[0]);
        }

        [Fact]
        public void DescribeAttempts_SingleSuccessfulAttempt_IsSilent()
        {
            var attempts = new[] { new AiFallbackAttempt { AttemptIndex = 1, ModelId = "claude-sonnet-5", Success = true } };
            Assert.Empty(FailureExplainer.DescribeAttempts(attempts, id => id));
        }

        [Theory]
        [InlineData("claude-sonnet-5", "anthropic")]
        [InlineData("gpt-5.6-sol", "openai")]
        [InlineData("gpt-image-2", "openai")]
        [InlineData("gemini-3.1-pro", "google")]
        [InlineData("veo-3.1-lite-generate-preview", "google")]
        [InlineData("pplx-sonar", "perplexity")]
        [InlineData("openai/gpt-5.6-sol", "perplexity")]   // Perplexity 代跑，錢付給 Perplexity
        [InlineData("", "")]
        public void ProviderIdForModel_MapsSpendToTheRightService(string modelId, string expected)
            => Assert.Equal(expected, ApiHealthChecker.ProviderIdForModel(modelId));

        [Fact]
        public void Ledger_TracksSpendPerProvider_AndSurvivesRestart()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-provider-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            SpendLedger.Initialize(dir);

            SpendLedger.Add(0.50, "test", "anthropic");
            SpendLedger.Add(0.20, "test", "openai");
            SpendLedger.Add(0.30, "test", "anthropic");

            Assert.Equal(0.80, SpendLedger.TodayUsdFor("anthropic"), 6);
            Assert.Equal(0.20, SpendLedger.TodayUsdFor("openai"), 6);
            Assert.Equal(0, SpendLedger.TodayUsdFor("google"), 6);
            Assert.Equal(1.00, SpendLedger.TodayUsd, 6);

            SpendLedger.Initialize(dir); // 重啟
            Assert.Equal(0.80, SpendLedger.MonthUsdFor("anthropic"), 6);
            Assert.Equal(0.80, SpendLedger.UsdForSince("anthropic", DateTime.Today), 6);
        }

        [Fact]
        public void Ledger_ReadsOldFileFormat()
        {
            // 舊檔是純 {"2026-09-20": 1.5}；升級後要讀得進來，不能把使用者的歷史花費清掉。
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-legacy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            File.WriteAllText(Path.Combine(dir, "_spend.json"), $"{{\"{today}\": 1.5}}");

            SpendLedger.Initialize(dir);

            Assert.Equal(1.5, SpendLedger.TodayUsd, 6);
            Assert.Equal(0, SpendLedger.TodayUsdFor("anthropic"), 6); // 舊檔沒有分服務資料
        }

        [Fact]
        public void Monitor_FlipsToNoCredit_OnRealFailure()
        {
            ProviderStatusMonitor.ReportSuccess("anthropic");
            Assert.Equal(ApiHealthChecker.HealthState.Ok, ProviderStatusMonitor.Get("anthropic")!.State);

            ProviderStatusMonitor.ReportFailure("anthropic", "Your credit balance is too low");
            var after = ProviderStatusMonitor.Get("anthropic")!;
            Assert.Equal(ApiHealthChecker.HealthState.NoCredit, after.State);
            Assert.True(after.FromLiveCall);
            Assert.Contains("餘額", after.Message);
        }

        [Fact]
        public void Providers_AllHaveKeyEnvAndBillingLink()
        {
            Assert.Equal(4, ApiHealthChecker.Providers.Count);
            foreach (var p in ApiHealthChecker.Providers)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.EnvName), p.Id);
                Assert.StartsWith("https://", p.BillingUrl);
            }
        }
    }
}
