using System.Collections.Generic;

namespace Cat5201
{
    public sealed class CodeAnalysisPayload
    {
        public string RequestType { get; init; } = "";
        public string Language { get; init; } = "";
        public string UserGoal { get; init; } = "";

        public IReadOnlyList<string> DetectedSignals { get; init; }
            = new List<string>();

        public IReadOnlyList<string> RequiredActions { get; init; }
            = new List<string>();

        public string Guidance { get; init; } = "";
    }
}