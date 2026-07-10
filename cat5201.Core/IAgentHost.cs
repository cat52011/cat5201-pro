using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// #3 補地基（服務層第一刀）：AgentRuntime 執行時需要「宿主」提供的能力集合。
    /// 讓 AgentRuntime 依賴這個抽象、而非具體 MainWindow（上帝物件）。
    /// B3 後本介面已全 Core 型別化並移入 Core：任何宿主（WPF / 測試假件 / 未來 web 服務）都能實作。
    /// 目前實作＝MainWindow（桌面）與整合測試的 FakeHost。
    /// </summary>
    public interface IAgentHost
    {
        // ── 個人化 / 設定（純讀取）──
        bool IsAutoModelSelectionEnabled();
        bool IsAdvancedAutoResolverEnabled();
        PresentationEngine GetPresentationEngine();
        VeoModelTier GetVeoModelTier();
        string GetEffectiveVeoModel();
        string GetEffectiveVideoStylePrompt();

        // ── 附件 / 路徑 ──（Slice B1：改收 INodeContext，執行核心不再需要具體 NodeControl）
        IReadOnlyList<AttachmentInfo> GetAttachmentsForNode(INodeContext node);
        IReadOnlyList<AttachmentInfo> GetEffectiveAttachmentsForNode(INodeContext node);
        string GetAttachmentsRootDir();
        string GetGeneratedFilesDir();

        // ── UI / 互動 ──
        void SetLiveDecisionResolving(INodeContext node, NodeExecutionDecision decision);
        Task<bool> ConfirmGenerationAsync(OrchestrationTaskType taskType, OutputIntent? outputIntent);
    }
}
