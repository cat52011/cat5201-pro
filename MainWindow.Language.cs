using System;
using System.Windows;

namespace Cat5201
{
    // MainWindow 部分類別 — 介面語言（個人化 → 語言、第一次啟動時的語言選擇）
    public partial class MainWindow
    {
        /// <summary>使用者選過語言了沒（偏好檔有值）。沒選過的話啟動時問一次。</summary>
        private bool _interfaceLanguageChosen;
        private bool _suppressUiLanguageRadioEvents;

        /// <summary>啟動時呼叫：只有偏好檔裡還沒有語言（第一次開啟）才跳出選擇視窗，之後永遠不再問。要在金鑰精靈之前，那個視窗要用選好的語言顯示。</summary>
        private void AskInterfaceLanguageIfNeeded()
        {
            if (_interfaceLanguageChosen)
                return;

            try
            {
                var dlg = new LanguagePickerDialog(this);
                dlg.ShowDialog();
                // 只在下載後第一次開啟時問這一次（本程式是解壓即用，沒有安裝程式，第一次開啟就是「安裝」）。
                // 不管是點選項還是按 Esc 關掉都存下來（Esc＝依系統語言猜的那個），之後只能在 個人化 → 語言 改。
                ApplyInterfaceLanguage(dlg.SelectedCode);
                AppLog.Info("Language", "首次選擇介面語言：" + dlg.SelectedCode);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Language", "語言選擇視窗失敗，沿用繁體中文", ex);
            }
        }

        private void ApplyInterfaceLanguage(string code)
        {
            _interfaceLanguageChosen = true;
            UiLanguage.Code = code;
            SyncUiLanguageRadios();
            RefreshLocalizedCodeText();
            SavePreferences();
        }

        private void SyncUiLanguageRadios()
        {
            _suppressUiLanguageRadioEvents = true;
            try
            {
                if (UiLanguageZh != null) UiLanguageZh.IsChecked = !UiLanguage.IsEnglish;
                if (UiLanguageEn != null) UiLanguageEn.IsChecked = UiLanguage.IsEnglish;
            }
            finally
            {
                _suppressUiLanguageRadioEvents = false;
            }
        }

        private void UiLanguage_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressUiLanguageRadioEvents)
                return;

            string code = ReferenceEquals(sender, UiLanguageEn) ? UiLanguage.English : UiLanguage.Chinese;
            ApplyInterfaceLanguage(code);
        }

        /// <summary>畫布上已經存在的節點：重新套用由程式碼填的字。</summary>
        private void RefreshLocalizedNodes()
        {
            foreach (var node in System.Linq.Enumerable.OfType<NodeControl>(MainCanvas.Children))
                node.RefreshLocalizedText();
        }

        private void RefreshCurrentFileLabel()
        {
            if (CurrentFileLabel == null) return;
            CurrentFileLabel.Text = string.IsNullOrEmpty(_currentFilePath)
                ? UiLanguage.P("目前檔案：尚未建立", "Current file: not created yet")
                : UiLanguage.P("目前檔案：", "Current file: ") + DisplayNameFromPath(_currentFilePath);
        }

        /// <summary>
        /// XAML 的字靠綁定自己會換；這裡補上「由程式碼填進去」的那些字
        /// （開始畫面標語、目前檔案名、含價格的選項、API 面板…）。
        /// </summary>
        private void RefreshLocalizedCodeText()
        {
            try
            {
                if (StartUI.Visibility == Visibility.Visible)
                    SetRandomStartMessage();
                RefreshCurrentFileLabel();
                SyncIllustrationQualityRadios();
                RefreshApiPanel();
                RefreshMemoryPanel();
                RefreshTrashList();
                RefreshTrashEntryPoint();
                BuildTaskRoutingPanel();
                SyncCostControls();
                RefreshGoogleStatus();
                ApplyDecisionPanelSize();     // 放大／縮回按鈕的提示
                UpdateDecisionPanelForCurrentMode();
                RefreshLocalizedNodes();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Language", "切換語言後刷新介面失敗", ex);
            }
        }
    }
}
