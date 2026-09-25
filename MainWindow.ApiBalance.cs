using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Cat5201
{
    // MainWindow 部分類別 — 右下角「API 餘額」小面板
    //
    // 各家 AI 服務的一般金鑰都沒有查餘額的 API（2026-09 實測：Anthropic / OpenAI / Google / Perplexity 都沒有），
    // 所以這裡的剩餘是「你填的儲值金額 − 這台機器自起算日以來記到的花費」。
    // 花費來自 UsageMeter 的逐筆帳目，每一次真的花錢的呼叫都會扣，不是估個整數。
    // 儲值之後請到「個人化 → API」把金額改成新的總額（會重新起算）。
    public partial class MainWindow
    {
        public sealed class ApiBalanceRow : INotifyPropertyChanged
        {
            public string ProviderId { get; init; } = "";
            public string DisplayName { get; init; } = "";

            private string _amount = "";
            private string _dot = "#C9CDD4";
            private string _tip = "";

            /// <summary>顯示用剩餘金額（有填儲值才有數字，否則顯示本月花費）。</summary>
            public string Amount { get => _amount; set => Set(ref _amount, value); }
            public string Dot { get => _dot; set => Set(ref _dot, value); }
            public string Tip { get => _tip; set => Set(ref _tip, value); }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void Set(ref string field, string value, [CallerMemberName] string? name = null)
            {
                if (field == value) return;
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        public ObservableCollection<ApiBalanceRow> ApiBalanceRows { get; } = new();

        private bool _showApiBalancePanel;
        private bool _apiBalanceWired;

        public bool IsApiBalancePanelEnabled() => _showApiBalancePanel;

        /// <summary>個人化開關：開啟時面板出現在右下角自動模型開關上方。</summary>
        private void ApiBalancePanelSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;

            _showApiBalancePanel = ApiBalancePanelSwitch?.IsChecked == true;
            ApplyApiBalancePanelVisibility();
            SavePreferences();
        }

        private void ApplyApiBalancePanelVisibility()
        {
            if (ApiBalancePanel == null)
                return;

            if (_showApiBalancePanel)
            {
                WireApiBalance();
                RefreshApiBalancePanel();
            }

            ApiBalancePanel.Visibility = _showApiBalancePanel ? Visibility.Visible : Visibility.Collapsed;
        }

        private void WireApiBalance()
        {
            if (_apiBalanceWired)
                return;
            _apiBalanceWired = true;

            if (ApiBalanceList != null)
                ApiBalanceList.ItemsSource = ApiBalanceRows;

            // 即時：每一筆花費進帳本就重算；服務狀態變化（餘額不足、金鑰失效）換燈號。
            SpendLedger.Changed += () =>
                Dispatcher.BeginInvoke(new Action(RefreshApiBalancePanel));
            ProviderBalance.Changed += () =>
                Dispatcher.BeginInvoke(new Action(RefreshApiBalancePanel));
            ProviderStatusMonitor.Changed += _ =>
                Dispatcher.BeginInvoke(new Action(RefreshApiBalancePanel));
        }

        /// <summary>重算每個服務的剩餘；沒有金鑰也沒有花費的服務不列，面板才不會變成一堆雜訊。</summary>
        internal void RefreshApiBalancePanel()
        {
            if (!_showApiBalancePanel || ApiBalancePanel == null)
                return;

            var states = ProviderStatusMonitor.All().ToDictionary(s => s.ProviderId, s => s, StringComparer.OrdinalIgnoreCase);
            var wanted = new List<ApiBalanceRow>();

            foreach (var provider in ApiHealthChecker.Providers)
            {
                bool tracked = ProviderBalance.Has(provider.Id);
                double month = SpendLedger.MonthUsdFor(provider.Id);
                string symbol = ApiHealthChecker.CurrencySymbol(provider.Currency);
                if (!tracked && month <= 0)
                    continue;

                var row = ApiBalanceRows.FirstOrDefault(r => r.ProviderId == provider.Id)
                          ?? new ApiBalanceRow { ProviderId = provider.Id, DisplayName = provider.DisplayName };

                if (tracked)
                {
                    double left = ProviderBalance.Remaining(provider.Id);
                    double total = ProviderBalance.TotalToppedUp(provider.Id);

                    row.Amount = $"{symbol}{left:0.00}";
                    row.Tip = $"{provider.DisplayName}：剩餘 {symbol}{left:0.00}，累計儲值 {symbol}{total:0.00}。\n" +
                              "各家都沒有查餘額的 API，這是本機扣帳：每一筆花費即時扣、沖銷加回。\n" +
                              "再次充值後到「個人化 → API」填入新增的金額按送出。";
                    row.Dot = BalanceDot(left, total, states.GetValueOrDefault(provider.Id));
                }
                else
                {
                    row.Amount = $"本月 {symbol}{ModelCostEstimator.ConvertUsd(month, provider.Currency):0.00}";
                    row.Tip = $"{provider.DisplayName}：尚未設定餘額，先顯示本月花費。\n到「個人化 → API」填入儲值金額按送出，就會改顯示剩餘。";
                    row.Dot = StateColor(states.TryGetValue(provider.Id, out var s) ? s.State : ApiHealthChecker.HealthState.Unknown);
                }

                wanted.Add(row);
            }

            // 就地更新，避免每次重建集合造成閃爍。
            for (int i = ApiBalanceRows.Count - 1; i >= 0; i--)
                if (!wanted.Any(w => ReferenceEquals(w, ApiBalanceRows[i])))
                    ApiBalanceRows.RemoveAt(i);

            foreach (var row in wanted)
                if (!ApiBalanceRows.Contains(row))
                    ApiBalanceRows.Add(row);

            if (ApiBalanceEmptyText != null)
                ApiBalanceEmptyText.Visibility = ApiBalanceRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 快用完就變色：剩 15% 以下紅、35% 以下黃；服務本身已回報餘額不足／金鑰問題一律紅。
        private static string BalanceDot(double left, double topUp, ProviderStatusMonitor.Snapshot? snapshot)
        {
            if (snapshot != null &&
                snapshot.State is ApiHealthChecker.HealthState.NoCredit
                    or ApiHealthChecker.HealthState.InvalidKey
                    or ApiHealthChecker.HealthState.Offline)
            {
                return "#E2483D";
            }

            double ratio = topUp > 0 ? left / topUp : 1;
            return ratio <= 0.15 ? "#E2483D" : ratio <= 0.35 ? "#E8A33D" : "#2FA36B";
        }

        /// <summary>點面板＝打開完整的 API 狀態頁（填儲值金額、重新檢查都在那裡）。</summary>
        private void ApiBalancePanel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OpenSettings_Click(sender, new RoutedEventArgs());
            ShowApiTab_Click(sender, new RoutedEventArgs());
        }
    }
}
