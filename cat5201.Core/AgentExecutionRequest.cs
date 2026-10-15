namespace Cat5201
{
    public sealed class AgentExecutionRequest
    {
        public INodeContext Node { get; init; } = null!;   // Slice B2：執行腦只認抽象節點
        public AgentDefinition Agent { get; init; } = null!;

        public string TopText { get; init; } = "";
        public bool UseStreaming { get; init; }
        public System.Action<string>? OnDelta { get; init; }

        public int DelegationDepth { get; init; }

        public System.Threading.CancellationToken CancellationToken { get; init; }
        public bool ForceAgentProfile { get; init; }
        public bool SkipCapabilities { get; init; }
        public AgentWorkspace? Workspace { get; init; }

        // 使用者偏好區塊（最高優先），會注入 final synthesis / 最終執行的輸入。
        public string PreferenceBlock { get; init; } = "";

        // §6 第一層輸出判斷的結果（先跑一次 API 決定要簡報/報告/表格之中哪幾個）；
        // null 代表此次未判斷（例如子代理委派），AgentRuntime 會退回關鍵字 + TaskType 行為。
        public OutputIntent? OutputIntent { get; init; }

        /// <summary>
        /// 還在跑的輸出判斷。給了這個就不用先 await——輸出判斷與「自動選模型」互不相依，
        /// 一前一後排隊等於每個任務都白白多等一次 API 來回（實測約 1～2 秒）。
        /// AgentRuntime 會在真正需要用到意圖的那一步才收這個結果。
        /// </summary>
        public System.Threading.Tasks.Task<OutputIntent?>? OutputIntentTask { get; init; }
    }
}