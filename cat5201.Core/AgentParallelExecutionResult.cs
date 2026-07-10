using System.Collections.Generic;

namespace Cat5201
{
    public sealed class AgentParallelExecutionResult
    {
        public IReadOnlyList<AgentParallelTaskResult> Results { get; init; }
            = new List<AgentParallelTaskResult>();

        public bool HasAnySuccess { get; init; }
    }

    public sealed class AgentParallelTaskResult
    {
        public string AgentId { get; init; } = "";

        public string ModelId { get; init; } = "";

        public string Output { get; init; } = "";

        public bool Success { get; init; }

        public string ErrorMessage { get; init; } = "";
    }
}