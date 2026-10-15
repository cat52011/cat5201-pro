using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Cat5201
{
    // MainWindow 部分類別 — API 即時狀態面板
    //
    // 各家 AI 服務的一般金鑰都沒有「查餘額」的 API，所以這裡改成做得到的即時監控：
    //   ① 每次真實呼叫的結果當下更新狀態（ProviderStatusMonitor）——餘額不足、金鑰失效、斷網馬上看得到；
    //   ② 本機帳本分服務累計（今日／本月）；
    //   ③ 使用者可填「我儲值了多少」，用花費推估剩餘。
    public partial class MainWindow
    {
        public sealed class ApiStatusRow : INotifyPropertyChanged
        {
            public string ProviderId { get; init; } = "";
            public string DisplayName { get; init; } = "";

            private string _currency = "USD";

            /// <summary>
            /// 這個服務向使用者收費的幣別。可以自己改——帳單國別不同的人，
            /// 預設值不見得對（寫死「Google ＝ AUD」是某一個帳號的事實，不是通則）。
            /// </summary>
            public string Currency
            {
                get => _currency;
                set
                {
                    if (!Set(ref _currency, value)) return;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Symbol)));
                }
            }

            /// <summary>這個服務的計價幣別符號。</summary>
            public string Symbol => ApiHealthChecker.CurrencySymbol(_currency);

            public IReadOnlyList<string> CurrencyOptions => ProviderBalance.SupportedCurrencies;

            public string Note { get; init; } = "";
            public string BillingUrl { get; init; } = "";

            private string _state = "尚未檢查";
            private string _detail = "";
            private string _dot = "#C9CDD4";
            private string _spend = "";
            private string _topUp = "";
            private string _balanceText = "未設定";
            private string _problem = "";
            private string _problemVisible = "Collapsed";
            private string _meta = "";

            public string State { get => _state; set => Set(ref _state, value); }
            public string Detail { get => _detail; set => Set(ref _detail, value); }
            public string Dot { get => _dot; set => Set(ref _dot, value); }
            public string Spend { get => _spend; set => Set(ref _spend, value); }
            /// <summary>輸入框：要把餘額改成多少（送出後清空）。</summary>
            public string TopUp { get => _topUp; set => Set(ref _topUp, value); }
            /// <summary>目前餘額（本機記帳，每一筆花費即時扣）。</summary>
            public string BalanceText { get => _balanceText; set => Set(ref _balanceText, value); }
            /// <summary>
            /// 餘額底下那一行小字：上次對帳是什麼時候、之後本機記了多少。
            ///
            /// 這一行存在的理由：沒有任何一家提供查餘額的 API，圖片與影片又是按張 / 按秒
            /// 的估算價，官方的工具費與沙盒費更是完全不回報。所以上面那個數字必然會漂。
            /// 與其讓它看起來像查來的真實餘額，不如老實說它是「從某一天起本機自己扣的」。
            /// </summary>
            public string Meta { get => _meta; set => Set(ref _meta, value); }

            /// <summary>只有出問題（餘額用完、金鑰無效、連不上）才顯示的一行。</summary>
            public string Problem { get => _problem; set => Set(ref _problem, value); }
            public string ProblemVisible { get => _problemVisible; set => Set(ref _problemVisible, value); }

            public event PropertyChangedEventHandler? PropertyChanged;

            private bool Set(ref string field, string value, [CallerMemberName] string? name = null)
            {
                if (field == value) return false;
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                return true;
            }
        }

        public ObservableCollection<ApiStatusRow> ApiStatusRows { get; } = new();

        private bool _apiStatusWired;

        private void EnsureApiStatusRows()
        {
            if (ApiStatusRows.Count == 0)
            {
                foreach (var p in ApiHealthChecker.Providers)
                {
                    ApiStatusRows.Add(new ApiStatusRow
                    {
                        ProviderId = p.Id,
                        DisplayName = p.DisplayName,
                        Note = p.Note,
                        BillingUrl = p.BillingUrl,
                        Currency = ProviderBalance.CurrencyOf(p.Id),
                    });
                }

                if (ApiStatusList != null)
                    ApiStatusList.ItemsSource = ApiStatusRows;
            }

            if (!_apiStatusWired)
            {
                _apiStatusWired = true;
                // 即時：任何一次真實呼叫失敗/成功都會推過來，不用等使用者按檢查。
                ProviderStatusMonitor.Changed += snapshot =>
                    Dispatcher.BeginInvoke(new Action(() => ApplySnapshot(snapshot)));
            }

            foreach (var snapshot in ProviderStatusMonitor.All())
                ApplySnapshot(snapshot);

            // 舊版「儲值金額 + 起算日」一次性轉進餘額簿，使用者不必重填。
            foreach (var kv in _providerTopUpUsd)
                ProviderBalance.SeedFromLegacyTopUp(
                    kv.Key, kv.Value,
                    _providerTopUpSince.TryGetValue(kv.Key, out var since) ? since : DateTime.Today);

            RefreshSpendColumns();
        }

        private void ApplySnapshot(ProviderStatusMonitor.Snapshot snapshot)
        {
            var row = ApiStatusRows.FirstOrDefault(r => r.ProviderId == snapshot.ProviderId);
            if (row == null)
                return;

            row.State = StateLabel(snapshot.State);
            row.Dot = StateColor(snapshot.State);
            row.Detail = string.IsNullOrWhiteSpace(snapshot.Action)
                ? $"{snapshot.Message}（{snapshot.UpdatedAt:HH:mm}{(snapshot.FromLiveCall ? " 實際呼叫" : " 檢查")}）"
                : $"{snapshot.Message}　→　{snapshot.Action}";

            // 這一頁只講餘額；狀態只有在「真的不能用」時才佔一行版面。
            bool problem = snapshot.State is ApiHealthChecker.HealthState.NoCredit
                or ApiHealthChecker.HealthState.InvalidKey
                or ApiHealthChecker.HealthState.Offline
                or ApiHealthChecker.HealthState.RateLimited;
            row.Problem = problem ? $"{StateLabel(snapshot.State)}：{snapshot.Message}" : "";
            row.ProblemVisible = problem ? "Visible" : "Collapsed";

            RefreshSpendColumns();
        }

        private static string StateLabel(ApiHealthChecker.HealthState state) => state switch
        {
            ApiHealthChecker.HealthState.Ok => "可用",
            ApiHealthChecker.HealthState.NoKey => "未設定金鑰",
            ApiHealthChecker.HealthState.InvalidKey => "金鑰無效",
            ApiHealthChecker.HealthState.NoCredit => "餘額不足",
            ApiHealthChecker.HealthState.RateLimited => "已達上限",
            ApiHealthChecker.HealthState.Offline => "連不上",
            _ => "狀態未知"
        };

        private static string StateColor(ApiHealthChecker.HealthState state) => state switch
        {
            ApiHealthChecker.HealthState.Ok => "#2FA36B",
            ApiHealthChecker.HealthState.NoKey => "#C9CDD4",
            ApiHealthChecker.HealthState.NoCredit => "#E2483D",
            ApiHealthChecker.HealthState.InvalidKey => "#E2483D",
            ApiHealthChecker.HealthState.Offline => "#E2483D",
            ApiHealthChecker.HealthState.RateLimited => "#E8A33D",
            _ => "#C9CDD4"
        };

        /// <summary>
        /// 使用者在下拉選單裡換了計價幣別：餘額一起換算過去，數字的意思才不會變。
        /// </summary>
        private void ProviderCurrency_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ApiStatusRow row)
                return;

            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string picked)
                return;

            if (string.Equals(picked, ProviderBalance.CurrencyOf(row.ProviderId), StringComparison.OrdinalIgnoreCase))
                return;

            ProviderBalance.SetCurrency(row.ProviderId, picked);
            row.Currency = picked;
            RefreshSpendColumns();
            RefreshApiBalancePanel();
        }

        /// <summary>這一頁只顯示「目前餘額」——花費明細在決策窗的成本明細裡，不在這裡重複一份。</summary>
        private void RefreshSpendColumns()
        {
            foreach (var row in ApiStatusRows)
            {
                row.Currency = ProviderBalance.CurrencyOf(row.ProviderId);

                if (!ProviderBalance.Has(row.ProviderId))
                {
                    row.BalanceText = "未設定";
                    row.Meta = "填入官網目前顯示的餘額，之後每一筆花費就會自動扣。";
                }
                else
                {
                    double left = ProviderBalance.Remaining(row.ProviderId);
                    // 扣成負數代表本機記到的花費比使用者填的餘額還多。數字顯示 0，
                    // 但要說出超了多少——這正是「該去官網對一次帳」的訊號。
                    row.BalanceText = $"{row.Symbol}{Math.Max(0, left):0.00}";
                    row.Meta = BuildBalanceMeta(row, left);
                }

                row.Spend = $"今日 {ModelCostEstimator.FormatTwd(SpendLedger.TodayUsdFor(row.ProviderId))}";
            }
        }

        /// <summary>餘額底下那行小字：這個數字是從哪一天起本機自己扣出來的。</summary>
        private static string BuildBalanceMeta(ApiStatusRow row, double left)
        {
            var parts = new List<string>();

            var reconciled = ProviderBalance.LastReconciledUtc(row.ProviderId);
            parts.Add(reconciled == null
                ? "本機記帳"
                : $"{reconciled.Value.ToLocalTime():M/d HH:mm} 對帳");

            double since = ProviderBalance.DeductedSinceReconcile(row.ProviderId);
            if (since > 0)
                parts.Add($"之後扣了 {row.Symbol}{since:0.00}");

            if (left < -0.005)
                parts.Add($"已超出 {row.Symbol}{-left:0.00}，建議重新對帳");

            return string.Join("・", parts);
        }

        // 「重新檢查／深入檢查」兩顆鈕已移除（2026-10-15）：
        // 狀態本來就由 ProviderStatusMonitor 在每一次真實呼叫後即時推上來，
        // 手動檢查只是多花一次錢做同一件事。

        private void OpenBillingPage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { AppLog.Warn("ApiStatus", "開啟帳單頁失敗", ex); }
            }
        }

        // 使用者填的儲值金額（美元）與起算日：存個人化偏好，用來估算剩餘。
        private Dictionary<string, double> _providerTopUpUsd = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DateTime> _providerTopUpSince = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 送出＝把這個服務的餘額「改成」輸入的數字（充值後照官網顯示的餘額填即可），輸入框隨即清空。
        /// 之後每一筆花費會自動扣掉，所以平常不需要再動它。
        /// </summary>
        private void ProviderTopUpSubmit_Click(object sender, RoutedEventArgs e)
            => SubmitProviderTopUp(sender as FrameworkElement);

        private void ProviderTopUp_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            SubmitProviderTopUp(sender as FrameworkElement);
        }

        private void SubmitProviderTopUp(FrameworkElement? source)
        {
            if (source?.DataContext is not ApiStatusRow row)
                return;

            string raw = (row.TopUp ?? "").Trim().TrimStart('$', 'U', 'S', 'u', 's', ' ');

            if (raw.Length == 0)
            {
                ProviderBalance.Forget(row.ProviderId);   // 清空送出＝不再追蹤這個服務的餘額
            }
            else if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double amount) && amount >= 0)
            {
                ProviderBalance.SetBalance(row.ProviderId, amount);
            }
            else
            {
                MenuConfirmDialog.ShowMessage(this, "目前餘額",
                    $"請輸入目前的餘額（{row.Symbol}），例如 20。\n清空後送出＝不再追蹤這個服務。", this);
                return;
            }

            row.TopUp = "";                 // 送出即清空，畫面不常駐輸入值
            RefreshSpendColumns();
            RefreshApiBalancePanel();
        }

        internal Dictionary<string, string> BuildProviderTopUpStorage()
            => _providerTopUpUsd.ToDictionary(
                kv => kv.Key,
                kv => $"{kv.Value.ToString("0.####", CultureInfo.InvariantCulture)}|" +
                      (_providerTopUpSince.TryGetValue(kv.Key, out var d) ? d : DateTime.Today).ToString("yyyy-MM-dd"));

        internal void LoadProviderTopUpStorage(Dictionary<string, string>? stored)
        {
            _providerTopUpUsd = new(StringComparer.OrdinalIgnoreCase);
            _providerTopUpSince = new(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in stored ?? new Dictionary<string, string>())
            {
                var parts = (kv.Value ?? "").Split('|');
                if (parts.Length >= 1 &&
                    double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double amount) &&
                    amount > 0)
                {
                    _providerTopUpUsd[kv.Key] = amount;
                    _providerTopUpSince[kv.Key] = parts.Length >= 2 && DateTime.TryParse(parts[1], out var d) ? d : DateTime.Today;
                }
            }
        }
    }
}
