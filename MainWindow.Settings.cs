using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PathShape = System.Windows.Shapes.Path;

namespace Cat5201
{
    // MainWindow 部分類別 — 設定視窗：分頁、自訂路由、費用與影片設定、記憶清單（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        // ===== Memory v1：個人化設定對話框（齒輪開啟）=====

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            RefreshMemoryPanel();
            SyncDownstreamAutoModeRadios();
            SyncPresentationEngineRadios();
            SyncDocumentEngineRadios();
            SyncIllustrationQualityRadios();
            EnsureApiStatusRows();
            SyncVideoStyleUI();
            BuildTaskRoutingPanel();
            SyncCostControls();
            ShowPersonalizationTab_Click(this, new RoutedEventArgs()); // 每次開啟預設停在「個人化」分頁
            RefreshApiPanel();
            if (SettingsOverlay != null)
                SettingsOverlay.Visibility = Visibility.Visible;
            MemoryInput?.Focus();
        }

        // ===== 設定分頁切換：個人化 / API / 技能 =====

        private void ShowPersonalizationTab_Click(object sender, RoutedEventArgs e)
        {
            if (PersonalizationScroll != null) PersonalizationScroll.Visibility = Visibility.Visible;
            if (ApiScroll != null) ApiScroll.Visibility = Visibility.Collapsed;
            if (SkillsScroll != null) SkillsScroll.Visibility = Visibility.Collapsed;
            SetTabSelected(TabPersonalizationBtn, true);
            SetTabSelected(TabApiBtn, false);
            SetTabSelected(TabSkillsBtn, false);
        }

        private void ShowApiTab_Click(object sender, RoutedEventArgs e)
        {
            if (PersonalizationScroll != null) PersonalizationScroll.Visibility = Visibility.Collapsed;
            if (ApiScroll != null) ApiScroll.Visibility = Visibility.Visible;
            if (SkillsScroll != null) SkillsScroll.Visibility = Visibility.Collapsed;
            SetTabSelected(TabPersonalizationBtn, false);
            SetTabSelected(TabApiBtn, true);
            SetTabSelected(TabSkillsBtn, false);
            RefreshApiPanel();
        }

        private void ShowSkillsTab_Click(object sender, RoutedEventArgs e)
        {
            if (PersonalizationScroll != null) PersonalizationScroll.Visibility = Visibility.Collapsed;
            if (ApiScroll != null) ApiScroll.Visibility = Visibility.Collapsed;
            if (SkillsScroll != null) SkillsScroll.Visibility = Visibility.Visible;
            SetTabSelected(TabPersonalizationBtn, false);
            SetTabSelected(TabApiBtn, false);
            SetTabSelected(TabSkillsBtn, true);
            RefreshSkillsList();
        }

        private static void SetTabSelected(System.Windows.Controls.Button? btn, bool selected)
        {
            if (btn == null) return;
            btn.Background = selected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x11, 0x11))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF1, 0xF3));
            btn.Foreground = selected
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44));
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlay != null)
                SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        // 點對話框外的暗色區域 → 關閉。
        private void SettingsOverlay_BackgroundClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SettingsOverlay != null)
                SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        // 點卡片本身不關閉（阻止冒泡到背景）。
        private void SettingsCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        // ===== 任務 → AI 自訂路由（個人化設定中的下拉清單）=====

        // 只列出真正會被路由特化的任務模式（Chat 為自由對話、沿用當前選定模型，不在此處覆蓋）。
        private static (NodeTaskMode Mode, string Label)[] _routingModes => new[]
        {
            (NodeTaskMode.Research, UiLanguage.P("研究 / 查資料", "Research / look up")),
            (NodeTaskMode.Code, UiLanguage.P("程式碼", "Code")),
            (NodeTaskMode.Translate, UiLanguage.P("翻譯", "Translation")),
            (NodeTaskMode.Summarize, UiLanguage.P("摘要", "Summary")),
            (NodeTaskMode.Rewrite, UiLanguage.P("改寫 / 潤稿", "Rewrite / polish")),
            (NodeTaskMode.Extract, UiLanguage.P("資訊擷取", "Information extraction")),
        };

        // 每種任務真正需要的能力；只有具備該能力的已啟用模型才會出現在該任務的下拉中。
        // null = 純文字任務（翻譯 / 摘要 / 改寫 / 擷取），不特別限制，列出全部已啟用模型。
        private static AiModelCapability? RequiredCapabilityFor(NodeTaskMode mode) => mode switch
        {
            NodeTaskMode.Research => AiModelCapability.Search, // 需即時搜尋（目前只有 Perplexity 具備）
            NodeTaskMode.Code => AiModelCapability.Code,       // 需寫程式能力（排除無 Code 的 Perplexity）
            _ => null
        };

        // 程式化建立每列時暫時靜音 SelectionChanged，避免初始化就寫入 override。
        private bool _buildingTaskRouting;

        private void BuildTaskRoutingPanel()
        {
            if (TaskRoutingList == null)
                return;

            _buildingTaskRouting = true;
            try
            {
                TaskRoutingList.Children.Clear();
                var labelBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                var comboStyle = TryFindResource("RoutingComboBoxStyle") as Style;

                foreach (var (mode, label) in _routingModes)
                {
                    // 依任務能力過濾：不適用該任務的模型不列入（例：Perplexity 不會出現在「程式碼」）。
                    // 用 Available 保留 registry 的公司分組順序（GPT → Claude → Perplexity → Gemini），
                    // 不用 WithCapability（那會按價格排序，把同公司的拆散）。
                    var cap = RequiredCapabilityFor(mode);
                    var models = AiModelRegistry.Available
                        .Where(m => !cap.HasValue || (m.Capabilities & cap.Value) == cap.Value)
                        .ToList();

                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var caption = new TextBlock
                    {
                        Text = label,
                        FontSize = 12.5,
                        Foreground = labelBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(caption, 0);
                    row.Children.Add(caption);

                    var combo = new ComboBox { Tag = mode };
                    if (comboStyle != null)
                        combo.Style = comboStyle;

                    // 第一項固定為「自動」（= 清除 override，回退內建建議）。
                    var autoItem = new ComboBoxItem { Content = UiLanguage.P("自動（建議）", "Auto (recommended)"), Tag = null };
                    combo.Items.Add(autoItem);

                    string? current = GetTaskRoutingOverride(mode);
                    ComboBoxItem selected = autoItem;

                    foreach (var m in models)
                    {
                        var item = new ComboBoxItem { Content = m.DisplayName, Tag = m.Id };
                        combo.Items.Add(item);
                        if (!string.IsNullOrEmpty(current) &&
                            string.Equals(current, m.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            selected = item;
                        }
                    }

                    combo.SelectedItem = selected;
                    combo.SelectionChanged += TaskRoutingCombo_SelectionChanged;

                    Grid.SetColumn(combo, 1);
                    row.Children.Add(combo);

                    TaskRoutingList.Children.Add(row);
                }
            }
            finally
            {
                _buildingTaskRouting = false;
            }
        }

        // ===== §15 個人化：高成本模型開關 + 手動逾時 =====

        // 把目前狀態反映到設定面板的控制項（開啟設定時呼叫）。
        private void SyncCostControls()
        {
            _syncingCostControls = true;
            try
            {
                if (BlockOpusSwitch != null)
                    BlockOpusSwitch.IsChecked = AiAutoCostPolicy.BlockOpus;
                if (BlockDeepResearchSwitch != null)
                    BlockDeepResearchSwitch.IsChecked = AiAutoCostPolicy.BlockDeepResearch;
                if (CompanionPdfSwitch != null)
                    CompanionPdfSwitch.IsChecked = DeliveryOptions.CompanionPdf;
                if (IndependentReviewSwitch != null)
                    IndependentReviewSwitch.IsChecked = DeliveryOptions.IndependentReview;
                if (AutoFixAfterReviewSwitch != null)
                {
                    AutoFixAfterReviewSwitch.IsChecked = DeliveryOptions.AutoFixAfterReview;
                    AutoFixAfterReviewSwitch.IsEnabled = DeliveryOptions.IndependentReview;
                }
                if (DecisionPanelSwitch != null)
                    DecisionPanelSwitch.IsChecked = _showDecisionPanel;
                if (TimeoutInput != null)
                    TimeoutInput.Text = _manualTimeoutSeconds > 0
                        ? _manualTimeoutSeconds.ToString()
                        : "";
                if (ReadUpstreamAttachmentsSwitch != null)
                    ReadUpstreamAttachmentsSwitch.IsChecked = _readUpstreamAttachments;

                if (ApiBalancePanelSwitch != null)
                    ApiBalancePanelSwitch.IsChecked = _showApiBalancePanel;
                if (MobileMirrorSwitch != null)
                    MobileMirrorSwitch.IsChecked = _mobileMirrorEnabled;
                if (MobileMirrorReadOnlySwitch != null)
                    MobileMirrorReadOnlySwitch.IsChecked = _mobileMirrorReadOnly;
                if (DailyBudgetInput != null)
                    DailyBudgetInput.Text = _dailyBudgetTwd > 0 ? _dailyBudgetTwd.ToString() : "";
                if (AutoConfirmSecondsInput != null)
                    AutoConfirmSecondsInput.Text = _autoConfirmSeconds > 0 ? _autoConfirmSeconds.ToString() : "0";
                if (GoogleAutoUploadSwitch != null)
                    GoogleAutoUploadSwitch.IsChecked = _googleAutoUploadDrive;
                if (DriveTypePdf != null)   DriveTypePdf.IsChecked   = _driveUploadTypes.Contains("pdf");
                if (DriveTypeWord != null)  DriveTypeWord.IsChecked  = _driveUploadTypes.Contains("word");
                if (DriveTypePpt != null)   DriveTypePpt.IsChecked   = _driveUploadTypes.Contains("ppt");
                if (DriveTypeExcel != null) DriveTypeExcel.IsChecked = _driveUploadTypes.Contains("excel");
                if (DriveTypeImage != null) DriveTypeImage.IsChecked = _driveUploadTypes.Contains("image");
                if (DriveTypeVideo != null) DriveTypeVideo.IsChecked = _driveUploadTypes.Contains("video");
                if (DriveTypeText != null)  DriveTypeText.IsChecked  = _driveUploadTypes.Contains("text");
                if (DriveConvertSwitch != null)
                    DriveConvertSwitch.IsChecked = _driveConvertGoogle;
                if (SpendTodayText != null)
                    SpendTodayText.Text = UiLanguage.P($"今日已花：{SpendLedger.TodayDisplay()}", $"Spent today: {SpendLedger.TodayDisplay()}");
                RefreshSkillsList();   // §19：開設定面板時同步技能清單
                RefreshOutputDirLabel();
            }
            finally
            {
                _syncingCostControls = false;
            }
        }

        private void ReadUpstreamAttachmentsSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            _readUpstreamAttachments = ReadUpstreamAttachmentsSwitch?.IsChecked == true;
            SavePreferences();
        }

        private void BlockOpusSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            AiAutoCostPolicy.BlockOpus = BlockOpusSwitch?.IsChecked == true;
            SaveState();
        }

        /// <summary>
        /// 產出檔案時要不要同時附一份同內容 PDF。關掉主要是為了速度——
        /// PDF 是整個產出流程最慢的一步，單頁編輯時尤其有感。
        /// </summary>
        private void CompanionPdfSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            DeliveryOptions.CompanionPdf = CompanionPdfSwitch?.IsChecked == true;
            SaveState();
        }

        /// <summary>產出報告後要不要跑獨立審查（要另外花錢，用自己金鑰的人要能關掉）。</summary>
        private void IndependentReviewSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            DeliveryOptions.IndependentReview = IndependentReviewSwitch?.IsChecked == true;
            // 自動修正要先有審查結果：審查關掉時開關跟著停用（保留原本的選擇，重新開啟審查時照舊）。
            if (AutoFixAfterReviewSwitch != null)
                AutoFixAfterReviewSwitch.IsEnabled = DeliveryOptions.IndependentReview;
            SaveState();
        }

        /// <summary>審查找到高／中問題時自動修正一輪（要多花一趟沙箱與一次審查，預設關）。</summary>
        private void AutoFixAfterReviewSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            DeliveryOptions.AutoFixAfterReview = AutoFixAfterReviewSwitch?.IsChecked == true;
            SaveState();
        }

        private void BlockDeepResearchSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            AiAutoCostPolicy.BlockDeepResearch = BlockDeepResearchSwitch?.IsChecked == true;
            SaveState();
        }

        // 逾時輸入：空白 = 自動；否則限制在 30～1800 秒（30 分鐘）的合理範圍。
        private void TimeoutInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (_syncingCostControls || TimeoutInput == null)
                return;

            string raw = (TimeoutInput.Text ?? "").Trim();

            if (string.IsNullOrEmpty(raw))
            {
                _manualTimeoutSeconds = 0;
            }
            else if (int.TryParse(raw, out int secs))
            {
                _manualTimeoutSeconds = Math.Clamp(secs, 30, 1800);
            }
            else
            {
                return; // 非數字：忽略，不覆寫既有值
            }

            SaveState();
        }

        private void TaskRoutingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_buildingTaskRouting)
                return;
            if (sender is not ComboBox combo || combo.Tag is not NodeTaskMode mode)
                return;

            string? modelId = (combo.SelectedItem as ComboBoxItem)?.Tag as string;

            if (string.IsNullOrEmpty(modelId))
                ClearTaskRoutingOverride(mode);
            else
                SetTaskRoutingOverride(mode, modelId);
        }

        private void RememberPreference_Click(object sender, RoutedEventArgs e)
        {
            if (_nodeService == null || MemoryInput == null)
                return;

            string text = (MemoryInput.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            _nodeService.AddManualPreference(text);
            MemoryInput.Clear();
            RefreshMemoryPanel();
            // 不彈確認視窗；新偏好會直接出現在下方清單。
        }

        // 單獨刪除一條偏好（× 按鈕）。
        private void DeletePreference_Click(object sender, RoutedEventArgs e)
        {
            if (_nodeService == null)
                return;

            if (sender is FrameworkElement fe && fe.Tag is string key && !string.IsNullOrWhiteSpace(key))
            {
                _nodeService.DeletePreference(key);
                RefreshMemoryPanel();
            }
        }

        // 「當前記憶」按鈕：展開／收合記憶清單（展開時即時刷新）。
        private void ToggleMemoryList_Click(object sender, RoutedEventArgs e)
        {
            if (MemoryListPanel == null)
                return;

            bool show = MemoryListPanel.Visibility != Visibility.Visible;
            MemoryListPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
                RefreshMemoryPanel();
        }

        private void ClearMemory_Click(object sender, RoutedEventArgs e)
        {
            if (_nodeService == null)
                return;

            bool ok = MenuConfirmDialog.ShowConfirm(
                this,
                UiLanguage.P("清除記憶", "Clear memory"),
                UiLanguage.P("確定要清除全部記憶嗎？此動作無法復原。", "Clear all memories? This can't be undone."),
                this,
                confirmText: UiLanguage.P("清除", "Clear"),
                cancelText: UiLanguage.P("取消", "Cancel"),
                danger: true);

            if (!ok)
                return;

            _nodeService.ClearShownMemory();
            RefreshMemoryPanel();
        }

        public void RefreshMemoryPanel()
        {
            if (_nodeService == null)
                return;

            try
            {
                var items = _nodeService.GetPreferenceItems();
                if (PreferenceList != null)
                    PreferenceList.ItemsSource = items;

                // 空狀態提示 + 清除鈕只在有資料時顯示。
                bool hasItems = items != null && items.Count > 0;
                if (MemoryEmptyHint != null)
                    MemoryEmptyHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
                if (ClearAllMemoryButton != null)
                    ClearAllMemoryButton.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                // 面板刷新失敗不影響主流程
            }
        }

        // 把「目前生效的影片風格」填進輸入框：使用者自訂時顯示自訂內容，否則顯示原廠完整 prompt
        // （讓使用者看得到格式才能照著改寫）。
        private void SyncVideoStyleUI()
        {
            if (VideoStyleInput == null)
                return;

            VideoStyleInput.Text = GetEffectiveVideoStylePrompt();

            // 影片模型檔位下拉同步（Tag 對 Standard / Fast / Lite）。
            if (VideoModelTierCombo != null)
            {
                string want = VeoModels.ToStorageValue(_videoModelTier);
                foreach (var obj in VideoModelTierCombo.Items)
                {
                    if (obj is ComboBoxItem ci &&
                        string.Equals(ci.Tag?.ToString(), want, StringComparison.OrdinalIgnoreCase))
                    {
                        VideoModelTierCombo.SelectedItem = ci;
                        break;
                    }
                }
            }
        }

        // 影片模型檔位變更：落地個人化（前期測試建議 Lite）。
        private void VideoModelTierCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoModelTierCombo?.SelectedItem is not ComboBoxItem ci)
                return;

            _videoModelTier = VeoModels.ParseTier(ci.Tag?.ToString());
            SavePreferences();
        }

        // 失焦時落地影片風格：等於原廠預設（或空）就清成空字串（=用預設），否則存為自訂覆寫。
        private void VideoStyleInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (VideoStyleInput == null)
                return;

            string text = (VideoStyleInput.Text ?? "").Trim();

            _videoStyleOverride =
                (text.Length == 0 ||
                 string.Equals(text, VideoStyle.DefaultCinematicPrompt.Trim(), StringComparison.Ordinal))
                    ? ""
                    : text;

            SavePreferences();
        }

        private void ResetVideoStyle_Click(object sender, RoutedEventArgs e)
        {
            _videoStyleOverride = "";
            SavePreferences();
            SyncVideoStyleUI();
        }
    }
}
