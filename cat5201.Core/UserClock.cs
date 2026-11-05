using System;
using System.Threading;

namespace Cat5201
{
    /// <summary>
    /// 平台裡「使用者現在的時間」的唯一來源（不一定顯示在畫面上）。
    ///
    /// 凡是要回答「今天是哪一天」的地方都從這裡拿：給模型的目前日期（CurrentDateBlock）、
    /// 每日花費帳本與每日上限（SpendLedger）、儲值起算日。以前各處各自讀 DateTime.Now，
    /// 模型以為的「今天」和額度的「今天」理論上可能不同步，跨午夜的情況也沒辦法測。
    ///
    /// 平台在使用者自己的電腦上跑，所以「使用者時間」＝這台電腦的時間與時區。
    /// 檔名、日誌時間戳這類「記下某個時刻」的用途不必經過這裡。
    /// </summary>
    public static class UserClock
    {
        // 測試用：只影響設定它的那條非同步流程（AsyncLocal），平行跑的其他測試看不到。
        private static readonly AsyncLocal<Func<DateTimeOffset>?> _override = new();

        /// <summary>使用者現在的時間（含時區）。</summary>
        public static DateTimeOffset Now => _override.Value?.Invoke() ?? DateTimeOffset.Now;

        /// <summary>使用者的今天（當地日期）。</summary>
        public static DateTime Today => Now.Date;

        /// <summary>帳本用的日期鍵，例如 2026-11-06。</summary>
        public static string TodayKey => DayKey(Now);

        public static string DayKey(DateTimeOffset userTime) => userTime.ToString("yyyy-MM-dd");

        /// <summary>某個 UTC 時刻換成使用者時區的時間（例如「這筆錢是哪一天扣的」）。</summary>
        public static DateTimeOffset ToUserTime(DateTime utc)
        {
            var instant = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
            var forced = _override.Value;
            return forced != null ? instant.ToOffset(forced().Offset) : instant.ToLocalTime();
        }

        /// <summary>
        /// 測試用：在這條非同步流程裡把「現在」換成指定的時間，Dispose 後恢復。
        /// 傳函式而不是固定值，測試才能讓時間往前走（例如 23:59 扣款、00:01 退款）。
        /// </summary>
        internal static IDisposable Override(Func<DateTimeOffset> now)
        {
            var previous = _override.Value;
            _override.Value = now;
            return new Restore(() => _override.Value = previous);
        }

        private sealed class Restore : IDisposable
        {
            private Action? _undo;
            public Restore(Action undo) => _undo = undo;
            public void Dispose()
            {
                _undo?.Invoke();
                _undo = null;
            }
        }
    }
}
