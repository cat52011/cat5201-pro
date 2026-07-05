using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace test
{
    /// <summary>
    /// 花錢安全（商品級核心）：全域花費帳本。這個產品會花使用者真金白銀，
    /// 「今天花了多少、有沒有超過我的上限」必須是一等公民，不是散在各節點的數字。
    ///
    /// 記錄點：LLM 文字成本（MainWindow.AddExecutionLog，依實際 tokens×價目表）＋
    /// 媒體成本（NodeControl.AddMediaCostUsd，圖片/影片按張/秒計價）。
    /// 儲存：per-day USD 總額，_config/_spend.json，原子寫入；絕不拋例外拖垮主流程。
    /// </summary>
    public static class SpendLedger
    {
        private const double UsdToTwd = 32.0; // 與 ModelCostEstimator 同一近似匯率

        private static readonly object _lock = new();
        private static string? _path;
        private static Dictionary<string, double> _usdByDay = new();

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
                    string? json = AtomicFile.ReadAllTextWithFallback(_path);
                    if (!string.IsNullOrWhiteSpace(json))
                        _usdByDay = JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new();
                }
                catch { _usdByDay = new(); }
            }
        }

        /// <summary>累加一筆花費（USD）。kind 僅供日誌（llm / image / video…）。</summary>
        public static void Add(double usd, string kind)
        {
            if (usd <= 0 || double.IsNaN(usd) || double.IsInfinity(usd))
                return;

            lock (_lock)
            {
                _usdByDay.TryGetValue(TodayKey, out double cur);
                _usdByDay[TodayKey] = cur + usd;
                Persist();
            }
            AppLog.Info("Spend", $"+US${usd:0.####}（{kind}）→ 今日累計 US${TodayUsd:0.####}");
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
        public static bool IsOverDailyBudget(int budgetTwd)
            => budgetTwd > 0 && TodayTwd >= budgetTwd;

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

                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_usdByDay, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AppLog.Warn("Spend", "帳本寫入失敗", ex); }
        }
    }
}
