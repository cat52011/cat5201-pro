using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 每日預算必須是「上限」，不能只是「啟動門檻」。
    ///
    /// 原本的流程是：入口檢查預算 → 通過 → 預留。兩步沒有綁在一起，
    /// 所以正在獲准的那個任務自己就能把今日金額推過上限，跑完才發現超支。
    /// 檢查與佔用要在同一個鎖裡完成。
    /// </summary>
    // 集合名稱要跟其他碰帳本的測試一字不差，拼錯等於自己一個集合、照樣平行跑。
    [Collection("spend-ledger")]
    public class SpendCapTests
    {
        [Fact]
        public void AReservationThatWouldBreachTheBudgetIsRefused()
        {
            SpendReservations.ResetForTests();
            try
            {
                // 預算要相對於「今日已經花掉的」來算，否則這個測試會受執行環境的帳本影響
                // （在已有花費的機器上就會失敗——第一次寫的時候就踩到了）。
                double rate = SpendLedger.UsdToTwdRate;
                int budget = (int)((SpendLedger.TodayUsd + 10) * rate) + 1;

                // 先佔住 US$8，還剩 US$2 的空間。
                var first = SpendReservations.TryReserve("先跑的任務", 8, budget, rate);
                Assert.NotNull(first);

                // 再要 US$5 會超過 → 必須被拒絕，而不是照樣放行。
                Assert.Null(SpendReservations.TryReserve("會超支的任務", 5, budget, rate));

                // 剩餘空間內的仍然可以。
                var third = SpendReservations.TryReserve("剛好放得下", 1, budget, rate);
                Assert.NotNull(third);

                // 放掉第一筆之後，空間回來了。
                first!.Dispose();
                Assert.NotNull(SpendReservations.TryReserve("釋放後", 5, budget, rate));
            }
            finally { SpendReservations.ResetForTests(); }
        }

        [Fact]
        public void NoBudgetMeansNoLimit()
        {
            SpendReservations.ResetForTests();
            try
            {
                Assert.NotNull(SpendReservations.TryReserve("不限制", 9999, budgetTwd: 0, usdToTwd: 32));
            }
            finally { SpendReservations.ResetForTests(); }
        }
    }
}
