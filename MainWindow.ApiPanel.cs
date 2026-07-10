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
    // MainWindow 部分類別 — API 金鑰設定面板
    // （P1-2 機械拆分:內容自 MainWindow.xaml.cs 原樣搬出,零邏輯變更。）
    public partial class MainWindow
    {

        // ===== P2-1 首次啟動精靈 =====

        /// <summary>四把主金鑰全空（環境變數+App 內都沒有）→ 顯示 onboarding 引導。</summary>
        private void ShowOnboardingIfNoKeys()
        {
            try
            {
                string[] mains = { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GEMINI_API_KEY", "PERPLEXITY_API_KEY" };
                if (mains.Any(k => !string.IsNullOrWhiteSpace(ApiKeyStore.Resolve(k))))
                    return; // 有任何一把就不打擾

                var dlg = new OnboardingDialog(this);
                bool saved = dlg.ShowDialog() == true;
                if (saved && dlg.EnteredKeys.Count > 0)
                {
                    ApiKeyStore.SetUserKeys(dlg.EnteredKeys.ToDictionary(kv => kv.Key, kv => (string?)kv.Value));
                    _aiRouter.ResetServices(); // 讓服務用新金鑰重建
                    AppLog.Info("Onboarding", $"精靈儲存 {dlg.EnteredKeys.Count} 把金鑰");
                }
            }
            catch (Exception ex) { AppLog.Warn("Onboarding", "首次啟動精靈失敗（略過）", ex); }
        }

        // ===== API 金鑰面板 =====

        // (env 變數名, 狀態文字框, 密碼框) 對應表，集中一次處理避免重複。
        private (string Env, System.Windows.Controls.TextBlock? Status, System.Windows.Controls.PasswordBox? Box)[] ApiKeyRows() =>
            new (string, System.Windows.Controls.TextBlock?, System.Windows.Controls.PasswordBox?)[]
        {
            ("OPENAI_API_KEY",     ApiStatus_OpenAI,     ApiKeyBox_OpenAI),
            ("ANTHROPIC_API_KEY",  ApiStatus_Anthropic,  ApiKeyBox_Anthropic),
            ("GEMINI_API_KEY",     ApiStatus_Gemini,     ApiKeyBox_Gemini),
            ("PERPLEXITY_API_KEY", ApiStatus_Perplexity, ApiKeyBox_Perplexity),
            ("GAMMA_API_KEY",      ApiStatus_Gamma,      ApiKeyBox_Gamma),
            ("GOOGLE_OAUTH_CLIENT_ID",     ApiStatus_GoogleClientId,     ApiKeyBox_GoogleClientId),
            ("GOOGLE_OAUTH_CLIENT_SECRET", ApiStatus_GoogleClientSecret, ApiKeyBox_GoogleClientSecret),
        };

        // 更新各列狀態徽章；密碼框一律清空（不回填祕密），留空＝不變更。
        private void RefreshApiPanel()
        {
            RefreshGoogleStatus(); // §18：Google 連結狀態
            foreach (var row in ApiKeyRows())
            {
                if (row.Box != null) row.Box.Password = "";
                if (row.Status == null) continue;

                switch (ApiKeyStore.GetSource(row.Env))
                {
                    case ApiKeyStore.KeySource.Environment:
                        row.Status.Text = "環境變數";
                        row.Status.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x9D, 0x55));
                        break;
                    case ApiKeyStore.KeySource.UserEntered:
                        row.Status.Text = "已儲存";
                        row.Status.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x6B, 0xE6));
                        break;
                    default:
                        row.Status.Text = "未設定";
                        row.Status.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA8, 0xA8, 0xAC));
                        break;
                }
            }
            if (ApiSaveHint != null) ApiSaveHint.Visibility = Visibility.Collapsed;
        }

        private void SaveApiKeys_Click(object sender, RoutedEventArgs e)
        {
            var updates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ApiKeyRows())
            {
                string entered = row.Box?.Password ?? "";
                if (!string.IsNullOrWhiteSpace(entered))
                    updates[row.Env] = entered.Trim(); // 只更新有輸入的；留空＝保留原值
            }

            if (updates.Count > 0)
            {
                ApiKeyStore.SetUserKeys(updates);
                _aiRouter.ResetServices(); // 讓已快取的服務用新金鑰重建
            }

            RefreshApiPanel();
            if (ApiSaveHint != null)
            {
                ApiSaveHint.Text = updates.Count > 0 ? "已儲存（已加密落地）。" : "沒有新輸入的金鑰。";
                ApiSaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
                    updates.Count > 0
                        ? System.Windows.Media.Color.FromRgb(0x1F, 0x9D, 0x55)
                        : System.Windows.Media.Color.FromRgb(0xA8, 0xA8, 0xAC));
                ApiSaveHint.Visibility = Visibility.Visible;
            }
        }

        private void ClearApiKey_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string env || string.IsNullOrWhiteSpace(env))
                return;

            ApiKeyStore.SetUserKeys(new Dictionary<string, string?> { [env] = null });
            _aiRouter.ResetServices();
            RefreshApiPanel();
            if (ApiSaveHint != null)
            {
                ApiSaveHint.Text = "已清除該金鑰。";
                ApiSaveHint.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA8, 0xA8, 0xAC));
                ApiSaveHint.Visibility = Visibility.Visible;
            }
        }
    }
}
