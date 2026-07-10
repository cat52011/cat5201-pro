using System;
using System.Threading;

namespace Cat5201
{
    public sealed class NodeExecutionRequest
    {
        public NodeControl Node { get; init; } = null!;
        public string TopText { get; init; } = "";
        public string ModelId { get; init; } = "";
        public NodeTaskMode TaskMode { get; init; } = NodeTaskMode.Chat;
        public Action<string>? OnDelta { get; init; }
        public bool UseStreaming { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }
}