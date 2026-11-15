using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Cat5201;

namespace Cat5201
{
    /// <summary>
    /// 商品級全域防護（§13 產品 UX 收尾）：任何未被捕捉的例外都不再讓程式無聲蒸發——
    /// 一律寫入診斷日誌（%LOCALAPPDATA%\cat5201-pro\logs），UI 執行緒的例外另給友善對話框並嘗試存活，
    /// 讓使用者有機會存檔，而不是直接丟失整個畫布。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // UI 執行緒未處理例外：記錄 + 友善提示 + 嘗試繼續執行（使用者的畫布還在，可先存檔）。
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 背景執行緒未處理例外：無法攔截存活，至少完整留痕（否則就是「程式自己消失了」）。
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                AppLog.Error("AppDomain", "未處理例外（背景執行緒，程式即將終止）", args.ExceptionObject as Exception);

            // 沒被 await 的 Task 例外：預設會被吞掉，改成留痕 + 標記已觀察避免程序終止。
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppLog.Warn("TaskScheduler", "未觀察的 Task 例外", args.Exception);
                args.SetObserved();
            };

            UseInvariantNumberFormat();

            AppLog.Info("App", $"啟動 v{typeof(App).Assembly.GetName().Version}");
            base.OnStartup(e);
        }

        /// <summary>
        /// 數字一律用「小數點是 .、千分位是 ,」的寫法，不跟著 Windows 地區設定變。
        ///
        /// 程式裡有一百多處把數字直接組進字串，其中不少會送進提示或寫進報告。
        /// 在小數點用逗號的地區（德文、法文等）3.14 會變成 3,14，模型讀到的就是另一個數字。
        /// 只換數字的分隔符號；日期、貨幣符號維持使用者的地區設定。
        /// 繁體中文的 Windows 本來就是這組符號，所以對它沒有任何改變。
        /// </summary>
        private static void UseInvariantNumberFormat()
        {
            try
            {
                var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.CurrentCulture.Clone();
                var n = culture.NumberFormat;
                n.NumberDecimalSeparator = ".";
                n.NumberGroupSeparator = ",";
                n.NumberGroupSizes = new[] { 3 };
                n.PercentDecimalSeparator = ".";
                n.PercentGroupSeparator = ",";
                n.CurrencyDecimalSeparator = ".";
                n.CurrencyGroupSeparator = ",";
                n.NegativeSign = "-";
                n.PositiveSign = "+";
                n.DigitSubstitution = System.Globalization.DigitShapes.None;

                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
            catch (Exception ex)
            {
                AppLog.Warn("App", "數字格式設定失敗，沿用系統地區設定", ex);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLog.Info("App", $"正常關閉（ExitCode={e.ApplicationExitCode}）");
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error("Dispatcher", "UI 執行緒未處理例外", e.Exception);

            // 這裡刻意用原生 MessageBox：崩潰路徑上 MainWindow / 自製對話框資源未必還活著，越簡單越可靠。
            try
            {
                MessageBox.Show(
                    UiLanguage.P("程式遇到未預期的錯誤，已記錄到診斷日誌。\n\n", "The app hit an unexpected error. It has been written to the diagnostic log.\n\n") +
                    $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
                    UiLanguage.P("建議立即存檔後重新啟動程式。\n", "Save your work and restart the app.\n") +
                    UiLanguage.P($"日誌位置：%LOCALAPPDATA%\\cat5201-pro\\logs", $"Log folder: %LOCALAPPDATA%\\cat5201-pro\\logs"),
                    UiLanguage.P("cat5201-pro — 發生錯誤", "cat5201-pro — Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { }

            e.Handled = true; // 嘗試存活，讓使用者有機會存檔；若錯誤持續發生，使用者可自行關閉。
        }
    }
}
