using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 流動工作流的排程規則：一個節點什麼時候才輪得到它跑。
    ///
    /// 這是領域邏輯不是介面邏輯，所以放在 Core——畫布只負責把「誰連到誰」告訴它。
    /// 抽出來的另一個理由是可測：菱形（A→B→D、A→C→D）的正確行為沒辦法靠人工點畫布驗，
    /// 那正是它壞了很久都沒人發現的原因。
    /// </summary>
    public static class FlowSchedule
    {
        public enum Gate
        {
            /// <summary>所有上游都完成了，現在可以跑。</summary>
            Run,

            /// <summary>這一輪已經開始過了，不要重複。</summary>
            AlreadyStarted,

            /// <summary>還有上游沒跑完——先不要跑，晚完成的那條分支稍後會再走到這裡。</summary>
            WaitForUpstream,

            /// <summary>有上游失敗了，這條路不會再有結果。</summary>
            UpstreamFailed,
        }

        /// <summary>
        /// 判斷某個節點現在該不該跑。
        /// </summary>
        /// <param name="node">要判斷的節點。</param>
        /// <param name="upstream">它所有「流動模式」上游的節點代號。</param>
        /// <param name="started">這一輪已經開始過的節點。</param>
        /// <param name="completed">這一輪已成功完成的節點。</param>
        /// <param name="failed">這一輪失敗的節點。</param>
        /// <param name="alreadySatisfied">
        /// 這一輪不會跑、但手上已經有成功結果的上游。
        ///
        /// 為什麼需要它：使用者很少「從頭跑整條鏈」，更常是手動跑完 B、再手動跑 C，
        /// 然後期待匯流的 D 自己接上去。只看這一輪的 completed 的話，兩次執行各自只知道
        /// 一半的上游，D 永遠停在 WaitForUpstream——畫面上就是「上游都跑完了，最下游卻不動」，
        /// 而且沒有任何訊息說明在等什麼（使用者 2026-10-13 回報）。
        ///
        /// 只能放「這一輪不會重跑」的上游：會重跑的那些，舊結果必須作廢，
        /// 否則 D 會拿著上一輪的舊內容提早開跑——那正是匯流本來要防的事。
        /// </param>
        public static Gate Evaluate(
            Guid node,
            IEnumerable<Guid> upstream,
            IReadOnlyCollection<Guid> started,
            IReadOnlyCollection<Guid> completed,
            IReadOnlyCollection<Guid> failed,
            IReadOnlyCollection<Guid>? alreadySatisfied = null)
        {
            if (started.Contains(node))
                return Gate.AlreadyStarted;

            var ups = (upstream ?? Enumerable.Empty<Guid>()).ToList();

            // 先看失敗：上游死了就沒有人會再來叫這個節點，讓呼叫端可以收掉等待狀態。
            if (ups.Any(failed.Contains))
                return Gate.UpstreamFailed;

            var satisfied = alreadySatisfied ?? Array.Empty<Guid>();
            if (ups.Any(u => !completed.Contains(u) && !satisfied.Contains(u)))
                return Gate.WaitForUpstream;

            return Gate.Run;
        }
    }
}
