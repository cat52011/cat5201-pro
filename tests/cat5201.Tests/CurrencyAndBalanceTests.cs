using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 餘額是本機記帳：花費一律以美元計算，但顯示與儲存要用該服務真正的收費幣別
    /// （Google 澳洲帳號出帳是 AUD），不然使用者得自己心算換匯。
    /// </summary>
    [Collection("spend-ledger")]
    public class CurrencyAndBalanceTests
    {
        [Theory]
        [InlineData("google", "AUD", "A$")]
        [InlineData("anthropic", "USD", "US$")]
        [InlineData("openai", "USD", "US$")]
        [InlineData("perplexity", "USD", "US$")]
        public void ProviderCurrencyAndSymbol(string providerId, string currency, string symbol)
        {
            Assert.Equal(currency, ProviderBalance.CurrencyOf(providerId));
            Assert.Equal(symbol, ApiHealthChecker.CurrencySymbol(currency));
        }

        [Fact]
        public void GoogleSpendIsConvertedBeforeItIsDeducted()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cat5201-cur-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            ProviderBalance.Initialize(dir);

            // 使用者照 Google 帳單填 A$100，花掉 US$10 → 扣掉的是換算後的澳元。
            ProviderBalance.SetBalance("google", 100);
            ProviderBalance.Deduct("google", 10);

            double expected = 100 - 10 * ModelCostEstimator.UsdToAud;
            Assert.Equal(expected, ProviderBalance.Remaining("google"), 4);

            // 美元計價的服務不換算。
            ProviderBalance.SetBalance("anthropic", 100);
            ProviderBalance.Deduct("anthropic", 10);
            Assert.Equal(90, ProviderBalance.Remaining("anthropic"), 4);
        }
    }
}
