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
        /// <summary>
        /// 一個服務的餘額。Currency 是使用者自己選的計價幣別（空＝沿用該服務的預設）。
        ///
        /// 幣別必須跟餘額存在一起：之前寫死「Google ＝ AUD」，那是某一個帳號的事實，
        /// 不是所有人的——帳單國別不同的人會看到自己從來沒有過的幣別符號。
        /// </summary>
        /// <param name="Remaining">
        /// 目前剩餘。可以是負的——本機記到的花費超過使用者填的餘額時，
        /// 把它夾在 0 會讓超出的部分永遠消失，下一次充值的數字就跟著偏高。
        /// 要顯示成 0 是畫面的事，帳要留真的。
        /// </param>
        /// <param name="ReconciledUtc">使用者上一次照官網餘額對帳的時間（空＝從來沒對過）。</param>
        /// <param name="DeductedSinceReconcile">自上次對帳以來本機扣掉的總額（該服務的計價幣別）。</param>
        private sealed record Entry(
            double Remaining,
            double TotalToppedUp,
            string UpdatedUtc,
            string Currency = "",
            string ReconciledUtc = "",
            double DeductedSinceReconcile = 0);

        /// <summary>
        /// 餘額簿的格式版本。
        /// 2：Remaining 允許負值，並記錄上次對帳時間與其後的累計扣款。
        /// </summary>
        private const int CurrentSchema = 2;

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
                    // 信封讀得懂舊的裸字典（schema 0），所以升級不會讓既有餘額歸零。
                    _entries = File.Exists(_path)
                        ? SchemaEnvelope.Read<Dictionary<string, Entry>>(File.ReadAllText(_path)).Data
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
        {
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                lock (_lock)
                {
                    if (_entries.TryGetValue(providerId!, out var entry) &&
                        !string.IsNullOrWhiteSpace(entry.Currency))
                    {
                        return entry.Currency.ToUpperInvariant();
                    }
                }
            }

            return DefaultCurrencyOf(providerId);
        }

        /// <summary>
        /// 出廠預設幣別：使用者還沒自己選過時用哪一種。
        ///
        /// 住在這裡而不是 ProviderInfo 上，是為了讓「讀得到出廠預設」的地方只有這一個檔案。
        /// 畫面一律走 CurrencyOf()，要繞過使用者的選擇得先改這裡——那會很顯眼。
        /// Google Cloud 依帳單國別出帳（作者的澳洲帳號是 AUD），其餘三家都是美元。
        /// </summary>
        private static readonly Dictionary<string, string> DefaultCurrencies =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["google"] = "AUD",
            };

        /// <summary>這個服務出廠預設的計價幣別（使用者沒有自己選過時用）。</summary>
        public static string DefaultCurrencyOf(string? providerId)
            => !string.IsNullOrWhiteSpace(providerId) &&
               DefaultCurrencies.TryGetValue(providerId!, out var currency)
                ? currency
                : "USD";

        /// <summary>
        /// 使用者可以選的幣別。直接取自匯率表——選單裡出現一個換算不出來的幣別，
        /// 等於請使用者填一個永遠不會被正確扣款的數字。
        /// </summary>
        public static IReadOnlyList<string> SupportedCurrencies => ModelCostEstimator.SupportedCurrencies;

        /// <summary>
        /// 改變一個服務的計價幣別，並把已存的餘額換算過去。
        ///
        /// 不換算的話，A$30 會直接變成「US$30」——數字沒動，意思卻完全不同，
        /// 使用者會以為自己突然多了四成額度。
        /// </summary>
        public static void SetCurrency(string? providerId, string? currency)
        {
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(currency))
                return;

            string next = currency!.ToUpperInvariant();
            string previous = CurrencyOf(providerId);
            if (string.Equals(previous, next, StringComparison.Ordinal))
                return;

            lock (_lock)
            {
                if (!_entries.TryGetValue(providerId!, out var entry))
                {
                    // 還沒設過餘額：只記下選擇，等使用者填數字時就會用新幣別。
                    _entries[providerId!] = new Entry(0, 0, DateTime.UtcNow.ToString("o"), next);
                }
                else
                {
                    double rate = ModelCostEstimator.ConvertUsd(1, next) /
                                  ModelCostEstimator.ConvertUsd(1, previous);

                    _entries[providerId!] = entry with
                    {
                        Remaining = entry.Remaining * rate,
                        TotalToppedUp = entry.TotalToppedUp * rate,
                        Currency = next,
                        UpdatedUtc = DateTime.UtcNow.ToString("o")
                    };
                }

                Persist();
            }

            AppLog.Info("Balance", $"{providerId} 計價幣別改為 {next}（餘額已換算）");
            Changed?.Invoke();
        }

        /// <summary>這個服務有沒有設定過餘額（沒有就不顯示剩餘，也不會被扣款）。</summary>
        public static bool Has(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return false;
            lock (_lock)
            {
                // 只選了幣別、還沒填過餘額的不算「已設定」——不然面板會冒出一列「剩餘 0」。
                // 看 TotalToppedUp 而不是 Remaining：餘額花到 0 的服務仍然要顯示（而且要顯示成紅燈），
                // 用 Remaining 判斷的話，最該提醒使用者的那一刻那一列反而會消失。
                return _entries.TryGetValue(providerId, out var e) && e.TotalToppedUp > 0;
            }
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

        /// <summary>
        /// 把餘額直接設成某個數字（使用者照官網顯示的餘額填）＝一次「對帳」。
        ///
        /// 這同時是唯一能知道本機記帳準不準的時機：拿上一次對帳後本機扣掉的金額，
        /// 跟使用者這次填的真實餘額比一比，就知道這段期間少記（或多記）了多少。
        /// 各家都沒有查餘額的 API，圖片與影片又是按張 / 按秒的估算價，誤差一定存在；
        /// 能做的是把誤差記下來、顯示出來，而不是讓它無聲地累積成一個假的餘額。
        /// </summary>
        public static void SetBalance(string providerId, double usd)
        {
            if (string.IsNullOrWhiteSpace(providerId) || usd < 0 || double.IsNaN(usd) || double.IsInfinity(usd))
                return;

            string symbol = ApiHealthChecker.CurrencySymbol(CurrencyOf(providerId));
            double drift = 0;
            bool hadReconciled;

            lock (_lock)
            {
                _entries.TryGetValue(providerId, out var current);
                double total = Math.Max(current?.TotalToppedUp ?? 0, usd);

                // 誤差＝本機以為還剩多少 − 實際還剩多少。正數＝少記了（花得比記到的多）。
                hadReconciled = !string.IsNullOrWhiteSpace(current?.ReconciledUtc);
                if (hadReconciled) drift = current!.Remaining - usd;

                _entries[providerId] = new Entry(
                    usd, total, DateTime.UtcNow.ToString("o"), current?.Currency ?? "",
                    ReconciledUtc: DateTime.UtcNow.ToString("o"),
                    DeductedSinceReconcile: 0);
                Persist();
            }

            AppLog.Info("Balance", $"{providerId} 餘額設定為 {symbol}{usd:0.00}");

            if (hadReconciled && Math.Abs(drift) >= 0.01)
            {
                string direction = drift > 0 ? "本機少記了這些花費" : "本機多記了這些花費";
                AppLog.Warn("Balance",
                    $"{providerId} 對帳差額 {symbol}{drift:0.00}（{direction}）——" +
                    "圖片 / 影片是估算價，工具與沙盒費官方不回報，請以官網為準。");
            }

            Changed?.Invoke();
        }

        /// <summary>上一次照官網對帳的時間（null＝從來沒對過）。</summary>
        public static DateTime? LastReconciledUtc(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return null;
            lock (_lock)
            {
                if (!_entries.TryGetValue(providerId, out var e) || string.IsNullOrWhiteSpace(e.ReconciledUtc))
                    return null;
                return DateTime.TryParse(e.ReconciledUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
            }
        }

        /// <summary>自上次對帳以來本機扣掉的金額（該服務的計價幣別）。</summary>
        public static double DeductedSinceReconcile(string? providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return 0;
            lock (_lock) return _entries.TryGetValue(providerId, out var e) ? e.DeductedSinceReconcile : 0;
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
                _entries[providerId] = new Entry(remaining, total, DateTime.UtcNow.ToString("o"), current?.Currency ?? "");
                Persist();
            }

            string sym = ApiHealthChecker.CurrencySymbol(CurrencyOf(providerId));
            AppLog.Info("Balance", $"{providerId} 充值 {sym}{usd:0.##} → 剩餘 {sym}{Remaining(providerId):0.00}");
            Changed?.Invoke();
        }

        /// <summary>
        /// 扣掉一筆花費（usd 為負＝沖銷，加回去）。沒設定過餘額的服務不建立紀錄——
        /// 不然畫面上會冒出一個「剩餘 -3.2 美元」的服務，那是沒有意義的數字。
        ///
        /// 扣到負數是允許的：使用者填的餘額本來就可能偏低（或估價偏高），
        /// 夾在 0 等於把超出的部分丟掉，下一次充值時那段差額就再也找不回來。
        /// 畫面要不要顯示成 0 是畫面自己的事。
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
                    Remaining = current.Remaining - amount,
                    DeductedSinceReconcile = current.DeductedSinceReconcile + amount,
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
                if (!_entries.TryGetValue(providerId, out var current)) return;

                // 幣別是使用者的選擇，不隨「不再追蹤餘額」一起忘掉。
                if (string.IsNullOrWhiteSpace(current.Currency)) _entries.Remove(providerId);
                else _entries[providerId] = new Entry(0, 0, DateTime.UtcNow.ToString("o"), current.Currency);

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
                _entries[providerId] = new Entry(remaining, total, DateTime.UtcNow.ToString("o"), currency);
                Persist();
            }
            AppLog.Info("Balance", $"{providerId} 由舊版儲值設定轉入餘額簿：剩餘 {ApiHealthChecker.CurrencySymbol(currency)}{Remaining(providerId):0.00}");
        }

        private static void Persist()
        {
            try
            {
                if (_path == null) return;
                AtomicFile.WriteAllText(_path, SchemaEnvelope.Write(_entries, CurrentSchema));
            }
            catch (Exception ex) { AppLog.Warn("Balance", "餘額簿寫入失敗", ex); }
        }
    }
}
