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
        // 決策窗：這個產品的賣點，但不是每個人每次都想看，小螢幕上它也會擋到畫布。
        // 預設開啟，跟 API 餘額面板同一組「面板顯示」設定。
        private bool _showDecisionPanel = true;

        public bool IsDecisionPanelEnabled() => _showDecisionPanel;

        private void DecisionPanelSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;

            _showDecisionPanel = DecisionPanelSwitch?.IsChecked == true;
            ApplyDecisionPanelVisibility();
            SavePreferences();
        }

        /// <summary>套用決策窗的顯示狀態（載入偏好與切換開關時都會呼叫）。</summary>
        private void ApplyDecisionPanelVisibility()
        {
            if (DecisionPanel != null)
                DecisionPanel.Visibility = _showDecisionPanel ? Visibility.Visible : Visibility.Collapsed;
        }

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

                // 這個小面板一律以美元顯示，不管每個服務各自的計價幣別是什麼。
                //
                // 它的工作是「四家並排，一眼看出哪一家快沒了」。A$ 跟 US$ 混在一起排，
                // 要比大小得先心算一次匯率，那就不叫快捷查看了。
                // 各自的原幣別留在「個人化 → API」——那一頁的工作是對帳，
                // 單位必須跟官網帳單一致，兩邊的用途不同，單位也就不該一樣。
                string nativeCurrency = ProviderBalance.CurrencyOf(provider.Id);
                const string symbol = "US$";
                if (!tracked && month <= 0)
                    continue;

                var row = ApiBalanceRows.FirstOrDefault(r => r.ProviderId == provider.Id)
                          ?? new ApiBalanceRow { ProviderId = provider.Id, DisplayName = provider.DisplayName };

                if (tracked)
                {
                    double nativeLeft = ProviderBalance.Remaining(provider.Id);
                    double left = ModelCostEstimator.ToUsd(nativeLeft, nativeCurrency);
                    double total = ModelCostEstimator.ToUsd(
                        ProviderBalance.TotalToppedUp(provider.Id), nativeCurrency);
                    var reconciled = ProviderBalance.LastReconciledUtc(provider.Id);

                    // 原幣別不是美元時，tooltip 補上官網上看到的那個數字：
                    // 換算是為了好比較，不是為了把原始事實藏起來。
                    string native = string.Equals(nativeCurrency, "USD", StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : $"（{ApiHealthChecker.CurrencySymbol(nativeCurrency)}{System.Math.Max(0, nativeLeft):0.00}）";

                    row.Amount = $"{symbol}{Math.Max(0, left):0.00}";
                    row.Tip = $"{provider.DisplayName}：剩餘 {symbol}{Math.Max(0, left):0.00}{native}，累計儲值 {symbol}{total:0.00}。\n" +
                              (reconciled == null
                                  ? "各家都沒有查餘額的 API，這是本機扣帳：每一筆花費即時扣、沖銷加回。\n"
                                  : $"上次對帳 {reconciled.Value.ToLocalTime():M/d HH:mm}，之後本機扣了 " +
                                    $"{symbol}{ModelCostEstimator.ToUsd(ProviderBalance.DeductedSinceReconcile(provider.Id), nativeCurrency):0.00}。\n") +
                              (left < -0.005
                                  ? $"已超出 {symbol}{-left:0.00}——本機估的花費比你填的餘額多，請到官網對一次帳。\n"
                                  : "") +
                              "再次充值後到「個人化 → API」填入官網顯示的餘額按送出。";
                    row.Dot = BalanceDot(left, total, states.GetValueOrDefault(provider.Id));
                }
                else
                {
                    row.Amount = $"本月 {symbol}{month:0.00}";
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
