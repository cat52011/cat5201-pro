using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 「執行中預留額度」。
    ///
    /// 為什麼需要：每日預算原本只看「已經記進帳本的花費」，那是已經花完的錢。
    /// 在快到上限時啟動一個長任務、或同時啟動好幾個節點，每一個檢查的當下都還沒超標，
    /// 於是全部放行——跑完才發現超支好幾倍。預算檢查因此比較像「啟動門檻」而不是上限。
    ///
    /// 這裡讓每個會花錢的階段在開始前先「預留」一筆估算金額，結束就釋放；
    /// 預算檢查看的是「已花 + 預留」。估得不準沒關係，寧可保守：預留只是暫時佔住額度，
    /// 真正入帳的還是 UsageMeter 記到的實際花費。
    /// </summary>
    public static class SpendReservations
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, Reservation> _active = new(StringComparer.Ordinal);

        // ── 使用者授權的單次超支 ──
        //
        // 上限該是上限，但「直接拒絕」對使用者來說常常是最糟的結果：
        // 他正要做一份要交出去的簡報，系統只回一句「超過上限」，他得去設定改數字再重跑一次。
        // 所以改成：要超過時先問，問過了這一次就放行，範圍僅限該次執行。
        //
        // 用 AsyncLocal + 可變盒子：範圍由 AgentRuntime 在執行開頭開啟，確認框在範圍內蓋章，
        // 之後同一次執行衍生的平行工作（例如三張配圖同時生）都看得到這個授權，
        // 而別的節點、別的執行看不到。
        private static readonly System.Threading.AsyncLocal<System.Runtime.CompilerServices.StrongBox<bool>?> _overBudgetGrant = new();

        /// <summary>開一個「可由使用者授權超支」的範圍（一次執行一個）。</summary>
        public static IDisposable BeginOverBudgetScope()
        {
            var previous = _overBudgetGrant.Value;
            _overBudgetGrant.Value = new System.Runtime.CompilerServices.StrongBox<bool>(false);
            return new ScopeHandle(previous);
        }

        /// <summary>在範圍內記下「使用者已同意這一次可以超過上限」。</summary>
        public static void GrantOverBudget()
        {
            var box = _overBudgetGrant.Value;
            if (box != null) box.Value = true;
        }

        /// <summary>這一次執行是否已獲使用者授權超支。</summary>
        public static bool OverBudgetGranted => _overBudgetGrant.Value?.Value == true;

        /// <summary>
        /// 這筆估算費用會不會超過今日上限（給確認框事先判斷用，不佔用額度）。
        /// </summary>
        public static bool WouldExceedBudget(double usd)
        {
            int budgetTwd = SpendLedger.DailyBudgetTwd;
            double rate = SpendLedger.UsdToTwdRate;
            if (budgetTwd <= 0 || rate <= 0 || usd <= 0) return false;

            lock (_lock)
            {
                DropExpiredUnsafe();
                return (SpendLedger.TodayUsd + ReservedUnsafe() + usd) * rate > budgetTwd;
            }
        }

        private sealed class ScopeHandle : IDisposable
        {
            private readonly System.Runtime.CompilerServices.StrongBox<bool>? _previous;
            private bool _done;
            public ScopeHandle(System.Runtime.CompilerServices.StrongBox<bool>? previous) => _previous = previous;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _overBudgetGrant.Value = _previous;
            }
        }

        private sealed record Reservation(string Label, double Usd, DateTime StartedUtc);

        /// <summary>預留失效時間：任務崩潰沒釋放時，額度不能被永久佔住。</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

        /// <summary>目前預留中的總額（美元）。</summary>
        public static double ReservedUsd
        {
            get
            {
                lock (_lock)
                {
                    DropExpiredUnsafe();
                    return _active.Values.Sum(r => r.Usd);
                }
            }
        }

        /// <summary>執行中的預留項目（決策窗／花費面板顯示用）。</summary>
        public static IReadOnlyList<(string Label, double Usd)> Active()
        {
            lock (_lock)
            {
                DropExpiredUnsafe();
                return _active.Values.Select(r => (r.Label, r.Usd)).ToList();
            }
        }

        /// <summary>
        /// 預留一筆額度，用完 Dispose 釋放。usd &lt;= 0 視為不預留（回傳空的 handle）。
        /// 不檢查預算——要讓預算成為上限請用 <see cref="TryReserve"/>。
        /// </summary>
        public static IDisposable Reserve(string label, double usd)
            => TryReserve(label, usd, budgetTwd: 0, usdToTwd: 0)
               ?? new Handle(null);

        /// <summary>
        /// 預留一筆額度，<b>但先確認加上去不會超過今日預算</b>；會超過就回 null，呼叫端據此拒絕執行。
        ///
        /// 為什麼要有這個：原本的流程是「入口檢查預算 → 通過 → 預留」，兩步之間沒有綁在一起，
        /// 所以正在獲准的那個任務自己就可以把金額推過上限——預算實際上只是啟動門檻。
        /// 檢查與佔用必須在同一個鎖裡完成，才會是真正的上限。
        /// </summary>
        /// <param name="budgetTwd">今日預算（0＝不限制）。</param>
        /// <param name="usdToTwd">匯率，用來把這筆預留換算成台幣比較。</param>
        public static IDisposable? TryReserve(string label, double usd, int budgetTwd, double usdToTwd)
        {
            if (usd <= 0 || double.IsNaN(usd) || double.IsInfinity(usd))
                return new Handle(null);

            string id = Guid.NewGuid().ToString("N");
            lock (_lock)
            {
                DropExpiredUnsafe();

                if (budgetTwd > 0 && usdToTwd > 0 && !OverBudgetGranted)
                {
                    double committedTwd = (SpendLedger.TodayUsd + ReservedUnsafe()) * usdToTwd;
                    if (committedTwd + usd * usdToTwd > budgetTwd)
                    {
                        AppLog.Info("Spend",
                            $"預留遭拒：這筆 US${usd:0.####}（{label}）會讓今日合計超過 NT${budgetTwd}");
                        return null;
                    }
                }

                _active[id] = new Reservation(label ?? "", usd, DateTime.UtcNow);
            }

            AppLog.Info("Spend", $"預留額度 US${usd:0.####}（{label}）→ 預留中合計 US${ReservedUsd:0.####}");
            return new Handle(id);
        }

        /// <summary>
        /// 依目前的每日預算預留。超過就回 null——呼叫端必須據此中止，不要照跑。
        /// </summary>
        public static IDisposable? TryReserveWithinBudget(string label, double usd)
            => TryReserve(label, usd, SpendLedger.DailyBudgetTwd, SpendLedger.UsdToTwdRate);

        /// <summary>已在鎖內時取預留總額（不要再進一次鎖）。</summary>
        private static double ReservedUnsafe() => _active.Values.Sum(r => r.Usd);

        private static void Release(string id)
        {
            lock (_lock)
            {
                if (!_active.Remove(id)) return;
            }
        }

        private static void DropExpiredUnsafe()
        {
            var now = DateTime.UtcNow;
            foreach (var key in _active.Where(kv => now - kv.Value.StartedUtc > MaxAge).Select(kv => kv.Key).ToList())
            {
                AppLog.Warn("Spend", $"預留額度逾時自動釋放：{_active[key].Label}");
                _active.Remove(key);
            }
        }

        /// <summary>測試用：清掉所有預留與超支授權。</summary>
        internal static void ResetForTests()
        {
            lock (_lock) _active.Clear();
            _overBudgetGrant.Value = null;
        }

        private sealed class Handle : IDisposable
        {
            private string? _id;
            public Handle(string? id) => _id = id;

            public void Dispose()
            {
                if (_id == null) return;
                Release(_id);
                _id = null;
            }
        }
    }
}
