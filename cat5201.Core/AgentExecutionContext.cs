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
    }
}
