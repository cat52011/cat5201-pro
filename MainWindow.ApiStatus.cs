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
            public string Note { get; init; } = "";
            public string BillingUrl { get; init; } = "";

            private string _state = "尚未檢查";
            private string _detail = "";
            private string _dot = "#C9CDD4";
            private string _spend = "";
            private string _topUp = "";

            public string State { get => _state; set => Set(ref _state, value); }
            public string Detail { get => _detail; set => Set(ref _detail, value); }
            public string Dot { get => _dot; set => Set(ref _dot, value); }
            public string Spend { get => _spend; set => Set(ref _spend, value); }
            /// <summary>使用者填的儲值金額（美元），空＝不估算剩餘。</summary>
            public string TopUp { get => _topUp; set => Set(ref _topUp, value); }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void Set(ref string field, string value, [CallerMemberName] string? name = null)
            {
                if (field == value) return;
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
                        TopUp = _providerTopUpUsd.TryGetValue(p.Id, out var amount) && amount > 0
                            ? amount.ToString("0.##", CultureInfo.InvariantCulture)
                            : ""
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

        /// <summary>花費欄：今日／本月／（有填儲值時）估計剩餘。</summary>
        private void RefreshSpendColumns()
        {
            foreach (var row in ApiStatusRows)
            {
                double today = SpendLedger.TodayUsdFor(row.ProviderId);
                double month = SpendLedger.MonthUsdFor(row.ProviderId);
                string text = $"今日 {ModelCostEstimator.FormatTwd(today)}　本月 {ModelCostEstimator.FormatTwd(month)}";

                if (_providerTopUpUsd.TryGetValue(row.ProviderId, out double topUp) && topUp > 0)
                {
                    var since = _providerTopUpSince.TryGetValue(row.ProviderId, out var d) ? d : DateTime.Today;
                    double used = SpendLedger.UsdForSince(row.ProviderId, since);
                    double left = Math.Max(0, topUp - used);
                    text += $"　估計剩餘 US${left:0.00}（{since:MM/dd} 起用掉 US${used:0.00}）";
                }

                row.Spend = text;
            }
        }

        private async void RefreshApiStatus_Click(object sender, RoutedEventArgs e)
            => await RunApiCheckAsync(deep: false);

        private async void DeepCheckApiStatus_Click(object sender, RoutedEventArgs e)
        {
            bool ok = MenuConfirmDialog.ShowConfirm(this, "深入檢查",
                "會對每個已設定金鑰的服務送出一個最小請求（合計約 NT$0.3），\n" +
                "這是唯一能查出「餘額不足」的方法（各家都沒有提供查餘額的 API）。\n\n要繼續嗎？",
                this, confirmText: "檢查", cancelText: "取消");
            if (ok)
                await RunApiCheckAsync(deep: true);
        }

        private async System.Threading.Tasks.Task RunApiCheckAsync(bool deep)
        {
            EnsureApiStatusRows();

            foreach (var row in ApiStatusRows)
            {
                row.State = "檢查中…";
                row.Dot = "#C9CDD4";
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                var results = await ApiHealthChecker.CheckAllAsync(deep, cts.Token);
                foreach (var result in results)
                    ProviderStatusMonitor.ReportCheck(result);
            }
            catch (Exception ex)
            {
                AppLog.Warn("ApiStatus", "API 狀態檢查失敗", ex);
            }

            RefreshSpendColumns();
        }

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

        private void ProviderTopUp_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ApiStatusRow row)
                return;

            string raw = (row.TopUp ?? "").Trim();
            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double amount) && amount > 0)
            {
                bool isNew = !_providerTopUpUsd.TryGetValue(row.ProviderId, out double old) || Math.Abs(old - amount) > 0.001;
                _providerTopUpUsd[row.ProviderId] = amount;
                if (isNew)
                    _providerTopUpSince[row.ProviderId] = DateTime.Today; // 重新填金額＝重新起算
            }
            else
            {
                _providerTopUpUsd.Remove(row.ProviderId);
                _providerTopUpSince.Remove(row.ProviderId);
            }

            SavePreferences();
            RefreshSpendColumns();
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
