using System;
using System.IO;
using System.Text;

namespace test
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
            catch { /* 主檔毀損 → 試 .bak */ }

            try
            {
                string bak = path + ".bak";
                if (File.Exists(bak))
                    return File.ReadAllText(bak);
            }
            catch { }

            return null;
        }
    }
}
