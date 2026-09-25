using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cat5201
{
    /// <summary>
    /// 各 AI 服務的「剩餘額度」餘額簿。
    ///
    /// 各家的一般金鑰都沒有查餘額的 API，所以這裡自己記帳：使用者充值時把金額加進來，
    /// 之後每一筆真的花錢的呼叫（UsageMeter 的每一筆帳目）就扣掉，沖銷則加回去。
    ///
    /// 為什麼不用「儲值金額 − 帳本累計花費」去算：花費帳本只留 90 天，過了就被裁掉，
    /// 舊花費一消失剩餘就會自己跳高，等於報一個假餘額給使用者。所以餘額要自己存成一個獨立、
    /// 會被扣減的數字，與帳本的保留期無關。
    /// </summary>
    public static class ProviderBalance
    {
        private sealed record Entry(double Remaining, double TotalToppedUp, string UpdatedUtc);

        private static readonly object _lock = new();
        private static string? _path;
        private static Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>餘額變動（充值或扣款）。UI 用來即時更新；訂閱端自行切回 UI 執行緒。</summary>
        public static event Action? Changed;

        public static void Initialize(string configDir)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(configDir);
                    _path = Path.Combine(configDir, "_provider_balance.json");
                    _entries = File.Exists(_path)
                        ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path))
                          ?? new(StringComparer.OrdinalIgnoreCase)
                        : new(StringComparer.OrdinalIgnoreCase);

                    // JSON 還原後比較器會變成預設的，重建成不分大小寫。
                    _entries = new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Balance", "餘額簿讀取失敗，從空的開始", ex);
                    _entries = new(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// 這個服務向使用者收費的幣別。餘額就以這個幣別存放（使用者照官網看到的數字填），
        /// 扣款時才把美元成本換算過去——不然 A$ 的餘額被 US$ 的花費扣，數字永遠對不上。
        /// </summary>
        public static string CurrencyOf(string? providerId)
            => ApiHealthChecker.Providers
                .FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))
                ?.Currency ?? "USD";

        /// <summary>這個服務有沒有設定過餘額（沒有就不顯示剩餘，也不會被扣款）。</summary>
        public static bool Has(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return false;
            lock (_lock) return _entries.ContainsKey(providerId);
        }

        public static double Remaining(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return 0;
            lock (_lock) return _entries.TryGetValue(providerId, out var e) ? e.Remaining : 0;
        }

        /// <summary>累計充值金額（顯示「這個服務總共儲值過多少」用）。</summary>
        public static double TotalToppedUp(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return 0;
            lock (_lock) return _entries.TryGetValue(providerId, out var e) ? e.TotalToppedUp : 0;
        }

        public static IReadOnlyCollection<string> Providers()
        {
            lock (_lock) return _entries.Keys.ToList();
        }

        /// <summary>把餘額直接設成某個數字（使用者照官網顯示的餘額填）。之後的花費照常扣。</summary>
        public static void SetBalance(string providerId, double usd)
        {
            if (string.IsNullOrWhiteSpace(providerId) || usd < 0 || double.IsNaN(usd) || double.IsInfinity(usd))
                return;

            lock (_lock)
            {
                _entries.TryGetValue(providerId, out var current);
                double total = Math.Max(current?.TotalToppedUp ?? 0, usd);
                _entries[providerId] = new Entry(usd, total, DateTime.UtcNow.ToString("o"));
                Persist();
            }

            AppLog.Info("Balance", $"{providerId} 餘額設定為 US${usd:0.00}");
            Changed?.Invoke();
        }

        /// <summary>使用者充值：把金額加到剩餘。負數＝更正上一次填錯的金額。</summary>
        public static void AddTopUp(string providerId, double usd)
        {
            if (string.IsNullOrWhiteSpace(providerId) || usd == 0 || double.IsNaN(usd) || double.IsInfinity(usd))
                return;

            lock (_lock)
            {
                _entries.TryGetValue(providerId, out var current);
                double remaining = Math.Max(0, (current?.Remaining ?? 0) + usd);
                double total = Math.Max(0, (current?.TotalToppedUp ?? 0) + usd);
                _entries[providerId] = new Entry(remaining, total, DateTime.UtcNow.ToString("o"));
                Persist();
            }

            AppLog.Info("Balance", $"{providerId} 充值 US${usd:0.##} → 剩餘 US${Remaining(providerId):0.00}");
            Changed?.Invoke();
        }

        /// <summary>
        /// 扣掉一筆花費（usd 為負＝沖銷，加回去）。沒設定過餘額的服務不建立紀錄——
        /// 不然畫面上會冒出一個「剩餘 -3.2 美元」的服務，那是沒有意義的數字。
        /// </summary>
        public static void Deduct(string? providerId, double usd)
        {
            if (string.IsNullOrWhiteSpace(providerId) || usd == 0 || double.IsNaN(usd) || double.IsInfinity(usd))
                return;

            // 成本一律以美元計算，餘額存的是該服務的計價幣別，所以扣款前先換算。
            double amount = ModelCostEstimator.ConvertUsd(usd, CurrencyOf(providerId));

            lock (_lock)
            {
                if (!_entries.TryGetValue(providerId, out var current))
                    return;

                _entries[providerId] = current with
                {
                    Remaining = Math.Max(0, current.Remaining - amount),
                    UpdatedUtc = DateTime.UtcNow.ToString("o")
                };
                Persist();
            }

            Changed?.Invoke();
        }

        /// <summary>移除這個服務的餘額追蹤（使用者清空金額時）。</summary>
        public static void Forget(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return;
            lock (_lock)
            {
                if (!_entries.Remove(providerId)) return;
                Persist();
            }
            Changed?.Invoke();
        }

        /// <summary>舊版偏好（儲值金額 + 起算日）一次性搬進餘額簿，使用者不用重填。</summary>
        public static void SeedFromLegacyTopUp(string providerId, double topUpUsd, DateTime since)
        {
            if (string.IsNullOrWhiteSpace(providerId) || topUpUsd <= 0 || Has(providerId))
                return;

            // 舊設定是以美元填的，換算成該服務的計價幣別後再存。
            string currency = CurrencyOf(providerId);
            double remaining = ModelCostEstimator.ConvertUsd(
                Math.Max(0, topUpUsd - SpendLedger.UsdForSince(providerId, since)), currency);
            double total = ModelCostEstimator.ConvertUsd(topUpUsd, currency);

            lock (_lock)
            {
                _entries[providerId] = new Entry(remaining, total, DateTime.UtcNow.ToString("o"));
                Persist();
            }
            AppLog.Info("Balance", $"{providerId} 由舊版儲值設定轉入餘額簿：剩餘 {ApiHealthChecker.CurrencySymbol(currency)}{Remaining(providerId):0.00}");
        }

        private static void Persist()
        {
            try
            {
                if (_path == null) return;
                AtomicFile.WriteAllText(_path,
                    JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLog.Warn("Balance", "餘額簿寫入失敗", ex); }
        }
    }
}
