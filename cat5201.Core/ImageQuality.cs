using System;

namespace Cat5201
{
    /// <summary>
    /// 圖片生成品質檔位（gpt-image 的 quality 參數）。
    ///
    /// 價差很大：1024×1536 一張 high US$0.165、medium US$0.041、low US$0.005。
    /// 所以「這張圖要給誰看、用多大」必須是個明確的決定，不能整支寫死 high
    /// ——那等於每張配圖都付四倍去買投影片上看不出來的畫素。
    /// </summary>
    public static class ImageQuality
    {
        public const string High = "high";
        public const string Medium = "medium";
        public const string Low = "low";

        /// <summary>認得的才送出去，其餘一律回 high（寧可貴也不要被 API 退件）。</summary>
        public static string Normalize(string? quality)
        {
            return (quality ?? "").Trim().ToLowerInvariant() switch
            {
                Medium => Medium,
                Low => Low,
                _ => High,
            };
        }

        /// <summary>dall-e-3 只有 hd / standard 兩檔。</summary>
        public static string ToDalle3(string? quality)
            => Normalize(quality) == High ? "hd" : "standard";
    }
}
