using System;
using System.IO;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 商品級可觀測性（STRATEGY §5「第一天就接 tracing」的最小落地）：
    /// 極輕量檔案日誌，供全域崩潰處理與「先前只能吞掉的 catch」留下線索。
    /// 設計原則：**絕不拋例外、絕不阻塞主流程**——記不進去就算了，比默默吞例外好，但絕不能反過來害死主程式。
    /// 寫入 %LOCALAPPDATA%\cat5201-pro\logs\app-yyyyMMdd.log，按日分檔，自動清 30 天前舊檔。
    /// </summary>
    public static class AppLog
    {
        private static readonly object _lock = new();
        private static string? _dir;
        private static bool _cleaned;

        private static string? LogDir
        {
            get
            {
                if (_dir != null) return _dir;
                try
                {
                    string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _dir = Path.Combine(baseDir, "cat5201-pro", "logs");
                    Directory.CreateDirectory(_dir);
                }
                catch { _dir = null; }
                return _dir;
            }
        }

        /// <summary>日誌資料夾（建立失敗時為 null）。「回報問題」打包用。</summary>
        public static string? DirectoryPath => LogDir;

        /// <summary>一般資訊（啟動/關閉/重要狀態轉換）。</summary>
        public static void Info(string source, string message) => Write("INFO", source, message, null);

        /// <summary>可預期但值得留痕的失敗（原本的空 catch 應改呼叫這個）。</summary>
        public static void Warn(string source, string message, Exception? ex = null) => Write("WARN", source, message, ex);

        /// <summary>未預期例外（全域 handler / 崩潰路徑）。</summary>
        public static void Error(string source, string message, Exception? ex = null) => Write("ERROR", source, message, ex);

        private static void Write(string level, string source, string message, Exception? ex)
        {
            try
            {
                var dir = LogDir;
                if (dir == null) return;

                var sb = new StringBuilder(256);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                  .Append(" [").Append(level).Append("] ")
                  .Append(source).Append(" — ").Append(message);
                if (ex != null)
                    sb.AppendLine().Append("    ").Append(ex.GetType().Name).Append(": ").Append(ex.Message)
                      .AppendLine().Append(ex.StackTrace);

                string path = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                lock (_lock)
                {
                    File.AppendAllText(path, sb.AppendLine().ToString(), Encoding.UTF8);
                    if (!_cleaned) { _cleaned = true; CleanOldLogs(dir); }
                }
            }
            catch { /* 日誌本身絕不可拖垮主程式 */ }
        }

        private static void CleanOldLogs(string dir)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-30);
                foreach (var f in Directory.GetFiles(dir, "app-*.log"))
                    if (File.GetLastWriteTime(f) < cutoff)
                        File.Delete(f);
            }
            catch { }
        }
    }
}
