using System;
using System.Collections.Generic;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 接線規則。這些情況在畫布上要靠人工排出形狀才試得到一次，
    /// 排錯了也不會有人發現——所以規則抽成純函式在這裡釘住。
    /// </summary>
    public class ConnectionRulesTests
    {
        private static readonly Guid A = NewId(1);
        private static readonly Guid B = NewId(2);
        private static readonly Guid C = NewId(3);
        private static readonly Guid D = NewId(4);
        private static readonly Guid E = NewId(5);

        [Fact]
        public void 空白畫布上任何兩個節點都接得起來()
        {
            Assert.Equal(ConnectionRules.Verdict.Ok,
                ConnectionRules.Evaluate(Edges(), A, B));
        }

        [Fact]
        public void 不能接自己()
        {
            Assert.Equal(ConnectionRules.Verdict.SameNode,
                ConnectionRules.Evaluate(Edges(), A, A));
        }

        [Fact]
        public void 已經連過的不分方向都算重複()
        {
            var edges = Edges((A, B));

            Assert.Equal(ConnectionRules.Verdict.Duplicate,
                ConnectionRules.Evaluate(edges, A, B));

            // 反向也擋：A→B 之外再加一條 B→A 就是一個兩點的環。
            Assert.Equal(ConnectionRules.Verdict.Duplicate,
                ConnectionRules.Evaluate(edges, B, A));
        }

        [Fact]
        public void 接到自己的直接下游會成環()
        {
            Assert.Equal(ConnectionRules.Verdict.WouldCycle,
                ConnectionRules.Evaluate(Edges((A, B), (B, C)), C, A));
        }

        [Fact]
        public void 接到隔了好幾層的後代一樣會成環()
        {
            // A→B→C→D→E，再把 E 接回 A。人工要排出這個形狀才試得到一次。
            var edges = Edges((A, B), (B, C), (C, D), (D, E));

            Assert.Equal(ConnectionRules.Verdict.WouldCycle,
                ConnectionRules.Evaluate(edges, E, A));
        }

        [Fact]
        public void 菱形的第二條邊接得起來()
        {
            // A→B→D 已經在了，現在要補 C→D（C 也接在 A 底下）——這就是匯流。
            // 擋掉它的話，整個 FlowSchedule 的 join 規則永遠跑不到。
            var edges = Edges((A, B), (B, D), (A, C));

            Assert.Equal(ConnectionRules.Verdict.Ok,
                ConnectionRules.Evaluate(edges, C, D));
        }

        [Fact]
        public void 共同祖先的兩個分支彼此接得起來()
        {
            // A→B、A→C：B 和 C 是兄弟，不是彼此的後代，B→C 不會成環。
            var edges = Edges((A, B), (A, C));

            Assert.Equal(ConnectionRules.Verdict.Ok,
                ConnectionRules.Evaluate(edges, B, C));
        }

        [Fact]
        public void 後代集合不包含自己()
        {
            var set = ConnectionRules.Descendants(Edges((A, B), (B, C)), A);

            Assert.Contains(B, set);
            Assert.Contains(C, set);
            Assert.DoesNotContain(A, set);
        }

        [Fact]
        public void 圖裡已經有環時走訪不會無限迴圈()
        {
            // 舊存檔可能存著一個環（以前沒有擋）。讀進來時不能把程式卡死。
            var set = ConnectionRules.Descendants(Edges((A, B), (B, C), (C, A)), A);

            Assert.Contains(B, set);
            Assert.Contains(C, set);
        }

        private static List<(Guid From, Guid To)> Edges(params (Guid, Guid)[] edges)
        {
            var list = new List<(Guid From, Guid To)>();
            foreach (var (from, to) in edges)
                list.Add((from, to));
            return list;
        }

        private static Guid NewId(int seed)
        {
            var bytes = new byte[16];
            bytes[0] = (byte)seed;
            return new Guid(bytes);
        }
    }
}
