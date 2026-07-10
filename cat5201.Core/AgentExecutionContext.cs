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
    }
}
