using System;
using System.IO;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 每日預算原本只看「已入帳的花費」，所以快到上限時同時啟動多個長任務會全部放行，
    /// 跑完才發現超支——預算變成啟動門檻而不是上限。加入「執行中預留」後才是真的上限。
    /// </summary>
    [Collection("spend-ledger")]
    public class SpendReservationTests
    {
        private static void FreshLedger()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-reserve-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            SpendLedger.Initialize(dir);
            SpendReservations.ResetForTests();
        }

        [Fact]
        public void ReservationCountsTowardTheDailyBudget()
        {
            FreshLedger();

            // 上限 NT$100；已花 NT$32（US$1）。
            SpendLedger.Add(1.0, "test");
            Assert.False(SpendLedger.IsOverDailyBudget(100));

            // 啟動一個估算 US$3（≈NT$96）的長任務：加起來已經超過上限，不該再放行別的任務。
            using (SpendReservations.Reserve("長任務", 3.0))
            {
                Assert.True(SpendLedger.IsOverDailyBudget(100));
                Assert.Equal(3.0, SpendReservations.ReservedUsd, 4);
                Assert.Contains("執行中預留", SpendLedger.CommittedDisplay());
            }

            // 任務結束就釋放額度。
            Assert.Equal(0, SpendReservations.ReservedUsd, 4);
            Assert.False(SpendLedger.IsOverDailyBudget(100));
        }

        [Fact]
        public void ConcurrentReservationsAccumulate()
        {
            FreshLedger();

            using var a = SpendReservations.Reserve("圖片 A", 0.25);
            using var b = SpendReservations.Reserve("圖片 B", 0.25);
            using var c = SpendReservations.Reserve("圖片 C", 0.25);

            Assert.Equal(0.75, SpendReservations.ReservedUsd, 4);
            Assert.Equal(3, SpendReservations.Active().Count);
        }

        [Fact]
        public void ZeroOrNegativeEstimatesReserveNothing()
        {
            FreshLedger();

            using var none = SpendReservations.Reserve("免費步驟", 0);
            using var bad = SpendReservations.Reserve("壞估算", double.NaN);

            Assert.Equal(0, SpendReservations.ReservedUsd, 4);
        }

        [Fact]
        public void BudgetWithoutLimitIsNeverBlocked()
        {
            FreshLedger();
            SpendLedger.Add(999, "test");
            using var huge = SpendReservations.Reserve("巨額", 999);

            Assert.False(SpendLedger.IsOverDailyBudget(0));   // 0 = 不限制
        }
    }
}
