namespace Cat5201
{
    /// <summary>
    /// 交付設定（個人化）。
    ///
    /// 目前只有一項：產出簡報／報告／表格時，要不要同時附一份同內容的 PDF。
    /// 預設 true＝維持原本行為。關掉的理由通常是速度：PDF 是整個產出流程裡最慢的一步，
    /// 以一份含圖的九頁簡報實測，重建 PDF 約佔單頁編輯總耗時的三分之二。
    ///
    /// 放在 Core 而不是 WPF，是因為 AgentRuntime 與預覽的重寫路徑都要讀它。
    /// </summary>
    public static class DeliveryOptions
    {
        /// <summary>產出檔案時是否同時產生同內容的對照 PDF。</summary>
        public static bool CompanionPdf { get; set; } = true;

        /// <summary>測試用：回到預設值，避免某個測試關掉之後影響別的測試。</summary>
        public static void ResetForTests() => CompanionPdf = true;
    }
}
