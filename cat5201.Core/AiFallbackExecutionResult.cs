using System.Collections.Generic;

namespace Cat5201
{
    public sealed class AiFallbackExecutionResult
    {
        public bool IsSuccess { get; init; }

        public string Text { get; init; } = "";

        public string ActualModelId { get; init; } = "";

        public bool UsedFallback { get; init; }

        public string Summary { get; init; } = "";

        public string ErrorMessage { get; init; } = "";

        public IReadOnlyList<AiFallbackAttempt> Attempts { get; init; }
            = new List<AiFallbackAttempt>();
    }
}