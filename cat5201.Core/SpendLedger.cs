using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cat5201
{
    /// <summary>
    /// 花錢安全（商品級核心）：全域花費帳本。這個產品會花使用者真金白銀，
    /// 「今天花了多少、有沒有超過我的上限」必須是一等公民，不是散在各節點的數字。
    ///
    /// 記錄點唯一＝UsageMeter：每個 AI 服務（LLM / 圖片 / 影片 / 搜尋）在呼叫完成當下記一筆，
    /// 輔助呼叫（意圖判斷、自動命名…）也逃不掉；影片在送出時先記、失敗時沖銷（官方：只有成功生成才收費）。
    /// 儲存：per-day USD 總額，_config/_spend.json，原子寫入；絕不拋例外拖垮主流程。
    /// </summary>
    public static class SpendLedger
    {
        /// <summary>花費帳本的格式版本。改變每日金額的意義時要加一號。</summary>
        private const int CurrentSchema = 1;

        // 匯率單一真相＝ModelCostEstimator.UsdToTwd（兩處各養一個匯率曾造成顯示不一致，不再重演）。
        private const double UsdToTwd = ModelCostEstimator.UsdToTwd;

        private static readonly object _lock = new();
        private static string? _path;
        private static Dictionary<string, double> _usdByDay = new();
        // 分服務累計：day -> provider -> USD。用來即時顯示「哪一家花了多少」（各家都沒有餘額 API）。
        private static Dictionary<string, Dictionary<string, double>> _usdByDayProvider = new();

        private static string TodayKey => DateTime.Now.ToString("yyyy-MM-dd");

        /// <summary>啟動時呼叫一次（在任何執行之前）。載入既有帳本。</summary>
        public static void Initialize(string configDir)
        {
            lock (_lock)
            {
                _usdByDay = new(); // 先清空：沒有帳本檔＝從零開始，不殘留前一個目錄的狀態
                try
                {
                    _path = Path.Combine(configDir, "_spend.json");
                    _usdByDayProvider = new();
                    string? json = AtomicFile.ReadAllTextWithFallback(_path);
                    if (!string.IsNullOrWhiteSpace(json))
                        LoadFromJsonLocked(json!);
                }
                catch { _usdByDay = new(); }
            }
        }

        /// <summary>帳本變動（新增或沖銷）。UI 用來即時更新餘額顯示；訂閱端自行切回 UI 執行緒。</summary>
        public static event Action? Changed;

        /// <summary>累加一筆花費（USD）。kind 僅供日誌（llm / image / video…）。</summary>
        public static void Add(double usd, string kind) => Add(usd, kind, providerId: "");

        /// <summary>累加一筆花費並歸戶到某個服務（anthropic / openai / google / perplexity）。</summary>
        public static void Add(double usd, string kind, string providerId)
        {
            if (usd <= 0 || double.IsNaN(usd) || double.IsInfinity(usd))
                return;

            lock (_lock)
            {
                _usdByDay.TryGetValue(TodayKey, out double cur);
                _usdByDay[TodayKey] = cur + usd;

                if (!string.IsNullOrWhiteSpace(providerId))
                {
                    if (!_usdByDayProvider.TryGetValue(TodayKey, out var byProvider))
                        _usdByDayProvider[TodayKey] = byProvider = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    byProvider.TryGetValue(providerId, out double p);
                    byProvider[providerId] = p + usd;
                }

                Persist();
            }
            AppLog.Info("Spend", $"+US${usd:0.####}（{kind}）→ 今日累計 US${TodayUsd:0.####}");
            Changed?.Invoke();
        }

        /// <summary>沖銷一筆先前記過、但實際不收費的花費（例：影片任務失敗）。當日總額不低於 0。</summary>
        public static void Subtract(double usd, string kind) => Subtract(usd, kind, providerId: "");

        /// <summary>
        /// 沖銷並歸戶到某個服務。沒有歸戶的話，該服務的「估計剩餘」會一直少算那一筆——
        /// 使用者看到的餘額就會比真實的低，等於用假數字嚇自己。
        /// </summary>
        public static void Subtract(double usd, string kind, string providerId)
        {
            if (usd <= 0 || double.IsNaN(usd) || double.IsInfinity(usd))
                return;

            lock (_lock)
            {
                _usdByDay.TryGetValue(TodayKey, out double cur);
                _usdByDay[TodayKey] = Math.Max(0, cur - usd);

                if (!string.IsNullOrWhiteSpace(providerId) &&
                    _usdByDayProvider.TryGetValue(TodayKey, out var byProvider) &&
                    byProvider.TryGetValue(providerId, out double p))
                {
                    byProvider[providerId] = Math.Max(0, p - usd);
                }

                Persist();
            }
            AppLog.Info("Spend", $"-US${usd:0.####}（沖銷：{kind}）→ 今日累計 US${TodayUsd:0.####}");
            Changed?.Invoke();
        }

        /// <summary>今日某個服務的累計花費（USD）。</summary>
        public static double TodayUsdFor(string providerId)
        {
            lock (_lock)
            {
                return _usdByDayProvider.TryGetValue(TodayKey, out var byProvider) &&
                       byProvider.TryGetValue(providerId ?? "", out var v) ? v : 0;
            }
        }

        /// <summary>本月某個服務的累計花費（USD）。</summary>
        public static double MonthUsdFor(string providerId)
        {
            string prefix = DateTime.Now.ToString("yyyy-MM");
            lock (_lock)
            {
                return _usdByDayProvider
                    .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .Sum(kv => kv.Value.TryGetValue(providerId ?? "", out var v) ? v : 0);
            }
        }

        /// <summary>某個服務自指定日期（含）起的累計花費（USD）——配合使用者填的儲值金額估算剩餘。</summary>
        public static double UsdForSince(string providerId, DateTime since)
        {
            string from = since.ToString("yyyy-MM-dd");
            lock (_lock)
            {
                return _usdByDayProvider
                    .Where(kv => string.CompareOrdinal(kv.Key, from) >= 0)
                    .Sum(kv => kv.Value.TryGetValue(providerId ?? "", out var v) ? v : 0);
            }
        }

        /// <summary>今日累計花費（USD）。</summary>
        public static double TodayUsd
        {
            get { lock (_lock) { return _usdByDay.TryGetValue(TodayKey, out var v) ? v : 0; } }
        }

        /// <summary>今日累計（台幣，顯示用）。</summary>
        public static double TodayTwd => TodayUsd * UsdToTwd;

        /// <summary>顯示文字，例：「今日已花 約 NT$12.4」。</summary>
        public static string TodayDisplay() => $"約 NT${TodayTwd:0.#}";

        /// <summary>
        /// 是否已達每日上限。budgetTwd &lt;= 0 = 不限制。
        /// </summary>
        /// <summary>
        /// 今日是否已達預算上限。含「執行中預留」——只看已入帳的金額，等於快到上限時
        /// 還能同時放行好幾個長任務，跑完才發現超支（預算變成啟動門檻而不是上限）。
        /// </summary>
        /// <summary>
        /// 今日預算（台幣，0＝不限制）。由個人化設定，Core 這邊需要它才能在「預留的同一刻」
        /// 判斷會不會超過上限——檢查與佔用分開做的話，預算只會是啟動門檻。
        /// </summary>
        public static int DailyBudgetTwd { get; set; }

        /// <summary>匯率（給需要自行換算的呼叫端）。</summary>
        public static double UsdToTwdRate => UsdToTwd;

        public static bool IsOverDailyBudget(int budgetTwd)
            => budgetTwd > 0 && CommittedTwd >= budgetTwd;

        /// <summary>今日已花 + 執行中預留（台幣）。</summary>
        public static double CommittedTwd => (TodayUsd + SpendReservations.ReservedUsd) * UsdToTwd;

        /// <summary>今日已花 + 執行中預留的說明文字（給預算訊息用）。</summary>
        public static string CommittedDisplay()
        {
            double reserved = SpendReservations.ReservedUsd * UsdToTwd;
            return reserved <= 0.005
                ? $"約 NT${TodayTwd:0.#}"
                : $"約 NT${TodayTwd:0.#}（另有執行中預留 NT${reserved:0.#}）";
        }

        // 檔案格式：新版 {"days":{...},"providers":{day:{provider:usd}}}；
        // 舊版是純 {day: usd}，照樣讀得進來（分服務資料從那天起才有）。
        private static void LoadFromJsonLocked(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("days", out var daysEl))
                {
                    _usdByDay = JsonSerializer.Deserialize<Dictionary<string, double>>(daysEl.GetRawText()) ?? new();
                    if (root.TryGetProperty("providers", out var provEl))
                        _usdByDayProvider = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(provEl.GetRawText()) ?? new();
                    return;
                }

                _usdByDay = JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Spend", "帳本格式無法解析，從零開始", ex);
                _usdByDay = new();
                _usdByDayProvider = new();
            }
        }

        private static void Persist()
        {
            try
            {
                if (_path == null) return;
                // 只留最近 90 天，帳本不無限長大。
                var cutoff = DateTime.Now.AddDays(-90).ToString("yyyy-MM-dd");
                var trimmed = new Dictionary<string, double>();
                foreach (var kv in _usdByDay)
                    if (string.CompareOrdinal(kv.Key, cutoff) >= 0)
                        trimmed[kv.Key] = kv.Value;
                _usdByDay = trimmed;

                foreach (var key in _usdByDayProvider.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
                    _usdByDayProvider.Remove(key);

                // 這份本來就有自己的外層，補上 schema 讓它跟其他落地檔案一致。
                var payload = new { schema = CurrentSchema, days = _usdByDay, providers = _usdByDayProvider };
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLog.Warn("Spend", "帳本寫入失敗", ex); }
        }
    }
}
