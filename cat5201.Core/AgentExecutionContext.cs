using System;
using System.Collections.Generic;

namespace Cat5201
{
    public sealed class AgentExecutionContext
    {
        public INodeContext Node { get; init; } = null!;

        public AgentDefinition Agent { get; init; } = null!;

        public string AgentId => Agent?.Id ?? "";

        public string TopText { get; init; } = "";

        public NodeTaskMode TaskMode { get; init; } = NodeTaskMode.Chat;

        public IReadOnlyList<AttachmentInfo> Attachments { get; init; }
            = Array.Empty<AttachmentInfo>();

        public string AttachmentsRootDir { get; init; } = "";

        /// <summary>
        /// 這次執行可以呼叫哪些模型。手動模式下能力層（搜尋研究）先用使用者選的模型；
        /// 做不到或確定失敗時改用預設模型，並告知使用者。
        /// 預設 Auto，維持舊行為。
        /// </summary>
        public ModelScope Scope { get; init; } = ModelScope.Auto;

        /// <summary>
        /// 這次需要知道「現在」時才有值（從 UserClock 讀，跟給模型的目前日期同一刻）；null＝這次跟時間無關。
        /// 搜尋用它把「這週末」「今天」換成實際日期再查——只送原句時搜尋引擎分不出是哪一週，
        /// 2026-11-06 實測拿回 10 月的舊文章、沒有 11/8 的預報。
        /// 能力規劃要等意圖判斷回來才知道需不需要，所以這個欄位在建立之後才設定。
        /// </summary>
        public DateTimeOffset? CurrentDate { get; set; }
    }
}
