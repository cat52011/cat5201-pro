using System.Threading;
using System.Threading.Tasks;

namespace test
{
    /// <summary>
    /// Slice B2：執行決策解析的抽象——AgentRuntime 依賴此介面而非具體 NodeExecutionDecisionResolver
    /// （後者持 MainWindow 讀 UI 選單狀態）。這是「執行腦可被測試/可被非 WPF 宿主重用」的最後一塊硬依賴。
    /// </summary>
    public interface IExecutionDecisionResolver
    {
        Task<NodeExecutionDecision> ResolveAsync(INodeContext nodeContext, string topText, CancellationToken ct);
    }

    /// <summary>
    /// Slice B2：決策收尾的抽象。AgentRuntime 只用 FinalizeDecision（純函式：把實際執行結果寫回決策），
    /// 具體 NodeExecutionFinalizer 另有 UI 呈現職責（Present），不屬於執行腦的依賴面。
    /// </summary>
    public interface IDecisionFinalizer
    {
        NodeExecutionDecision FinalizeDecision(NodeExecutionDecision decision, AiFallbackExecutionResult execution);
    }
}
