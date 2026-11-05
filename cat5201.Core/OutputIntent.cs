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

        /// <summary>
        /// 使用者明確說不要圖（不配圖、純文字、no images…）。跟「沒提到圖」是兩回事：
        /// 沒提到時由內容決定要不要配圖；明說不要就一定不配。
        /// 2026-11-03：關鍵字規則把「不配圖」讀成「要配圖」，簡報被生了封面照片——這件事只能交給語意判斷。
        /// </summary>
        public bool DeclinesIllustrations { get; init; }

        /// <summary>
        /// 使用者這次指定的製作方式：快速版或最佳品質（沒講＝None，照個人化的設定）。
        /// 2026-11-03：要求寫「快速版」，個人化設最佳品質，結果照個人化走了最佳品質。這一次明講的優先。
        /// </summary>
        public RequestedQuality.Level Quality { get; init; }

        /// <summary>
        /// 這份產出的主題，當檔名用（例如「BMI」）。由模型從整句話抽出，不含指令與格式詞；
        /// 以前用刪字清單，檔名常留下「BMI（快速版、不）」這種殘字。空字串＝沒判斷出來，改用舊的清理方式。
        /// </summary>
        public string Topic { get; init; } = "";

        /// <summary>使用者指定的投影片張數；0＝沒指定，由內容決定。</summary>
        public int RequestedSlides { get; init; }

        /// <summary>
        /// 這次請求明確指定的輸出語言（沒指定＝空字串）。
        /// 由第一層語意判斷得出，讓程式可以先解析出「這次到底用哪個語言」，
        /// 而不是把「全域偏好說繁中」和「這次要求英文」兩條規則一起丟給模型自己喬。
        /// </summary>
        public string RequestedLanguage { get; init; } = "";

        /// <summary>
        /// 這次要動用哪些能力（要不要搜尋即時資料、要不要推理分析、要不要寫程式）。
        /// 跟上面的輸出判斷是同一次模型呼叫，不另外花錢。null＝模型沒給（關鍵字後援、API 失敗、舊格式回覆），
        /// AgentCapabilityPlanner 才退回關鍵字規則。
        /// </summary>
        public CapabilityNeeds? Capabilities { get; init; }

        /// <summary>
        /// 產出物（簡報／報告／表格／圖片）的藝術方向 ID（DeckArtDirection.AllIds 之一），由同一次語意判斷決定；
        /// 空字串＝沒判斷出來或不需要。2026-11-06 以前用關鍵字比對使用者那句話，「介紹京都」一個字都沒中，
        /// 京都簡報就被套上商業科技藍。藝術類一律往使用者偏好的復古雜誌美感靠（PoeticPrintStyle）。
        /// </summary>
        public string ArtDirection { get; init; } = "";

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
