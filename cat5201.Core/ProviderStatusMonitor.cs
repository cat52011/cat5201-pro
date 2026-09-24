using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    /// <summary>
    /// 各 AI 服務的「即時狀態」：不靠輪詢查詢（各家一般金鑰本來就沒有餘額 API），
    /// 而是每一次真實呼叫的結果當下就更新——成功就綠燈，失敗就依錯誤內容標成
    /// 餘額不足／金鑰無效／限流／斷網，使用者一眼看到問題出在哪一家。
    ///
    /// 搭配 SpendLedger 的分服務累計（今日／本月），就能在沒有官方餘額 API 的情況下
    /// 做到「花了多少、現在還能不能用」的即時監控。
    /// </summary>
    public static class ProviderStatusMonitor
    {
        public sealed class Snapshot
        {
            public string ProviderId { get; init; } = "";
            public ApiHealthChecker.HealthState State { get; init; } = ApiHealthChecker.HealthState.Unknown;
            public string Message { get; init; } = "";
            public string Action { get; init; } = "";
            public DateTime UpdatedAt { get; init; } = DateTime.Now;
            /// <summary>狀態是來自真實呼叫（true）還是手動檢查（false）。</summary>
            public bool FromLiveCall { get; init; }
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<string, Snapshot> _states = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>狀態有變動時觸發（UI 用來即時刷新；不保證在 UI 執行緒上）。</summary>
        public static event Action<Snapshot>? Changed;

        /// <summary>一次成功的呼叫：該服務目前可用。</summary>
        public static void ReportSuccess(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return;

            Set(new Snapshot
            {
                ProviderId = providerId!,
                State = ApiHealthChecker.HealthState.Ok,
                Message = "最近一次呼叫成功",
                FromLiveCall = true
            });
        }

        /// <summary>一次失敗的呼叫：把原始錯誤翻成白話狀態（餘額不足／金鑰無效／斷網…）。</summary>
        public static void ReportFailure(string? providerId, string? rawError)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return;

            var explained = FailureExplainer.Explain(rawError);
            var state = explained?.Title switch
            {
                "AI 服務的餘額用完了" => ApiHealthChecker.HealthState.NoCredit,
                "金鑰無效或沒有權限" => ApiHealthChecker.HealthState.InvalidKey,
                "這把金鑰沒有這項功能的權限" => ApiHealthChecker.HealthState.InvalidKey,
                "呼叫太頻繁或額度已達上限" => ApiHealthChecker.HealthState.RateLimited,
                "連不上網路或服務暫時無法連線" => ApiHealthChecker.HealthState.Offline,
                _ => ApiHealthChecker.HealthState.Unknown
            };

            Set(new Snapshot
            {
                ProviderId = providerId!,
                State = state,
                Message = explained?.Title ?? FailureExplainer.Describe(rawError, 80),
                Action = explained?.Action ?? "",
                FromLiveCall = true
            });
        }

        /// <summary>依模型 ID 回報（服務層只知道自己用什麼模型）。</summary>
        public static void ReportSuccessForModel(string? modelId)
            => ReportSuccess(ApiHealthChecker.ProviderIdForModel(modelId));

        public static void ReportFailureForModel(string? modelId, string? rawError)
            => ReportFailure(ApiHealthChecker.ProviderIdForModel(modelId), rawError);

        /// <summary>手動檢查的結果也寫進來，UI 顯示統一。</summary>
        public static void ReportCheck(ApiHealthChecker.HealthResult result)
        {
            if (result?.Provider == null || string.IsNullOrWhiteSpace(result.Provider.Id))
                return;

            Set(new Snapshot
            {
                ProviderId = result.Provider.Id,
                State = result.State,
                Message = result.Message,
                Action = result.Action,
                FromLiveCall = false
            });
        }

        public static Snapshot? Get(string providerId)
        {
            lock (_lock)
                return _states.TryGetValue(providerId ?? "", out var s) ? s : null;
        }

        public static IReadOnlyList<Snapshot> All()
        {
            lock (_lock)
                return _states.Values.OrderBy(x => x.ProviderId, StringComparer.Ordinal).ToList();
        }

        private static void Set(Snapshot snapshot)
        {
            lock (_lock)
                _states[snapshot.ProviderId] = snapshot;

            try { Changed?.Invoke(snapshot); }
            catch (Exception ex) { AppLog.Warn("ProviderStatus", "狀態變更通知失敗", ex); }
        }
    }
}
