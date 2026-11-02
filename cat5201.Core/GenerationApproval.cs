using System;
using System.Collections.Generic;

namespace Cat5201
{
    /// <summary>
    /// 確認框要讓使用者選的東西（由執行核心決定，宿主照著畫）。
    ///
    /// 2026-10-29：手動模式下「所選模型以外的任何模型，都必須在確認框裡由使用者親手選」。
    /// 撰寫者與配圖原本由個人化設定決定，按下「產生」時看不到——
    /// 使用者回報手動選了 GPT，簡報卻是 Claude 寫的；但也指出「說不定使用者想看其他 AI 寫的簡報」。
    /// 兩者都要成立，所以選擇放在產生的那一刻，預設是節點選的模型。
    /// </summary>
    public sealed record GenerationChoices
    {
        /// <summary>這次有沒有要寫文件（簡報／報告／表格）。沒有就不顯示撰寫者選單。</summary>
        public bool IncludesDocuments { get; init; }

        /// <summary>預設撰寫者：手動＝節點選的模型；Auto＝個人化設定會用的那一個。</summary>
        public string DefaultAuthorModelId { get; init; } = "";

        /// <summary>可選的撰寫者（應用程式內部 id）。</summary>
        public IReadOnlyList<string> AuthorOptions { get; init; } = Array.Empty<string>();

        /// <summary>要不要顯示「加入配圖」勾選框（文件類產出才有）。</summary>
        public bool ShowIllustrationOption { get; init; }

        /// <summary>配圖勾選框的預設值：手動＝不勾（會用到圖片模型）；Auto＝勾（交給配圖政策判斷）。</summary>
        public bool IllustrationsDefault { get; init; }

        /// <summary>最佳品質模式是否開著——只有 Claude 能用，選其他撰寫者時要講清楚會改用一般版面。</summary>
        public bool BestQualityEnabled { get; init; }

        public static GenerationChoices None { get; } = new();
    }

    /// <summary>
    /// 使用者在確認框裡的回答。
    /// AuthorModelId／Illustrations 只在使用者「改了預設」時才有值；沒動就是 null，
    /// 讓 Auto 模式在使用者什麼都沒改時行為與以前完全相同。
    /// </summary>
    public sealed record GenerationApproval(bool Approved, string? AuthorModelId = null, bool? Illustrations = null)
    {
        public static GenerationApproval Declined { get; } = new(false);
        public static GenerationApproval Accepted { get; } = new(true);
    }
}
