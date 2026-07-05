namespace test
{
    public sealed class AgentRuntimeFactory
    {
        private readonly MainWindow _main;
        private readonly IExecutionDecisionResolver _decisionResolver;
        private readonly IDecisionFinalizer _executionFinalizer;
        private readonly System.Func<INodeContext, string, NodeExecutionDecision, System.Action<string>?, bool, System.Threading.CancellationToken, System.Threading.Tasks.Task<AiFallbackExecutionResult>> _executeWithFallbackAsync;

        public AgentRuntimeFactory(
            MainWindow main,
            IExecutionDecisionResolver decisionResolver,
            IDecisionFinalizer executionFinalizer,
            System.Func<INodeContext, string, NodeExecutionDecision, System.Action<string>?, bool, System.Threading.CancellationToken, System.Threading.Tasks.Task<AiFallbackExecutionResult>> executeWithFallbackAsync)
        {
            _main = main;
            _decisionResolver = decisionResolver;
            _executionFinalizer = executionFinalizer;
            _executeWithFallbackAsync = executeWithFallbackAsync;
        }

        public AgentRuntime Create()
        {
            return new AgentRuntime(
                _main,
                _decisionResolver,
                _executeWithFallbackAsync,
                _executionFinalizer);
        }
    }
}