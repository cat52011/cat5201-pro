using System.Collections.Generic;
using System.Threading.Tasks;

namespace test
{
    /// <summary>
    /// #3 補地基（服務層第一刀）：AgentRuntime 執行時需要「宿主」提供的能力集合。
    /// 讓 AgentRuntime 依賴這個抽象、而非具體 <see cref="MainWindow"/>（8000 行的上帝物件），
    /// 是把執行核心與 WPF 解耦、朝可測試 / 可遠端化（§17 階段三、web 化）前進的第一塊介面。
    ///
    /// 目前唯一實作＝MainWindow；所有成員簽章與 MainWindow 既有方法完全一致，本次抽取零行為變更。
    /// 註：介面仍引用 NodeControl（具體 UI 型別）——那是下一刀要抽的，本刀先解 MainWindow 這個最大的結。
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
        IReadOnlyList<MainWindow.AttachmentInfo> GetAttachmentsForNode(INodeContext node);
        IReadOnlyList<MainWindow.AttachmentInfo> GetEffectiveAttachmentsForNode(INodeContext node);
        string GetAttachmentsRootDir();
        string GetGeneratedFilesDir();

        // ── UI / 互動 ──
        void SetLiveDecisionResolving(INodeContext node, NodeExecutionDecision decision);
        Task<bool> ConfirmGenerationAsync(OrchestrationTaskType taskType, OutputIntent? outputIntent);
    }
}
