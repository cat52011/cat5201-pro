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
        string? OutputImagePath = null
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
        List<UsageRecord>? UsageRecords = null
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
