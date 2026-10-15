using System;
using System.Collections.Generic;

namespace Cat5201
{
    /// <summary>
    /// 價目表與成本計算的單一真相。各 AI 服務回報真實 usage 時用 FromUsage；
    /// 拿不到 usage 時才用 Compute 依字元數推估（顯示一律標「估算」）。
    /// 實際記帳由 UsageMeter 在每次呼叫完成時進行，這裡只負責「多少錢」。
    /// </summary>
    public static class ModelCostEstimator
    {
        public readonly struct Estimate
        {
            public Estimate(int inputTokens, int outputTokens, double usdCost, bool isActual = false)
            {
                InputTokens = inputTokens;
                OutputTokens = outputTokens;
                UsdCost = usdCost;
                IsActual = isActual;
            }

            public int InputTokens { get; }
            public int OutputTokens { get; }
            public int TotalTokens => InputTokens + OutputTokens;
            public double UsdCost { get; }

            // 是否為 API 回傳的真實用量（true）或字元數推估（false）。
            public bool IsActual { get; }

            // 真實：「實際 2.4k tokens · 約 NT$0.6」；估算：「估算 ≈ 2.4k tokens · 約 NT$0.6」。
            // token 數標「實際」，金額仍標「約」（價目表與匯率本身是近似值）。
            public string Display => IsActual
                ? $"實際 {FormatTokens(TotalTokens)} tokens · 約 {FormatTwd(UsdCost)}"
                : $"估算 ≈ {FormatTokens(TotalTokens)} tokens · 約 {FormatTwd(UsdCost)}";
        }

        // 每百萬 token 美元單價（input / output）＋每次呼叫固定費（有些 provider 按次收）。
        // 皆為估算值，僅供成本量級參考。
        private readonly struct Price
        {
            public Price(double inputPerMillion, double outputPerMillion, double perRequestUsd = 0)
            {
                InputPerMillion = inputPerMillion;
                OutputPerMillion = outputPerMillion;
                PerRequestUsd = perRequestUsd;
            }

            public double InputPerMillion { get; }
            public double OutputPerMillion { get; }

            // 每次 API 呼叫的固定費用（USD）。Perplexity 按次收 search/request fee，
            // 只算 token 會系統性低估 —— 這裡以官方價目的中檔情境取近似值。
            public double PerRequestUsd { get; }
        }

        private static readonly Price DefaultPrice = new(3.00, 15.00);

        // 價目來源＝各家官方 pricing 頁，最後核對 2026-09-17。改價時三件事一起做：
        // ①改數字 ②更新本註解日期 ③跑 ModelCostEstimatorTests（有釘關鍵價目防回歸）。
        private static readonly Dictionary<string, Price> PriceTable = new(StringComparer.OrdinalIgnoreCase)
        {
            // GPT-5.6 Sol：目前官方促銷價 $4/$20（標價 $5/$30），促銷「至少到 2026-11-21」——過後回來改。
            ["gpt-5.6-sol"] = new Price(4.00, 20.00),
            ["gpt-6-astra"] = new Price(10.00, 50.00),                  // OpenAI 官方（standard）
            ["claude-sonnet-5"] = new Price(2.00, 10.00),               // Anthropic 官方
            ["claude-opus-5-5"] = new Price(4.00, 20.00),               // Anthropic 官方（2026-09-25 查證）
            ["claude-opus-5"] = new Price(5.00, 25.00),                 // Anthropic 官方

            // 舊世代：模型選單已移除，但舊專案的執行紀錄仍以這些 ID 算歷史成本——刪掉會退回預設價、歷史成本變錯。
            ["gpt-5.5"] = new Price(5.00, 30.00),
            ["claude-sonnet-4-6"] = new Price(3.00, 15.00),
            ["claude-opus-4-8"] = new Price(5.00, 25.00),

            // Perplexity Agent API 代跑第三方模型：官方寫明「依原廠直售價、不加價」，且列的是 GPT-5.6 Sol 標價 $5/$30
            // （不是 OpenAI 直連的促銷價）。web_search 工具費另計，見 PerplexityAgentWebSearchUsd。
            ["openai/gpt-5.6-sol"] = new Price(5.00, 30.00),

            ["pplx-sonar"] = new Price(1.00, 1.00, 0.008),              // + $8/千次 request fee（medium context）
            // Deep research 除 token 外還有 citation tokens($2/M)、search queries($5/千次)、reasoning tokens($3/M)，
            // provider 未在 usage 回報 —— per-request 取保守常數近似（約 30 次 search + reasoning 的中檔情境）。
            ["pplx-sonar-deep-research"] = new Price(2.00, 8.00, 0.30),
            ["gemini-3.1-pro"] = new Price(2.00, 12.00),                // Google 官方（舊表 1.25/10 低估）
            ["gemini-3.5-flash"] = new Price(1.50, 9.00),               // Google 官方
        };

        // Perplexity 按次計價（官方 pricing 頁核對 2026-09-17）：Search API $5/千次；Agent API 的 web_search 工具 $0.0025/次。
        public const double PerplexitySearchRequestUsd = 0.005;
        public const double PerplexityAgentWebSearchUsd = 0.0025;

        // 約略匯率，僅用於估算顯示。公開＝全 App 單一真相（SpendLedger 也引用這裡，
        // 兩處各養一個匯率曾造成 10 倍級顯示錯誤，不再重演）。
        public const double UsdToTwd = 32.0;

        /// <summary>
        /// 美元 → 澳元（Google Cloud 澳洲帳號以 AUD 出帳）。所有成本都以美元計算，
        /// 只有「顯示給使用者的餘額」需要換算；匯率會變，這裡是約略值，餘額本來就是本機推估。
        /// </summary>
        public const double UsdToAud = 1.52;

        /// <summary>
        /// 美元 → 各幣別的約略匯率（2026-10 查）。
        ///
        /// 為什麼要有這張表：餘額的幣別是使用者自己從下拉選單選的。少一個幣別，
        /// ConvertUsd 就會把它當成 1:1 的美元——¥3,000 的餘額會變成永遠扣不完，
        /// 而畫面上完全看不出哪裡不對。所以「可以選的幣別」與「換算得出來的幣別」
        /// 必須是同一份清單：SupportedCurrencies 負責順序（給下拉選單），這張表負責換算，
        /// 並有測試盯著兩邊完全一致。
        /// </summary>
        private static readonly Dictionary<string, double> UsdRates = new(StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = 1.0,
            ["AUD"] = UsdToAud,
            ["TWD"] = UsdToTwd,
            ["EUR"] = 0.92,
            ["JPY"] = 155.0,
            ["GBP"] = 0.79,
        };

        /// <summary>可以換算的幣別（＝可以讓使用者選的幣別）。</summary>
        public static IReadOnlyList<string> SupportedCurrencies { get; } =
            new[] { "USD", "AUD", "TWD", "EUR", "JPY", "GBP" };

        /// <summary>這個幣別換算得出來嗎（沒有匯率就不該出現在選單裡）。</summary>
        public static bool CanConvert(string? currency)
            => !string.IsNullOrWhiteSpace(currency) && UsdRates.ContainsKey(currency!.Trim());

        /// <summary>
        /// 反向：某個幣別的金額 → 美元。
        /// 餘額是以各服務自己的計價幣別存的，但要把四家擺在一起比較時必須換回同一個單位，
        /// 不然畫面上是 A$22.96 和 US$10.52 並排，誰多誰少得先心算一次。
        /// </summary>
        public static double ToUsd(double amount, string? currency)
        {
            double rate = ConvertUsd(1, currency);
            return rate > 0 ? amount / rate : amount;
        }

        /// <summary>把美元換算成某個服務的計價幣別。沒有匯率的幣別原樣回傳並留紀錄，不要靜靜當成美元。</summary>
        public static double ConvertUsd(double usd, string? currency)
        {
            string code = (currency ?? "USD").Trim();
            if (code.Length == 0 || UsdRates.TryGetValue(code, out double rate) == false)
            {
                if (code.Length > 0 && !string.Equals(code, "USD", StringComparison.OrdinalIgnoreCase))
                    AppLog.Warn("Cost", $"沒有「{code}」的匯率，餘額暫時以美元計（請補進 UsdRates）");
                return usd;
            }

            return usd * rate;
        }

        // 圖片生成成本（每張輸出・美元）。gpt-image 系列實際是按 token 計價
        // （影像輸出 $30/M、影像輸入 $8/M、文字輸入 $5/M），但官方同時公布了
        // 「各尺寸 × 品質的輸出費用」估算表，對不透明的 token 數來說這是唯一可用的數字。
        //
        // 2026-10-13 依官方價目重新核對過一次。上一版是手寫的猜測（1536 寬的一律記 $0.25），
        // 直向 1024x1536 其實比正方形便宜，被多記了五成；正方形反而少記兩成。
        // 一張圖的單價記錯，在一份 12 頁簡報上會放大成十幾倍的誤差。
        // key 格式："{size}:{quality}"（小寫）。
        private static readonly Dictionary<string, double> ImagePriceUsd = new(StringComparer.OrdinalIgnoreCase)
        {
            ["1024x1024:high"] = 0.211,
            ["1024x1024:medium"] = 0.053,
            ["1024x1024:low"] = 0.006,
            ["1024x1536:high"] = 0.165,
            ["1024x1536:medium"] = 0.041,
            ["1024x1536:low"] = 0.005,
            ["1536x1024:high"] = 0.165,
            ["1536x1024:medium"] = 0.041,
            ["1536x1024:low"] = 0.005,
            ["1792x1024:high"] = 0.165,
            ["1024x1792:high"] = 0.165,
            // I2V 英雄圖精確 9:16 / 16:9：像素數與 1024x1536 同級，沿用同價。
            ["864x1536:high"] = 0.165,
            ["1536x864:high"] = 0.165,
        };

        private const double DefaultImageUsd = 0.211; // 查不到時用最貴的那一格（寧可高估）

        /// <summary>文字輸入單價（gpt-image 系列的提示詞走這個價）。</summary>
        private const double ImageTextInputPerMillion = 5.00;

        /// <summary>
        /// 一次圖片生成的完整估算：輸出（查表）＋提示詞的文字輸入。
        /// 提示詞只值千分之幾，但把它算進來，帳目才對得上官方帳單的組成方式。
        /// </summary>
        public static double ImageCallCostUsd(string? size, string? quality, string? prompt)
            => ImageCostUsd(size, quality)
               + EstimateTokens(prompt) / 1_000_000.0 * ImageTextInputPerMillion;

        public static Estimate Compute(string? modelId, string? inputText, string? outputText)
        {
            int inputTokens = EstimateTokens(inputText);
            int outputTokens = EstimateTokens(outputText);

            return BuildEstimate(modelId, inputTokens, outputTokens, isActual: false);
        }

        /// <summary>用 API 回傳的真實 token 數計算成本（標示為「實際」）。</summary>
        public static Estimate FromUsage(string? modelId, int inputTokens, int outputTokens)
        {
            return BuildEstimate(modelId, Math.Max(0, inputTokens), Math.Max(0, outputTokens), isActual: true);
        }

        /// <summary>
        /// 命中提示快取的輸入 token 單價倍率（相對於一般輸入單價）。
        /// OpenAI 官方 2026-10：gpt-5.6-sol $4.00 → 快取 $0.40、gpt-6-astra $10.00 → 快取 $1.00，都是一折。
        /// </summary>
        public const double OpenAiCachedInputMultiplier = 0.10;

        /// <summary>
        /// 真實用量 + 其中命中快取的輸入 token。
        ///
        /// 為什麼要分開算：OpenAI 回報的 input_tokens **含**快取命中的部分，但那部分只收一折。
        /// 全部用原價算，一個系統提示重複送幾十次的工作階段會被高估好幾倍——
        /// 2026-10-13 實測，本機記了 US$1.48 的 OpenAI 文字費用，官網實際只扣了約 US$0.43。
        /// </summary>
        public static Estimate FromUsageWithCache(
            string? modelId, int inputTokens, int outputTokens, int cachedInputTokens)
        {
            int input = Math.Max(0, inputTokens);
            int output = Math.Max(0, outputTokens);
            int cached = Math.Clamp(cachedInputTokens, 0, input);

            var full = BuildEstimate(modelId, input - cached, output, isActual: true);
            if (cached == 0)
                return full;

            // 快取那段用同一個模型的輸入單價再乘上倍率。
            double cachedUsd = BuildEstimate(modelId, cached, 0, isActual: true).UsdCost * OpenAiCachedInputMultiplier;

            // token 數照實回報（含快取），使用者看到的「讀進多少」才跟官方帳單對得上。
            return new Estimate(input, output, full.UsdCost + cachedUsd, isActual: true);
        }

        /// <summary>
        /// 快取讀取的單價倍率（相對於輸入單價）。官方 2026-09-25：多數模型 0.1x，
        /// Opus 5.5 是 0.05x，Fable / Mythos 5.1 是 0.025x。寫死 0.1 會把 Opus 5.5 的快取算貴一倍。
        /// </summary>
        public static double CacheReadMultiplier(string? modelId)
        {
            string m = ResolvePricingModelId(modelId).ToLowerInvariant();
            if (m.Contains("opus-5-5")) return 0.05;
            if (m.Contains("fable-5-1") || m.Contains("mythos-5-1")) return 0.025;
            return 0.10;
        }

        /// <summary>5 分鐘快取寫入的單價倍率（官方：1.25x 輸入單價；1 小時快取為 2x，本專案只用 5 分鐘）。</summary>
        public const double CacheWriteMultiplier = 1.25;

        /// <summary>單張圖片的估算美元成本（依尺寸 / 品質查表，查無則退回 1024 高品質估算）。</summary>
        public static double ImageCostUsd(string? size, string? quality)
        {
            string s = string.IsNullOrWhiteSpace(size) ? "1024x1024" : size.Trim().ToLowerInvariant();
            string q = string.IsNullOrWhiteSpace(quality) ? "high" : quality.Trim().ToLowerInvariant();

            if (ImagePriceUsd.TryGetValue($"{s}:{q}", out var exact))
                return exact;
            if (ImagePriceUsd.TryGetValue($"{s}:high", out var bySize))
                return bySize;

            return DefaultImageUsd;
        }

        /// <summary>
        /// 圖片單張成本描述（給二次確認框等 UI 用）——與實際計費同一價目表，避免兩處真相不一致。
        /// 例：「約 NT$5.44/張」。
        /// </summary>
        public static string ImageUnitCostText(string? size = null, string? quality = null)
            => $"約 {FormatTwd(ImageCostUsd(size, quality))}/張";

        /// <summary>
        /// 圖片成本提示字串，沿用文字成本的「估算 ≈ … · 約 NT$…」格式（圖片無 token，改顯示張數）。
        /// 例：「估算 ≈ 1 張圖 · 約 NT$5.44」。
        /// </summary>
        public static string ImageCostDisplay(int count, string? size, string? quality)
        {
            int n = Math.Max(1, count);
            double usd = ImageCostUsd(size, quality) * n;
            return $"估算 ≈ {n} 張圖 · 約 {FormatTwd(usd)}";
        }

        private static Estimate BuildEstimate(string? modelId, int inputTokens, int outputTokens, bool isActual)
        {
            Price price = ResolvePrice(modelId);

            double usd =
                inputTokens / 1_000_000.0 * price.InputPerMillion +
                outputTokens / 1_000_000.0 * price.OutputPerMillion;

            // 按次計費（Perplexity search fee 等）：有實際用量＝真的呼叫過一次，加上固定費。
            if (inputTokens > 0 || outputTokens > 0)
                usd += price.PerRequestUsd;

            return new Estimate(inputTokens, outputTokens, usd, isActual);
        }

        /// <summary>
        /// 粗估 token 數：CJK 字元約 1.5 字/token，其餘（拉丁、數字、符號）約 4 字/token。
        /// </summary>
        public static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int cjk = 0;
            int other = 0;

            foreach (char c in text)
            {
                if (IsCjk(c))
                    cjk++;
                else if (!char.IsWhiteSpace(c))
                    other++;
            }

            double tokens = (cjk / 1.5) + (other / 4.0);
            return (int)Math.Ceiling(tokens);
        }

        private static bool IsCjk(char c)
        {
            return
                (c >= '一' && c <= '鿿') ||   // CJK 統一表意文字
                (c >= '㐀' && c <= '䶿') ||   // 擴充 A
                (c >= '぀' && c <= 'ヿ') ||   // 平假名 / 片假名
                (c >= '＀' && c <= '￯');     // 全形符號
        }

        /// <summary>
        /// 服務端送出的模型 ID → 價目表用的 ID。例：sonar → pplx-sonar、gemini-3.1-pro-preview → gemini-3.1-pro。
        /// 對不到就原樣回傳（BuildEstimate 會退回預設價，至少不會記 0 元）。
        /// </summary>
        public static string ResolvePricingModelId(string? modelId)
        {
            string id = (modelId ?? "").Trim();
            if (id.Length == 0 || PriceTable.ContainsKey(id))
                return id;

            foreach (var def in AiModelRegistry.All)
            {
                if (string.Equals(def.ServiceModel, id, StringComparison.OrdinalIgnoreCase))
                    return def.Id;
            }

            string alias = AiModelRegistry.ResolveAlias(id) ?? id;
            return PriceTable.ContainsKey(alias) ? alias : id;
        }

        private static Price ResolvePrice(string? modelId)
        {
            if (!string.IsNullOrWhiteSpace(modelId) &&
                PriceTable.TryGetValue(modelId.Trim(), out var p))
            {
                return p;
            }

            return DefaultPrice;
        }

        public static string FormatTokens(int tokens)
        {
            if (tokens >= 1000)
                return (tokens / 1000.0).ToString("0.0") + "k";

            return tokens.ToString();
        }

        public static string FormatTwd(double usd)
        {
            double twd = usd * UsdToTwd;

            if (twd < 0.01)
                return "< NT$0.01";

            return "NT$" + twd.ToString("0.00");
        }
    }
}
