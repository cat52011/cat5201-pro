using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 畫布上「這兩個節點接不接得起來」的規則。
    ///
    /// 跟 <see cref="FlowSchedule"/> 同一個理由放在 Core：這是領域邏輯不是介面邏輯，
    /// 而且**人工拖放驗不完**——自己接自己、重複接、接成環、接到隔了五層的祖先，
    /// 每一種都要在畫布上手動排出那個形狀才試得到一次，排錯了還不會有人發現。
    /// 規則抽出來之後，畫布只負責回答「滑鼠放開時游標在誰身上」。
    /// </summary>
    public static class ConnectionRules
    {
        public enum Verdict
        {
            /// <summary>可以接。</summary>
            Ok,

            /// <summary>來源和目標是同一個節點。</summary>
            SameNode,

            /// <summary>這兩個已經連過了（不分方向）——再接一條只會疊在原地，看起來像沒反應。</summary>
            Duplicate,

            /// <summary>接下去會形成環，執行時繞不出來。</summary>
            WouldCycle,
        }

        /// <summary>
        /// 能不能把 source 接成 target 的上游（source → target）。
        /// </summary>
        /// <param name="edges">目前畫布上所有的連線，(上游, 下游)。</param>
        public static Verdict Evaluate(
            IEnumerable<(Guid From, Guid To)> edges, Guid source, Guid target)
        {
            if (source == target)
                return Verdict.SameNode;

            var list = (edges ?? Enumerable.Empty<(Guid, Guid)>()).ToList();

            if (list.Any(e => (e.From == source && e.To == target) ||
                              (e.From == target && e.To == source)))
                return Verdict.Duplicate;

            // 接 source → target 會成環，當且僅當 source 已經是 target 的後代
            // （沿著既有的邊，從 target 走得到 source）。
            if (Descendants(list, target).Contains(source))
                return Verdict.WouldCycle;

            return Verdict.Ok;
        }

        /// <summary>沿有向邊從 root 走得到的所有節點（不含 root 自己）。</summary>
        public static HashSet<Guid> Descendants(
            IEnumerable<(Guid From, Guid To)> edges, Guid root)
        {
            var list = (edges ?? Enumerable.Empty<(Guid, Guid)>()).ToList();
            var seen = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var e in list)
                {
                    if (e.From == cur && seen.Add(e.To))
                        queue.Enqueue(e.To);
                }
            }

            seen.Remove(root);
            return seen;
        }
    }
}
