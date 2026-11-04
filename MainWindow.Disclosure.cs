using System;
using System.Windows;

namespace Cat5201
{
    public partial class MainWindow
    {
        /// <summary>
        /// 決策窗「匯出紀錄」：把目前顯示的節點最近一次執行，整理成 AI 使用紀錄（AiUsageDisclosure）。
        /// 存在產出資料夾，但不掛到節點的輸出清單上——重跑節點會刪掉輸出檔，紀錄不該跟著被刪。
        /// </summary>
        private void DecisionExportButton_Click(object sender, RoutedEventArgs e)
        {
            var node = _lastDecisionNode;
            var log = node == null ? null : GetLatestExecutionLog(node);
            if (node == null || log == null)
            {
                MessageBox.Show(this, UiLanguage.P("這個節點還沒有執行紀錄。先執行一次，再匯出。", "This node hasn't run yet. Run it once, then export."), UiLanguage.P("匯出 AI 使用紀錄", "Export AI usage record"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string markdown = AiUsageDisclosure.Build(log, node.GetTopText());
                // 跟存檔用同一個標題（左側列表的名字），不從要求裡截字。
                string title = ShortTitle.Normalize(string.IsNullOrEmpty(_currentFilePath) ? "" : DisplayNameFromPath(_currentFilePath));
                if (title.Length == 0) title = TitleFor(node.GetTopText());
                var file = GeneratedFileWriter.WriteMarkdown(
                    GetGeneratedFilesDir(), title + UiLanguage.P("（AI使用紀錄）", " (AI usage record)"), markdown, UiLanguage.P("AI 使用紀錄", "AI usage record"));

                if (!file.Success)
                {
                    MessageBox.Show(this, UiLanguage.P("紀錄沒有存成功：", "The record wasn't saved:") + file.ErrorMessage, UiLanguage.P("匯出 AI 使用紀錄", "Export AI usage record"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AppLog.Info("Disclosure", $"已匯出 AI 使用紀錄：{file.FileName}");
                OpenPreview(file.FilePath, node);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Disclosure", "匯出 AI 使用紀錄失敗", ex);
                MessageBox.Show(this, UiLanguage.P("匯出失敗：", "Export failed:") + ex.Message, UiLanguage.P("匯出 AI 使用紀錄", "Export AI usage record"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>檔名用要求的開頭幾個字（去掉換行與檔名不允許的字元由 GeneratedFileWriter 處理）。</summary>
        private static string TitleFor(string? request)
        {
            string t = (request ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length > 20) t = t[..20];
            return t.Length == 0 ? UiLanguage.P("節點", "Node") : t;
        }
    }
}
