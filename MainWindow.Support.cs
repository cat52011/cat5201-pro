using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace Cat5201
{
    // MainWindow 部分類別 — 支援（回報問題）
    public partial class MainWindow
    {
        private const string SupportEmail = "s10655097@gmail.com";

        // 設定視窗「回報問題」：把最近 7 天日誌 + 環境資訊打包到桌面（金鑰已遮蔽），並在檔案總管選取它。
        private void ReportProblem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ver = typeof(MainWindow).Assembly.GetName().Version;
                string version = ver == null ? "unknown" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

                AppLog.Info("Support", $"使用者產生回報診斷檔（v{version}）");
                string zipPath = DiagnosticsBundle.Create(desktop, version, new[]
                {
                    $"畫布節點數：{MainCanvas.Children.OfType<NodeControl>().Count()}"
                });

                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Support", "開啟檔案總管失敗", ex);
                }

                MenuConfirmDialog.ShowMessage(this, "回報問題",
                    $"已在桌面產生診斷檔：\n{Path.GetFileName(zipPath)}\n\n" +
                    $"請把這個檔案寄到 {SupportEmail}，並簡單寫下：\n" +
                    "・你做了什麼\n・預期會怎樣\n・實際發生什麼（有截圖更好）\n\n" +
                    "檔案已自動遮蔽 API 金鑰與 Windows 使用者名稱，不含對話內容；寄出前也可以自己打開檢查。",
                    this);
            }
            catch (Exception ex)
            {
                AppLog.Error("Support", "產生回報診斷檔失敗", ex);
                MenuConfirmDialog.ShowMessage(this, "回報問題",
                    $"產生診斷檔失敗：{ex.Message}\n\n可以直接寄信到 {SupportEmail} 描述狀況。", this);
            }
        }
    }
}
