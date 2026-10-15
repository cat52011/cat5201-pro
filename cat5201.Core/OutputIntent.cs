namespace Cat5201
{
    /// <summary>
    /// §6 第一層輸出判斷的結果：使用者這次想要「簡報 / 報告 / 表格」之中的哪幾個（可多選、可全無）。
    /// 由 OutputIntentResolver（先跑一次 API 判斷）產生，找不到時退回關鍵字判斷。
    /// 實際產檔規則（AgentRuntime）：報告→.docx、表格→.xlsx、簡報→.pptx，且「不管哪一種都一律再配一份 .pdf」。
    /// </summary>
    public sealed class OutputIntent
    {
        public bool WantsPresentation { get; init; }
        public bool WantsReport { get; init; }
        public bool WantsTable { get; init; }

        // 影片 / 圖片也納入同一個第一層 LLM 判斷（與簡報/報告/表格同一真相來源），
        // 不再只靠 OrchestrationPlanner 的關鍵字白名單——避免「給我一個15秒的影片」這種講法漏掉。
        public bool WantsVideo { get; init; }
        public bool WantsImage { get; init; }

        /// <summary>
        /// 文件裡要不要配圖（跟 WantsImage「要一張獨立的圖檔」是兩回事）。
        /// 這個判斷一定要由模型做：關鍵字表只認得繁體中文的講法，簡體、英文、日文的使用者
        /// 說了要圖也會被漏掉，簡報就會變成純文字。
        /// </summary>
        public bool WantsIllustrations { get; init; }

        /// <summary>使用者指定的投影片張數；0＝沒指定，由內容決定。</summary>
        public int RequestedSlides { get; init; }

        /// <summary>
        /// 這次請求明確指定的輸出語言（沒指定＝空字串）。
        /// 由第一層語意判斷得出，讓程式可以先解析出「這次到底用哪個語言」，
        /// 而不是把「全域偏好說繁中」和「這次要求英文」兩條規則一起丟給模型自己喬。
        /// </summary>
        public string RequestedLanguage { get; init; } = "";

        // debug：API 原始回覆 / 判斷來源
        public string Source { get; init; } = "";

        public bool WantsAny => WantsPresentation || WantsReport || WantsTable || WantsVideo || WantsImage;

        /// <summary>白話摘要，給決策窗「執行摘要」顯示這次第一層判斷出的想要輸出。</summary>
        public string ToSummary()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (WantsReport) parts.Add("報告");
            if (WantsTable) parts.Add("表格");
            if (WantsPresentation) parts.Add("簡報");
            if (WantsImage) parts.Add("圖片");
            if (WantsVideo) parts.Add("影片");
            return parts.Count == 0 ? "純文字回答" : string.Join("、", parts);
        }

        public static OutputIntent None => new OutputIntent { Source = "none" };

        /// <summary>關鍵字後援判斷（API 失敗或未啟用時用）。影片/圖片沿用 OrchestrationPlanner 的關鍵字。</summary>
        public static OutputIntent FromKeywords(string? text)
        {
            var taskType = OrchestrationPlanner.ResolveTaskType(text, NodeTaskMode.Chat);
            return new OutputIntent
            {
                WantsPresentation = OutputFormatDetector.WantsPresentation(text),
                WantsReport = OutputFormatDetector.WantsWrittenReport(text),
                WantsTable = OutputFormatDetector.WantsSpreadsheet(text),
                WantsVideo = taskType == OrchestrationTaskType.VideoGeneration,
                WantsImage = taskType == OrchestrationTaskType.ImageGeneration,
                WantsIllustrations = AgentRuntime.PresentationWantsCoverImage(text),
                RequestedSlides = PresentationOutlineBuilder.DetectRequestedSlideCount(text ?? ""),
                Source = "keywords"
            };
        }
    }
}
