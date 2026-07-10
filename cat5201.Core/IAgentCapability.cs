using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    public interface IAgentCapability
    {
        string Id { get; }

        AgentCapability RequiredAgentCapability { get; }

        bool CanHandle(AgentExecutionContext context);

        Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct);
    }
}