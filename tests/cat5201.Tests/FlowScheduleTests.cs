using System;
using System.Collections.Generic;
using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 匯流（菱形）流程的排程規則。
    ///
    /// 原本用一個共用的 visited 防重複，結果 A→B→D、A→C→D 這種圖裡，
    /// B、C 並行、先跑完的那條直接啟動 D，另一條稍後被當成「跑過了」而略過——
    /// D 少吃了一條分支的結果，而且全程沒有錯誤訊息。靠人工點畫布驗不出來，
    /// 所以規則抽出來用測試釘住。
    /// </summary>
    public class FlowScheduleTests
    {
        private static readonly Guid A = Guid.NewGuid();
        private static readonly Guid B = Guid.NewGuid();
        private static readonly Guid C = Guid.NewGuid();
        private static readonly Guid D = Guid.NewGuid();

        private static FlowSchedule.Gate Gate(
            Guid node, IEnumerable<Guid> upstream,
            IEnumerable<Guid>? started = null,
            IEnumerable<Guid>? completed = null,
            IEnumerable<Guid>? failed = null)
            => FlowSchedule.Evaluate(
                node, upstream,
                (started ?? Enumerable.Empty<Guid>()).ToList(),
                (completed ?? Enumerable.Empty<Guid>()).ToList(),
                (failed ?? Enumerable.Empty<Guid>()).ToList());

        [Fact]
        public void TheJoinWaitsForTheSlowerBranch()
        {
            // A 跑完，B 先完成、C 還在跑 → D 這時還不能動。
            Assert.Equal(
                FlowSchedule.Gate.WaitForUpstream,
                Gate(D, new[] { B, C }, started: new[] { A, B, C }, completed: new[] { A, B }));

            // C 也完成了 → 這時 D 才輪得到。
            Assert.Equal(
                FlowSchedule.Gate.Run,
                Gate(D, new[] { B, C }, started: new[] { A, B, C }, completed: new[] { A, B, C }));
        }

        [Fact]
        public void AWaitingNodeIsNotPermanentlySkipped()
        {
            // 「先不要跑」與「已經跑過」必須是不同的狀態——
            // 舊的做法把兩者混在同一個 visited 集合裡，才會發生永久略過。
            var waiting = Gate(D, new[] { B, C }, started: new[] { B }, completed: new[] { B });
            Assert.Equal(FlowSchedule.Gate.WaitForUpstream, waiting);
            Assert.NotEqual(FlowSchedule.Gate.AlreadyStarted, waiting);
        }

        [Fact]
        public void ANodeNeverStartsTwice()
            => Assert.Equal(
                FlowSchedule.Gate.AlreadyStarted,
                Gate(D, new[] { B }, started: new[] { D }, completed: new[] { B }));

        [Fact]
        public void AFailedUpstreamStopsThatPathInsteadOfHanging()
        {
            // B 失敗之後不會再有人來叫 D，要能分辨這個狀態，才不會讓它卡在「等待中」。
            Assert.Equal(
                FlowSchedule.Gate.UpstreamFailed,
                Gate(D, new[] { B, C }, started: new[] { B, C }, completed: new[] { C }, failed: new[] { B }));
        }

        [Fact]
        public void ANodeWithNoUpstreamRunsImmediately()
            => Assert.Equal(FlowSchedule.Gate.Run, Gate(A, Array.Empty<Guid>()));

        [Fact]
        public void ALinearChainStillWorks()
        {
            Assert.Equal(FlowSchedule.Gate.WaitForUpstream, Gate(B, new[] { A }));
            Assert.Equal(FlowSchedule.Gate.Run, Gate(B, new[] { A }, completed: new[] { A }));
        }

        [Fact]
        public void 上游是在前一次執行跑完的也算數()
        {
            // 使用者 2026-10-13 回報：B、C 都跑完了，匯流的 D 卻不動。
            // 原因是只看「這一輪」的完成名單——手動跑完 B 再手動跑 C，
            // 兩次執行各自只知道一半的上游，D 永遠停在等待。
            var b = Guid.NewGuid();
            var c = Guid.NewGuid();
            var d = Guid.NewGuid();

            // 這一輪只跑 B（和它下游的 D）；C 不在這一輪，但手上已經有成功結果。
            var gate = FlowSchedule.Evaluate(
                d,
                new[] { b, c },
                started: new HashSet<Guid>(),
                completed: new HashSet<Guid> { b },
                failed: new HashSet<Guid>(),
                alreadySatisfied: new HashSet<Guid> { c });

            Assert.Equal(FlowSchedule.Gate.Run, gate);
        }

        [Fact]
        public void 這一輪會重跑的上游不能拿舊結果充數()
        {
            // 從頭跑整條鏈時，C 也會重跑——它上一輪的舊結果必須作廢，
            // 否則 D 會拿著過時的內容提早開跑，那正是匯流要防的事。
            var b = Guid.NewGuid();
            var c = Guid.NewGuid();
            var d = Guid.NewGuid();

            var gate = FlowSchedule.Evaluate(
                d,
                new[] { b, c },
                started: new HashSet<Guid>(),
                completed: new HashSet<Guid> { b },
                failed: new HashSet<Guid>(),
                alreadySatisfied: new HashSet<Guid>());   // C 在這一輪裡，所以不放進來

            Assert.Equal(FlowSchedule.Gate.WaitForUpstream, gate);
        }

        [Fact]
        public void 上游這一輪失敗時舊的成功結果不能救它()
        {
            var b = Guid.NewGuid();
            var c = Guid.NewGuid();
            var d = Guid.NewGuid();

            var gate = FlowSchedule.Evaluate(
                d,
                new[] { b, c },
                started: new HashSet<Guid>(),
                completed: new HashSet<Guid> { c },
                failed: new HashSet<Guid> { b },
                alreadySatisfied: new HashSet<Guid> { b });

            Assert.Equal(FlowSchedule.Gate.UpstreamFailed, gate);
        }

    }
}
