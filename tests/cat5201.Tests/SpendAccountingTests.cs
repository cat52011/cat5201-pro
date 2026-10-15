using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-13：使用者回報「API 餘額跟官網差了好幾倍」。
    ///
    /// 追下去，扣款本身是對的（餘額簿的變動跟帳本逐筆對得上分毫），問題全在
    /// 「算多少錢」與「這筆錢算在誰頭上」這兩步，而且錯的時候一點聲音都沒有。
    /// 這一組測試把每一個無聲失敗的環節都釘起來。
    /// </summary>
    [Collection("spend-ledger")]
    public class SpendAccountingTests
    {
        // ─────────────────────── 幣別：能選的就要能換算

        [Fact]
        public void 選單裡的每一種幣別都換算得出來()
        {
            // 少一個匯率，ConvertUsd 會默默當成 1:1 美元——¥3,000 的餘額永遠扣不完，
            // 而畫面上完全看不出哪裡不對。
            foreach (string currency in ProviderBalance.SupportedCurrencies)
            {
                Assert.True(ModelCostEstimator.CanConvert(currency), $"{currency} 沒有匯率");
                Assert.True(ModelCostEstimator.ConvertUsd(1, currency) > 0);
            }

            // 兩份清單必須完全一致（含順序以外的內容）。
            Assert.Equal(
                ModelCostEstimator.SupportedCurrencies.OrderBy(x => x, StringComparer.Ordinal),
                ProviderBalance.SupportedCurrencies.OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void 換成非美元幣別時餘額會跟著換算()
        {
            string id = NewProvider();

            ProviderBalance.SetBalance(id, 100);          // 預設 USD
            ProviderBalance.SetCurrency(id, "JPY");

            Assert.Equal("JPY", ProviderBalance.CurrencyOf(id));
            Assert.Equal(100 * ModelCostEstimator.ConvertUsd(1, "JPY"), ProviderBalance.Remaining(id), 2);
        }

        [Fact]
        public void 幣別只有一個真相來源()
        {
            string dir = NewTempDir();
            ProviderBalance.Initialize(dir);

            // 出廠預設寫死「Google ＝ AUD」，那是某一個帳號的事實。
            // 帳單國別不同的人改成 USD 之後，每一個顯示餘額的地方都必須跟著改——
            // 右下角小面板曾經直接讀 ProviderInfo.Currency，於是設定頁顯示 US$、
            // 小面板還是顯示 A$，同一筆錢兩個講法。
            Assert.Equal("AUD", ProviderBalance.CurrencyOf("google"));

            ProviderBalance.SetBalance("google", 50);
            ProviderBalance.SetCurrency("google", "USD");

            Assert.Equal("USD", ProviderBalance.CurrencyOf("google"));
            Assert.Equal("US$", ApiHealthChecker.CurrencySymbol(ProviderBalance.CurrencyOf("google")));

            // 預設值本身沒有被改掉，只是不再是這個使用者看到的那一個。
            Assert.Equal("AUD", ProviderBalance.DefaultCurrencyOf("google"));

            // 重開程式後選擇要還在。
            ProviderBalance.Initialize(dir);
            Assert.Equal("USD", ProviderBalance.CurrencyOf("google"));
        }

        [Fact]
        public void 換回美元是換過去的反運算()
        {
            // 右下角的快捷面板一律以美元並排顯示（四家混著 A$ 和 US$ 排在一起，
            // 要比大小得先心算匯率，那就不叫快捷查看了）。
            // 來回換算必須完全對稱，不然每重畫一次面板數字就會漂一點。
            foreach (string currency in ProviderBalance.SupportedCurrencies)
            {
                double native = ModelCostEstimator.ConvertUsd(37.5, currency);
                Assert.Equal(37.5, ModelCostEstimator.ToUsd(native, currency), 6);
            }

            // 作者的 Google 帳號以澳元出帳：A$22.96 在快捷面板上要顯示成美元。
            Assert.Equal(22.96 / ModelCostEstimator.UsdToAud,
                         ModelCostEstimator.ToUsd(22.96, "AUD"), 6);
        }

        // ─────────────────────── 超用的部分不能消失

        [Fact]
        public void 花得比填的餘額多時超出的部分會留在帳上()
        {
            string id = NewProvider();

            ProviderBalance.SetBalance(id, 1.00);
            ProviderBalance.Deduct(id, 1.50);

            // 夾在 0 的話，使用者下次充值 10 元填進來就會以為還有 10，
            // 實際上官網那邊已經先被那 0.5 吃掉了。
            Assert.Equal(-0.50, ProviderBalance.Remaining(id), 4);
        }

        // ─────────────────────── 對帳：差額要被記下來

        [Fact]
        public void 送出餘額等於重新對帳並重算之後的累計扣款()
        {
            string id = NewProvider();

            Assert.Null(ProviderBalance.LastReconciledUtc(id));

            ProviderBalance.SetBalance(id, 20);
            var first = ProviderBalance.LastReconciledUtc(id);
            Assert.NotNull(first);
            Assert.Equal(0, ProviderBalance.DeductedSinceReconcile(id), 4);

            ProviderBalance.Deduct(id, 3);
            ProviderBalance.Deduct(id, 2);
            Assert.Equal(5, ProviderBalance.DeductedSinceReconcile(id), 4);

            // 使用者去官網看到其實只剩 10（本機以為 15）＝這段期間少記了 5。
            ProviderBalance.SetBalance(id, 10);
            Assert.Equal(10, ProviderBalance.Remaining(id), 4);
            Assert.Equal(0, ProviderBalance.DeductedSinceReconcile(id), 4);
            Assert.True(ProviderBalance.LastReconciledUtc(id) >= first);
        }

        [Fact]
        public void 對帳紀錄會跟著餘額一起存檔()
        {
            string dir = NewTempDir();
            ProviderBalance.Initialize(dir);
            ProviderBalance.SetBalance("anthropic", 8);
            ProviderBalance.Deduct("anthropic", 1.25);

            // 重開程式＝重新讀檔。對帳時間與其後的扣款都要還在，
            // 不然每次啟動都會變成「從來沒對過帳」。
            ProviderBalance.Initialize(dir);
            Assert.Equal(6.75, ProviderBalance.Remaining("anthropic"), 4);
            Assert.Equal(1.25, ProviderBalance.DeductedSinceReconcile("anthropic"), 4);
            Assert.NotNull(ProviderBalance.LastReconciledUtc("anthropic"));
        }

        // ─────────────────────── 命中快取的輸入只收一折

        [Fact]
        public void 命中快取的輸入token不能用原價算()
        {
            // OpenAI 回報的 input_tokens **含**快取命中的部分，但那部分只收一折。
            // 全部用原價算，一個系統提示重複送幾十次的工作階段會被高估好幾倍
            // （2026-10-13 實測：本機記 US$1.48，官網實際約 US$0.43）。
            var full = ModelCostEstimator.FromUsage("gpt-5.6-sol", 100_000, 0);
            var cached = ModelCostEstimator.FromUsageWithCache("gpt-5.6-sol", 100_000, 0, 100_000);

            Assert.Equal(full.UsdCost * ModelCostEstimator.OpenAiCachedInputMultiplier, cached.UsdCost, 6);

            // token 數照實回報（含快取），畫面上的「讀進多少」才跟官方帳單對得上。
            Assert.Equal(100_000, cached.InputTokens);
        }

        [Fact]
        public void 一半命中快取就只打一半的折()
        {
            var est = ModelCostEstimator.FromUsageWithCache("gpt-5.6-sol", 100_000, 0, 50_000);
            double expected =
                ModelCostEstimator.FromUsage("gpt-5.6-sol", 50_000, 0).UsdCost * (1 + ModelCostEstimator.OpenAiCachedInputMultiplier);

            Assert.Equal(expected, est.UsdCost, 6);
        }

        [Fact]
        public void 沒有快取時跟原本算法完全一樣()
        {
            var a = ModelCostEstimator.FromUsage("gpt-5.6-sol", 12_345, 6_789);
            var b = ModelCostEstimator.FromUsageWithCache("gpt-5.6-sol", 12_345, 6_789, 0);

            Assert.Equal(a.UsdCost, b.UsdCost, 8);
        }

        [Fact]
        public void 快取數字超過輸入總數時不會變成負的()
        {
            // 防呆：服務端回報不一致時，不能算出比實際更便宜（甚至負的）成本。
            var est = ModelCostEstimator.FromUsageWithCache("gpt-5.6-sol", 1_000, 0, 999_999);
            Assert.True(est.UsdCost > 0);
            Assert.Equal(
                ModelCostEstimator.FromUsage("gpt-5.6-sol", 1_000, 0).UsdCost * ModelCostEstimator.OpenAiCachedInputMultiplier,
                est.UsdCost, 8);
        }

        // ─────────────────────── 這筆錢算在誰頭上

        [Theory]
        [InlineData("gpt-image-2", "openai")]
        [InlineData("gpt-5.6-sol", "openai")]
        [InlineData("chatgpt-4o-latest", "openai")]
        [InlineData("sora-2", "openai")]
        [InlineData("text-embedding-3-large", "openai")]
        [InlineData("claude-sonnet-5", "anthropic")]
        [InlineData("gemini-3.1-pro", "google")]
        [InlineData("veo-3.1-lite-generate-preview", "google")]
        [InlineData("imagen-4", "google")]
        [InlineData("sonar-deep-research", "perplexity")]
        [InlineData("openai/gpt-5.6-sol", "perplexity")]   // Perplexity 代跑，錢付給 Perplexity
        [InlineData("anthropic/claude-sonnet-5", "perplexity")]
        public void 每一種會花錢的模型都對得到服務(string modelId, string expected)
        {
            // 對不到就回空字串，而 SpendLedger 與 ProviderBalance 收到空字串是直接忽略——
            // 漏一個前綴，那一類呼叫花的錢就整批從帳上消失。
            Assert.Equal(expected, ApiHealthChecker.ProviderIdForModel(modelId));
        }

        [Fact]
        public void 實際跑過的模型沒有一個落在無人認領的狀態()
        {
            foreach (var def in AiModelRegistry.All)
            {
                Assert.False(string.IsNullOrEmpty(ApiHealthChecker.ProviderIdForModel(def.ServiceModel)),
                    $"{def.ServiceModel} 對不到服務");
                Assert.False(string.IsNullOrEmpty(ApiHealthChecker.ProviderIdForModel(def.Id)),
                    $"{def.Id} 對不到服務");
            }
        }

        // ─────────────────────── 圖片價目

        [Fact]
        public void 直向圖比正方形便宜而不是更貴()
        {
            // 舊表把所有 1536 寬的一律記 $0.25，比官方價高五成；正方形記 $0.17，又比官方低兩成。
            // 一張圖差幾分錢，在一份十幾頁、每頁配圖的簡報上就是好幾倍的誤差。
            double square = ModelCostEstimator.ImageCostUsd("1024x1024", "high");
            double portrait = ModelCostEstimator.ImageCostUsd("1024x1536", "high");

            Assert.Equal(0.211, square, 3);
            Assert.Equal(0.165, portrait, 3);
            Assert.True(portrait < square);

            // 品質降一級要真的便宜，不然「省錢模式」是假的。
            Assert.True(ModelCostEstimator.ImageCostUsd("1024x1024", "medium") < square);
            Assert.True(ModelCostEstimator.ImageCostUsd("1024x1024", "low")
                        < ModelCostEstimator.ImageCostUsd("1024x1024", "medium"));
        }

        [Fact]
        public void 查不到的尺寸退回最貴的那一格()
        {
            // 寧可高估：低估會讓使用者在不知情的狀況下超支。
            double unknown = ModelCostEstimator.ImageCostUsd("9999x9999", "high");
            Assert.True(unknown >= ModelCostEstimator.ImageCostUsd("1024x1024", "high"));
        }

        [Fact]
        public void 圖片成本含提示詞的輸入費()
        {
            double bare = ModelCostEstimator.ImageCostUsd("1024x1024", "high");
            double withPrompt = ModelCostEstimator.ImageCallCostUsd(
                "1024x1024", "high", new string('字', 4000));

            Assert.True(withPrompt > bare);
            Assert.Equal(bare, ModelCostEstimator.ImageCallCostUsd("1024x1024", "high", null), 6);
        }

        // ─────────────────────── 輔助

        private static string NewProvider()
        {
            ProviderBalance.Initialize(NewTempDir());
            return "acct-" + Guid.NewGuid().ToString("N")[..8];
        }

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-acct-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
