using System.Collections.Generic;

namespace test
{
    /// <summary>
    /// 手機鏡像（§17 階段一）用的「UI 無關」狀態快照。
    /// 這是把執行/工作區狀態從 WPF 抽離的「服務層第一塊磚」：
    /// 任何前端（手機網頁、未來 web 版）都只認這個資料模型，不認 NodeControl / MainWindow。
    /// 故意只放純資料、不放任何 System.Windows 型別。
    /// </summary>
    public sealed class WorkspaceSnapshot
    {
        /// <summary>每次推送遞增；前端可用來判斷是否有更新。</summary>
        public long Version { get; set; }

        public string GeneratedAtUtc { get; set; } = "";
        public string GeneratedAtLocal { get; set; } = "";

        /// <summary>是否有任何節點正在執行（手機端顯示「執行中」全域指示）。</summary>
        public bool AnyRunning { get; set; }

        public int NodeCount { get; set; }

        public List<NodeSnapshot> Nodes { get; set; } = new();

        /// <summary>目前有一個「要不要產生檔案／媒體」的二次確認在等回答（§17 階段二）；null = 沒有。</summary>
        public PendingConfirmationSnapshot? Pending { get; set; }

        /// <summary>可選模型清單（§17 階段三：手機下指令時可指定模型）。</summary>
        public List<MirrorModelOption> Models { get; set; } = new();
    }

    /// <summary>手機端模型選項。</summary>
    public sealed class MirrorModelOption
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>待回答的二次確認（手機端顯示橫幅 + 是/否）。</summary>
    public sealed class PendingConfirmationSnapshot
    {
        /// <summary>偵測到即將產生的東西（例如「簡報 PPTX、圖片」）。</summary>
        public string What { get; set; } = "";

        /// <summary>距離自動同意的剩餘秒數；-1 = 沒有自動同意倒數。手機每次輪詢會拿到更新值＝近似倒數。</summary>
        public int RemainingSeconds { get; set; } = -1;
    }

    /// <summary>單一節點的唯讀鏡像狀態。</summary>
    public sealed class NodeSnapshot
    {
        public string Id { get; set; } = "";

        /// <summary>節點輸入的第一行（當標題用）。</summary>
        public string Title { get; set; } = "";

        /// <summary>idle / running / success / failed。</summary>
        public string Status { get; set; } = "idle";

        /// <summary>中文狀態標籤（閒置 / 執行中 / 成功 / 失敗）。</summary>
        public string StatusLabel { get; set; } = "";

        public string Model { get; set; } = "";

        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }

        /// <summary>是否有來自 API 的真實 token 用量（否則前端顯示「估算」）。</summary>
        public bool HasRealTokens { get; set; }

        /// <summary>圖片/影片等媒體生成累積費用（USD，token 無法計價的部分）。</summary>
        public double MediaCostUsd { get; set; }

        /// <summary>執行中的進度提示（例如「影片生成中 42%」）。</summary>
        public string LoadingHint { get; set; } = "";

        /// <summary>輸出文字預覽（截斷）。</summary>
        public string OutputPreview { get; set; } = "";

        /// <summary>已產生的檔案名稱（PDF/PPTX/DOCX/圖片/影片…）。</summary>
        public List<string> Files { get; set; } = new();

        /// <summary>手機端可否「停止」此節點（執行中才可）。</summary>
        public bool CanStop { get; set; }

        /// <summary>手機端可否「重跑」此節點（非執行中且有輸入才可）。</summary>
        public bool CanRerun { get; set; }
    }

    /// <summary>
    /// 手機端送來的輕操控指令（§17 階段二）。UI 無關 DTO——server 只認這個，不認 NodeControl。
    /// 這是「把指令從前端抽離」的服務層第二塊磚。
    /// </summary>
    public sealed class MirrorCommand
    {
        /// <summary>目標節點 Id（NodeControl.Id 的字串）；newnode / confirm / reject 等不綁節點的動作可留空。</summary>
        public string NodeId { get; set; } = "";

        /// <summary>動作：stop / rerun / confirm / reject / newnode（§17 階段三：手機直接下指令）。</summary>
        public string Action { get; set; } = "";

        /// <summary>newnode 用：要執行的指令文字。</summary>
        public string Text { get; set; } = "";

        /// <summary>newnode 用：要連接的上游節點。空字串＝自動（最後執行節點）；"none"＝不連接；其他＝節點 Id。</summary>
        public string ParentNodeId { get; set; } = "";

        /// <summary>newnode 用：指定模型 Id；空字串＝預設。</summary>
        public string ModelId { get; set; } = "";
    }

    /// <summary>指令執行結果，回給手機顯示。</summary>
    public sealed class MirrorCommandResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
    }
}
