using System;

namespace Cat5201
{
    /// <summary>
    /// 服務層第二刀（Slice B1）：執行核心（AgentRuntime）看得到的「節點」抽象。
    /// AgentRuntime 對節點其實只需要兩件事：身分（Id）、進度提示（成本改由 UsageMeter 在服務層逐筆記帳）——
    /// 讓它依賴這個介面而非具體 <see cref="NodeControl"/>（WPF UserControl），
    /// 執行腦就與 UI 徹底斷開，可測試、可搬進 Core、可被 web/手機後端重用。
    ///
    /// 目前唯一實作＝NodeControl；UI 邊界（MainWindow / NodeService / Resolver）
    /// 需要具體控制項時以 cast 橋接（runtime 流動的本來就是 NodeControl，安全）。
    /// </summary>
    public interface INodeContext
    {
        /// <summary>節點身分（附件儲存、決策記錄、鏡像快照都以此為鍵）。</summary>
        Guid Id { get; }

        /// <summary>長任務（影片/圖片等）的即時進度提示，附加在 loading 文字後。</summary>
        void SetLoadingHint(string? hint);
    }
}
