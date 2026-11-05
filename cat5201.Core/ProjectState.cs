using System;
using System.Collections.Generic;

namespace Cat5201
{
    // ===== 專案檔(畫布存檔)的資料模型 =====
    // 第 1 刀(拆上帝物件):自 MainWindow 巢狀 record 原樣搬出到 Core。
    // JSON 相容性:System.Text.Json 只看屬性名,巢狀→頂層不影響既有 .json 專案檔。
    // 放 Core 的理由:Phase C 的 API 宿主要能讀寫專案檔,資料模型不能被 WPF 綁架。

    public record NodeState(
        string Id,
        double X,
        double Y,
        double Width,
        double Height,
        string? TopText,
        string? BottomText,
        bool TopLocked,
        double FontSize,
        string? AgentId = null,
        string? NodeModel = null,
        string? TaskMode = null,
        bool UnsupportedDownstreamNode = false,
        List<string>? OutputFilePaths = null,
        string? OutputImagePath = null,
        // 協作模式：這個節點由哪幾個模型一起回答（2026-10-29）。舊專案檔沒有 → null。
        List<string>? CollaborationModels = null
    );

    public record ConnState(string StartId, string EndId, string StartThumb, string EndThumb, bool FlowMode = false);

    public record AttachmentState(
        string NodeId,
        string FileName,
        string RelativePath,
        string MimeType,
        string Kind
    );

    public record ExecutionLogState(
        string NodeId,
        DateTime StartedAtUtc,
        DateTime EndedAtUtc,
        long DurationMs,

        string SelectionMode,
        string Resolver,
        string WorkspaceSummary,
        List<string> WorkspaceArtifactDetails,
        List<AgentWorkspaceArtifactRecord>? WorkspaceArtifacts,
        string RequestedModelId,
        string PlannedModelId,
        string ActualModelId,

        string TaskMode,
        double Confidence,

        string ResolverReason,
        List<string> ResolverKeywords,

        bool CapabilityAdjusted,
        string CapabilityReason,

        string CapabilityRequestedModelId,
        string CapabilityResolvedModelId,
        string CapabilityRequired,
        string CapabilityMissing,
        bool CapabilityStreamingAdjusted,

        List<AgentCapabilityTraceItem> CapabilityTrace,

        string RequestedAgentId,
        string ActualAgentId,

        bool RuntimeFallbackUsed,
        string RuntimeFallbackSummary,

        bool Success,
        string ErrorMessage,

        List<AiFallbackAttempt> FallbackAttempts,

        int InputTokens = 0,
        int OutputTokens = 0,
        string CostDisplay = "",
        // 逐筆帳目（2026-09-17 起）；舊專案檔沒有此欄位 → null，顯示時只剩總額文字。
        List<UsageRecord>? UsageRecords = null,

        // 稽查三件組（2026-10-27 起）：這三個欄位在執行當下就產生了，卻一直沒有存檔，
        // 重開專案後決策窗就少了「召回了哪些記憶、當時生效的偏好是什麼、輸出意圖怎麼判的」。
        // 對一個主打可追溯的產品，這是核心資料遺失。舊專案檔沒有這些欄位 → 預設空值。
        MemoryRecallStats? MemoryRecall = null,
        string EffectivePreferences = "",
        string OutputIntentSummary = "",

        // 各步驟實際收到了什麼（2026-10-29）。舊專案檔沒有 → null。完整內容在 StepInputLogPath 指的檔案裡。
        List<StepInputRecord>? StepInputs = null,
        string StepInputLogPath = "",

        // 能力判斷摘要（2026-11-05 起）。舊專案檔沒有 → 空字串。
        string CapabilityPlanSummary = ""
    );

    public record AppState(
        DateTime CreatedAt,
        string? InitialNodeId,
        List<NodeState> Nodes,
        List<ConnState> Connections,
        List<AttachmentState> Attachments,
        List<ExecutionLogState>? ExecutionLogs = null,
        bool FileNameLocked = false,
        bool AutoModelSelectionEnabled = false,
        bool AdvancedAutoResolverEnabled = false,
        string DownstreamAutoMode = "OneClick",
        string PresentationEngine = "Claude",
        Dictionary<string, string>? TaskRoutingOverrides = null,
        bool BlockOpus = false,
        bool BlockDeepResearch = false,
        int ManualTimeoutSeconds = 0,
        // 專案檔格式版本(#4):0=加欄位前的舊檔(缺欄位時 record 預設)。
        // 之後格式演進靠它分流。目前版本=ProjectStore.CurrentSchemaVersion。
        int SchemaVersion = 0
    );
}
