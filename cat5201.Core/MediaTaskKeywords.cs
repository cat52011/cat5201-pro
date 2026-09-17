using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 媒體任務關鍵字的「單一真相」（#7 分類器收斂第一刀）。
    ///
    /// 歷史問題:同一組關鍵字散落在 OrchestrationPlanner.ResolveTaskType 與 NodeControl.IsImageTask/IsVideoTask,
    /// 註解互相要求「清單保持一致」全靠人工同步——本檔把清單集中,兩處改為引用,加詞只改這裡。
    ///
    /// 三組清單刻意分開、不可合併:
    ///  - ImageGenerationCommands / VideoGenerationCommands = 「命令式」嚴格清單,觸發實際生成(要花錢,寧漏勿誤);
    ///  - VideoTimeoutHints = 寬鬆清單,只用來判斷「這任務可能跑很久」給逾時策略(寧誤勿漏,多給時間無害)。
    /// </summary>
    public static class MediaTaskKeywords
    {
        /// <summary>圖片生成(命令式)。OrchestrationPlanner 判 ImageGeneration 與 NodeControl 逾時判斷共用。</summary>
        public static readonly IReadOnlyList<string> ImageGenerationCommands = new[]
        {
            "圖片", "圖像", "生成圖片", "產生圖片",
            "畫一張", "畫一隻", "畫一幅", "畫個", "畫張", "幫我畫", "請畫",
            "照片", "相片", "photo",
            "image", "generate image", "draw",
        };

        /// <summary>影片生成(命令式):必須是「明確要產生影片」的說法。
        /// 不可只因內文出現「影片/video」就觸發——「他發了個影片限動」是聊天不是任務。</summary>
        public static readonly IReadOnlyList<string> VideoGenerationCommands = new[]
        {
            "生成影片", "產生影片", "做一支影片", "做一部影片", "做個影片", "做成影片",
            "製作影片", "幫我做影片", "剪一支影片", "剪輯影片", "剪成影片",
            "拍一支影片", "拍一部影片", "拍個影片", "預告片", "短影片", "音樂影片",
            "generate video", "make a video", "create a video", "video clip", "trailer", "montage",
        };

        /// <summary>影片「提及」寬鬆清單:只給逾時策略用(判斷可能長跑 → 無逾時上限),不觸發任何生成。</summary>
        public static readonly IReadOnlyList<string> VideoTimeoutHints = new[]
        {
            "影片", "視頻", "生成影片", "產生影片", "預告片", "短片",
            "video", "generate video", "trailer",
        };

        /// <summary>小寫化後任一關鍵字命中即 true(與原兩處 ContainsAny 同語義:OrdinalIgnoreCase 子字串)。</summary>
        public static bool MatchesAny(string? text, IReadOnlyList<string> keywords)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            string lower = text.ToLowerInvariant();
            return keywords.Any(k => lower.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsImageGenerationCommand(string? text) => MatchesAny(text, ImageGenerationCommands);
        public static bool IsVideoGenerationCommand(string? text) => MatchesAny(text, VideoGenerationCommands);
        public static bool MentionsVideoForTimeout(string? text) => MatchesAny(text, VideoTimeoutHints);
    }
}
