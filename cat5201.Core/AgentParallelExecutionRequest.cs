using System.Collections.Generic;

namespace Cat5201
{
    public sealed class AgentParallelExecutionRequest
    {
        public INodeContext Node { get; init; } = null!;

        public string OriginalInput { get; init; } = "";

        public IReadOnlyList<AgentParallelTask> Tasks { get; init; }
            = new List<AgentParallelTask>();

        public AgentWorkspace Workspace { get; init; } = null!;
    }

    public sealed class AgentParallelTask
    {
        public string AgentId { get; init; } = "";

        public string Instruction { get; init; } = "";

        public string Purpose { get; init; } = "";
    }
}