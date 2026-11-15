using System;
using System.IO;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// 商品級資料安全：原子寫檔。先寫暫存檔再原子替換，寫到一半斷電/崩潰也不會毀掉原檔；
    /// 舊內容自動留一份 .bak。個人化偏好、專案檔、花費帳本等「使用者資產」一律走這裡。
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string contents, Encoding? encoding = null)
        {
            encoding ??= new UTF8Encoding(false);
            string tmp = path + ".tmp";
            string bak = path + ".bak";

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(tmp, contents, encoding);

            if (File.Exists(path))
            {
                // File.Replace = 原子替換 + 舊檔轉 .bak（同一磁碟區保證原子性）。
                // 但檔案被防毒/同步軟體/另一把手短暫鎖住時會丟 IOException（實測：連續快速存偏好時發生）——
                // 先短暫重試一次，仍失敗就退回「複製覆寫」：非原子但可靠，內容不會丟。
                try
                {
                    File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                }
                catch (IOException)
                {
                    try
                    {
                        System.Threading.Thread.Sleep(50);
                        File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                    }
                    catch (IOException)
                    {
                        File.Copy(tmp, path, overwrite: true);
                        try { File.Delete(tmp); } catch { }
                    }
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        /// <summary>讀檔；主檔毀損/不存在時退回 .bak（有的話）。都沒有回 null。</summary>
        public static string? ReadAllTextWithFallback(string path)
        {
            try
            {
                if (File.Exists(path))
                    return File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                // 主檔毀損＝使用者資料事件，退 .bak 前留下線索（否則「設定莫名變舊」查無原因）。
                AppLog.Warn("AtomicFile", $"主檔讀取失敗，改用 .bak 備份：{path}", ex);
            }

            try
            {
                string bak = path + ".bak";
                if (File.Exists(bak))
                    return File.ReadAllText(bak);
            }
            catch (Exception ex) { AppLog.Warn("AtomicFile", $".bak 備份也讀取失敗：{path}.bak", ex); }

            return null;
        }

        /// <summary>
        /// 讀檔並解析；主檔「讀不到、空白、或解析失敗」都退 .bak。都不行回 null。
        /// ReadAllTextWithFallback 只救 IO 失敗，救不了「讀得到但內容毀損」（半寫入、亂碼）這種最常見的型態。
        /// parse 解析失敗時可以拋例外或回 null，兩者都視為毀損。
        /// </summary>
        public static T? LoadWithFallback<T>(string path, Func<string, T?> parse, string logCategory) where T : class
        {
            if (TryLoad(path, parse, logCategory, out var fromMain))
                return fromMain;

            if (TryLoad(path + ".bak", parse, logCategory, out var fromBak))
            {
                if (File.Exists(path))
                    AppLog.Warn(logCategory, $"主檔毀損，已改用 .bak 上一版備份：{path}");
                return fromBak;
            }

            return null;
        }

        private static bool TryLoad<T>(string path, Func<string, T?> parse, string logCategory, out T? value) where T : class
        {
            value = null;
            try
            {
                if (!File.Exists(path))
                    return false;

                string text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                value = parse(text);
                return value != null;
            }
            catch (Exception ex)
            {
                AppLog.Warn(logCategory, $"檔案讀取或解析失敗：{path}", ex);
                value = null;
                return false;
            }
        }
    }
}
