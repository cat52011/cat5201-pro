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

        // 圖片生成成本（每張・美元）。OpenAI gpt-image 系列「以張計價」，依尺寸 / 品質不同，
        // 與文字模型的 token 計價邏輯不同（用字數估 token 會誤導），故獨立一張表。
        // 數字為估算：對 gpt-image-1 公布價目；gpt-image-2 尚無正式價目，取同級高品質估算。
        // key 格式："{size}:{quality}"（小寫）。
        private static readonly Dictionary<string, double> ImagePriceUsd = new(StringComparer.OrdinalIgnoreCase)
        {
            ["1024x1024:high"] = 0.17,
            ["1024x1024:medium"] = 0.04,
            ["1024x1024:low"] = 0.01,
            ["1536x1024:high"] = 0.25,
            ["1024x1536:high"] = 0.25,
            ["1792x1024:high"] = 0.25,
            ["1024x1792:high"] = 0.25,
            // I2V 英雄圖精確 9:16 / 16:9：像素比 1024x1536 少，保守沿用同價（寧可高估不低估）。
            ["864x1536:high"] = 0.25,
            ["1536x864:high"] = 0.25,
        };

        private const double DefaultImageUsd = 0.17; // 1024 高品質估算

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
