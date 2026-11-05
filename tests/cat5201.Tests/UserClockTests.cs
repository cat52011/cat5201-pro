using System;
using System.IO;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 平台唯一的使用者時間（UserClock）：給模型的目前日期和每日花費帳本用同一個「今天」，
    /// 而且跨午夜可以測。2026-11-06 起。
    /// </summary>
    [Collection("spend-ledger")]  // 會寫全域帳本，跟其他帳本測試循序執行
    public class UserClockTests
    {
        private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);

        private static DateTimeOffset At(int month, int day, int hour, int minute)
            => new(2026, month, day, hour, minute, 0, Taipei);

        private static string FreshDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>把時鐘撥到某一刻，讀那一天的帳本總額。</summary>
        private static double TotalOn(ref DateTimeOffset clock, DateTimeOffset day)
        {
            var keep = clock;
            clock = day;
            double usd = SpendLedger.TodayUsd;
            clock = keep;
            return usd;
        }

        [Fact]
        public void Override_ChangesNowAndToday_ThenRestores()
        {
            var t = At(11, 5, 23, 59);
            using (UserClock.Override(() => t))
            {
                Assert.Equal("2026-11-05", UserClock.TodayKey);
                t = t.AddMinutes(2);
                Assert.Equal("2026-11-06", UserClock.TodayKey);   // 時間可以往前走
            }

            Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), UserClock.TodayKey);   // 結束後恢復真實時間
        }

        [Fact]
        public void ToUserTime_UsesTheUsersTimeZone()
        {
            var t = At(11, 6, 0, 3);
            using (UserClock.Override(() => t))
            {
                // UTC 11/5 16:30 在台北是 11/6 00:30——帳本要記在 11/6。
                var user = UserClock.ToUserTime(new DateTime(2026, 11, 5, 16, 30, 0, DateTimeKind.Utc));
                Assert.Equal("2026-11-06", UserClock.DayKey(user));
            }
        }

        [Fact]
        public void Ledger_UsesTheUserClocksDay()
        {
            SpendLedger.Initialize(FreshDir());
            var clock = At(11, 5, 23, 59);
            using (UserClock.Override(() => clock))
            {
                SpendLedger.Add(0.50, "test", "openai");
                clock = At(11, 6, 0, 1);
                SpendLedger.Add(0.25, "test", "openai");

                Assert.Equal(0.25, SpendLedger.TodayUsd, 6);                          // 過了午夜是新的一天
                Assert.Equal(0.50, TotalOn(ref clock, At(11, 5, 12, 0)), 6);          // 前一天的錢還在前一天
                Assert.Equal(0.75, SpendLedger.MonthUsdFor("openai"), 6);             // 本月照樣加總
            }
        }

        [Fact]
        public void Refund_AfterMidnight_GoesBackToTheChargeDay()
        {
            // 影片 23:58 送出先記帳，00:03 雲端回報失敗才沖銷（官方：沒成功生成不收費）。
            SpendLedger.Initialize(FreshDir());
            var clock = At(11, 5, 23, 58);
            using (UserClock.Override(() => clock))
            {
                var charge = UsageMeter.RecordMetered(UsageKinds.Video, AiModels.OpenAi_Gpt56, 8, "秒", 1.20,
                    note: "測試：送出", purpose: "影片生成");
                Assert.Equal(1.20, SpendLedger.TodayUsd, 6);

                clock = At(11, 6, 0, 3);
                SpendLedger.Add(0.10, "隔天的其他花費", "openai");
                UsageMeter.RecordMetered(UsageKinds.Video, AiModels.OpenAi_Gpt56, 8, "秒", -1.20,
                    note: "測試：失敗沖銷", purpose: "影片失敗沖銷", refundsChargeAtUtc: charge.AtUtc);

                Assert.Equal(0.10, SpendLedger.TodayUsd, 6);                          // 隔天不受影響
                Assert.Equal(0.00, TotalOn(ref clock, At(11, 5, 12, 0)), 6);          // 前一天那筆被沖掉
            }
        }

        [Fact]
        public void Refund_DaysLater_AfterRestart_StillGoesBackToTheChargeDay()
        {
            // 影片可以在重啟後才取回結果，失敗沖銷可能隔好幾天；以前會從沖銷當天扣，當天是 0 就整筆不見。
            SpendLedger.Initialize(FreshDir());
            var clock = At(11, 5, 20, 0);
            using (UserClock.Override(() => clock))
            {
                SpendLedger.Add(2.00, "影片生成", "google");
                DateTime chargedAtUtc = clock.UtcDateTime;

                clock = At(11, 7, 9, 0);
                SpendLedger.Subtract(2.00, "影片失敗沖銷", "google", chargedAtUtc);

                Assert.Equal(0.00, SpendLedger.TodayUsd, 6);
                Assert.Equal(0.00, TotalOn(ref clock, At(11, 5, 12, 0)), 6);
                Assert.Equal(0.00, SpendLedger.UsdForSince("google", new DateTime(2026, 11, 1)), 6);
            }
        }

        [Fact]
        public void Refund_WithoutChargeTime_StillUsesToday()
        {
            // 沒說扣款時間的舊呼叫端照舊：沖今天。
            SpendLedger.Initialize(FreshDir());
            var clock = At(11, 6, 10, 0);
            using (UserClock.Override(() => clock))
            {
                SpendLedger.Add(1.00, "test");
                SpendLedger.Subtract(0.40, "test");
                Assert.Equal(0.60, SpendLedger.TodayUsd, 6);
            }
        }
    }
}
