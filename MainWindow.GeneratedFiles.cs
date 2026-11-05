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
    // MainWindow 部分類別 — 資料根目錄、產出資料夾整理、記憶根目錄（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        // MVP 可攜化（Codex P0-1）：資料根目錄不再寫死 D:\。
        // 開發機：legacy 路徑存在就沿用（行為零變、既有專案原地可用）；
        // 其他機器：%LOCALAPPDATA%\cat5201-pro\file（每台 Windows 都有效，打包發佈可直接跑）。
        // 所有使用者資料（專案/附件/產出/記憶/偏好/金鑰/帳本）都從這個根長出來，改這裡=全部跟著搬。
        private static readonly string _savesDirResolved = ResolveSavesDir();
        private string SavesDir => _savesDirResolved;

        private static string ResolveSavesDir()
        {
            const string legacyDevDir = @"D:\desk\college\cat5201-pro\file";
            try
            {
                if (Directory.Exists(legacyDevDir))
                    return legacyDevDir;
            }
            catch { /* 磁碟機不存在等：直接走可攜路徑 */ }

            try
            {
                string root = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "cat5201-pro", "file");
                Directory.CreateDirectory(root);
                return root;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Startup", "LOCALAPPDATA 資料目錄建立失敗，退回程式所在目錄", ex);
                return System.IO.Path.Combine(AppContext.BaseDirectory, "file");
            }
        }
        private string AttachmentsRootDir => System.IO.Path.Combine(SavesDir, "_attachments");
        /// <summary>
        /// 使用者自己指定的產出資料夾（空＝用預設的 SavesDir\_generated）。
        ///
        /// 產出的檔案本來就不隨聊天刪除消失——它們放在所有存檔共用的 _generated，
        /// 「最近刪除」只搬畫布、附件與該存檔自己的記憶。但使用者看不出這件事，
        /// 而且會想把成品放到自己的資料夾（雲端同步、交作業的位置）。
        /// </summary>
        private string _generatedFilesDirOverride = "";

        /// <summary>
        /// 產出檔案的**根**目錄（所有存檔共用）。
        ///
        /// 跟 GeneratedFilesDir（這個存檔自己的子資料夾）分開，是因為「可以開啟 / 預覽的範圍」
        /// 必須是整個根目錄：別的存檔產的檔案、以及被刪存檔集中到的 other，都要打得開。
        /// 2026-10-15 把檔案分資料夾之後，安全檢查還拿子資料夾當根，於是除了當前存檔以外
        /// 的所有附件預覽全部被擋掉。
        /// </summary>
        private string GeneratedFilesRoot
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_generatedFilesDirOverride))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(_generatedFilesDirOverride);
                        return _generatedFilesDirOverride;
                    }
                    catch (Exception ex)
                    {
                        // 指定的位置不能用（磁碟拔掉、沒權限）→ 退回預設並留紀錄，
                        // 不要讓產出直接失敗：使用者等了幾分鐘的檔案比設定重要。
                        AppLog.Warn("Output", $"產出資料夾「{_generatedFilesDirOverride}」無法使用，這次改用預設位置", ex);
                    }
                }

                return System.IO.Path.Combine(SavesDir, "_generated");
            }
        }

        /// <summary>
        /// 找得到這個產出檔案嗎——記錄的位置不在了就在根目錄底下用檔名找一次。
        ///
        /// 檔案會被搬動（整理成各存檔子資料夾、存檔刪除後集中到 other、使用者自己改了存放位置），
        /// 但節點裡記的是當初那條絕對路徑。只認那條路徑的話，搬一次附件就全部打不開了。
        /// </summary>
        public string? ResolveGeneratedFile(string? recordedPath)
        {
            if (string.IsNullOrWhiteSpace(recordedPath))
                return null;

            if (System.IO.File.Exists(recordedPath))
                return recordedPath;

            try
            {
                string root = GeneratedFilesRoot;
                if (!System.IO.Directory.Exists(root))
                    return null;

                string name = System.IO.Path.GetFileName(recordedPath);
                var hit = System.IO.Directory
                    .EnumerateFiles(root, name, System.IO.SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (hit != null)
                    AppLog.Info("Output", $"附件已移動，改用新位置：{name}");

                return hit;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Output", $"尋找已移動的附件失敗：{System.IO.Path.GetFileName(recordedPath)}", ex);
                return null;
            }
        }

        private string GeneratedFilesDir
        {
            get
            {
                string root = GeneratedFilesRoot;

                // 分到各存檔自己的子資料夾：<根>\<存檔名>。
                //
                // 所有存檔共用一個資料夾時，幾十份簡報報告混在一起，
                // 光看檔名分不出哪一份是哪個專案的。分開之後「這個聊天產了什麼」一眼就看得到，
                // 而且仍然不隨聊天刪除消失——刪除只是把它們集中到 other。
                string saveName = SafeFolderName(CurrentSaveDisplayName());
                return saveName.Length == 0 ? root : System.IO.Path.Combine(root, saveName);
            }
        }

        /// <summary>
        /// 目前存檔的顯示名稱（沒有開啟任何存檔時為空）。
        /// 在節點的執行流程裡，指的是那個節點所屬的存檔——使用者中途切到別的存檔，產出仍寫回原本的存檔資料夾。
        /// </summary>
        private string CurrentSaveDisplayName()
        {
            try
            {
                string? path = EffectiveProjectPath;
                return string.IsNullOrWhiteSpace(path) ? "" : DisplayNameFromPath(path);
            }
            catch { return ""; }
        }

        /// <summary>存檔名拿來當資料夾名（規則在 Core，搬檔與建檔共用同一份）。</summary>
        private static string SafeFolderName(string? name) => GeneratedFileOrganizer.SafeFolderName(name);

        /// <summary>
        /// 一次性整理：把散在 _generated 底下的舊檔案搬進各自的存檔資料夾。
        ///
        /// 哪個檔案屬於哪個聊天，問存檔自己——每個節點都記著它產出的檔案路徑。
        /// 認領不到的（存檔已刪、或更早的版本留下來的）集中到 other，不丟掉：
        /// 那些是使用者花錢跑出來的成品。
        /// </summary>
        private void OrganizeGeneratedFilesOnce()
        {
            try
            {
                string root = System.IO.Path.Combine(SavesDir, "_generated");
                if (!System.IO.Directory.Exists(root))
                    return;

                // 先看有沒有東西要搬，沒有就不要白跑（每次啟動都會呼叫）。
                if (System.IO.Directory.GetFiles(root).Length == 0)
                    return;

                var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (string save in System.IO.Directory.GetFiles(SavesDir, "*.json"))
                {
                    string display = DisplayNameFromPath(save);
                    try
                    {
                        // 只要 Nodes[].OutputFilePaths 這一個欄位，直接讀 JSON 比反序列化整個專案便宜很多。
                        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(save));
                        if (!doc.RootElement.TryGetProperty("Nodes", out var nodes)) continue;

                        foreach (var node in nodes.EnumerateArray())
                        {
                            if (!node.TryGetProperty("OutputFilePaths", out var files)) continue;
                            foreach (var f in files.EnumerateArray())
                                if (f.GetString() is string fp && fp.Length > 0)
                                    owner[System.IO.Path.GetFileName(fp)] = display;
                        }
                    }
                    catch { /* 單一存檔讀不開不影響其他的 */ }
                }

                int moved = GeneratedFileOrganizer.Organize(root, owner);
                if (moved > 0)
                {
                    AppLog.Info("Output", $"產出檔案整理完成：{moved} 個檔案已歸到各自的存檔資料夾");

                    // 搬完一定要把存檔裡記的路徑改掉，否則所有附件 chip 都會指到空的位置
                    //（2026-10-15 我就是漏了這一步，使用者的附件預覽全部不見）。
                    RepairGeneratedFilePaths(root);
                }
            }
            catch (Exception ex) { AppLog.Warn("Output", "整理產出檔案失敗", ex); }
        }

        /// <summary>
        /// 啟動時：改過名的存檔，產出資料夾對齊目前的存檔名稱，並收掉完全空的產出資料夾（規則見 GeneratedFileOrganizer.AlignSaveFolders）。
        /// 2026-11-04：改名跟著搬資料夾之前就改過名的聊天，檔案還留在舊名稱的資料夾。
        /// 已經對齊時什麼都不做；在開啟存檔之前執行，所以只要改寫存檔 JSON，不用動畫面上的節點。
        /// </summary>
        private void AlignGeneratedFoldersWithSaveNames()
        {
            try
            {
                string root = GeneratedFilesRoot;
                if (!System.IO.Directory.Exists(root))
                    return;

                var saves = new List<GeneratedFileOrganizer.SaveOutputs>();
                var pathBySave = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string save in System.IO.Directory.GetFiles(SavesDir, "*.json"))
                {
                    string display = DisplayNameFromPath(save);
                    var paths = new List<string>();
                    try
                    {
                        // 只要 Nodes[].OutputFilePaths 與 OutputImagePath，直接讀 JSON 比反序列化整個專案便宜。
                        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(save));
                        if (doc.RootElement.TryGetProperty("Nodes", out var nodes) && nodes.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var node in nodes.EnumerateArray())
                            {
                                if (node.TryGetProperty("OutputFilePaths", out var files) && files.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    foreach (var f in files.EnumerateArray())
                                        if (f.ValueKind == System.Text.Json.JsonValueKind.String && f.GetString() is { Length: > 0 } fp)
                                            paths.Add(fp);
                                if (node.TryGetProperty("OutputImagePath", out var img) && img.ValueKind == System.Text.Json.JsonValueKind.String &&
                                    img.GetString() is { Length: > 0 } ip)
                                    paths.Add(ip);
                            }
                    }
                    catch { paths.Clear(); /* 讀不開的存檔不搬，但名稱仍算數：名稱同它的資料夾不會被別的存檔搬走 */ }

                    saves.Add(new GeneratedFileOrganizer.SaveOutputs(display, paths));
                    pathBySave[display] = save;
                }

                foreach (var r in GeneratedFileOrganizer.AlignSaveFolders(root, saves))
                {
                    if (pathBySave.TryGetValue(r.SaveName, out var savePath))
                        RewriteOutputPathsInSave(savePath, r.Map);
                    AppLog.Info("Output", $"「{r.SaveName}」的產出檔案從「{r.FromFolder}」移到「{r.ToFolder}」（{r.Map.Count} 個檔案）");
                }
            }
            catch (Exception ex) { AppLog.Warn("Output", "對齊產出資料夾名稱失敗（檔案沒有搬動）", ex); }
        }

        /// <summary>搬動之後，把每個存檔裡記的產出路徑指回新位置。</summary>
        private void RepairGeneratedFilePaths(string root)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in System.IO.Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories))
                index.TryAdd(System.IO.Path.GetFileName(file), file);

            foreach (string save in System.IO.Directory.GetFiles(SavesDir, "*.json"))
            {
                try
                {
                    string raw = System.IO.File.ReadAllText(save);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(raw);
                    if (node?["Nodes"] is not System.Text.Json.Nodes.JsonArray nodes) continue;

                    bool changed = false;
                    foreach (var n in nodes)
                    {
                        if (n?["OutputFilePaths"] is not System.Text.Json.Nodes.JsonArray paths) continue;

                        for (int i = 0; i < paths.Count; i++)
                        {
                            string? p = paths[i]?.GetValue<string>();
                            if (string.IsNullOrWhiteSpace(p) || System.IO.File.Exists(p)) continue;

                            if (index.TryGetValue(System.IO.Path.GetFileName(p), out string? target))
                            {
                                paths[i] = target;
                                changed = true;
                            }
                        }
                    }

                    if (changed)
                        AtomicFile.WriteAllText(save, node!.ToJsonString(
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Output", $"修正 {System.IO.Path.GetFileName(save)} 的附件路徑失敗", ex);
                }
            }
        }

        public string GetGeneratedFilesDir() => GeneratedFilesDir;

        /// <summary>產出檔案的根目錄（所有存檔共用）。節點清理上一輪產出時，以它為界收空資料夾。</summary>
        public string GetGeneratedFilesRootDir() => GeneratedFilesRoot;

        /// <summary>目前設定的產出資料夾（給個人化面板顯示）。</summary>
        public string GetGeneratedFilesDirSetting() => _generatedFilesDirOverride;

        private void SetGeneratedFilesDir(string? path)
        {
            _generatedFilesDirOverride = (path ?? "").Trim();
            RefreshOutputDirLabel();
            SavePreferences();
            AppLog.Info("Output", $"產出資料夾設為：{GeneratedFilesDir}");
        }

        private void RefreshOutputDirLabel()
        {
            if (OutputDirText != null)
                OutputDirText.Text = GeneratedFilesDir;
        }

        private void ChangeOutputDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = UiLanguage.P("選擇產出檔案要存到哪裡", "Choose where to save output files"),
                InitialDirectory = GeneratedFilesDir,
            };

            if (dlg.ShowDialog(this) == true)
                SetGeneratedFilesDir(dlg.FolderName);
        }

        private void ResetOutputDir_Click(object sender, RoutedEventArgs e) => SetGeneratedFilesDir("");

        private void OpenOutputDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = GeneratedFilesDir;
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex) { AppLog.Warn("Output", "開啟產出資料夾失敗", ex); }
        }

        // pro 記憶根目錄＝pro 自己的 SavesDir。歷史上 NodeService 誤把 MemoryStore 指向畢業版旁的
        // D:\desk\college\final\file，造成 pro 與畢業版記憶混用（Codex P0-2）。已改回 pro 專屬，
        // 並在首次存取時把舊記憶「複製」過來（畢業版原檔只讀不動，維持「絕不碰畢業版」原則）。
        private const string LegacyMemoryBaseDir = @"D:\desk\college\final\file";
        private bool _legacyMemoryMigrationDone;

        public string GetMemoryBaseDir()
        {
            MigrateLegacyMemoryIfNeeded();
            return SavesDir;
        }

        private void MigrateLegacyMemoryIfNeeded()
        {
            if (_legacyMemoryMigrationDone)
                return;
            _legacyMemoryMigrationDone = true;

            try
            {
                var newFile = System.IO.Path.Combine(SavesDir, "_memory", "memory_store.json");
                if (File.Exists(newFile))
                    return; // pro 已有自己的記憶，不覆蓋

                var legacyFile = System.IO.Path.Combine(LegacyMemoryBaseDir, "_memory", "memory_store.json");
                if (!File.Exists(legacyFile))
                    return; // 沒有舊記憶可搬

                Directory.CreateDirectory(System.IO.Path.Combine(SavesDir, "_memory"));
                File.Copy(legacyFile, newFile, overwrite: false); // 複製而非搬移：畢業版原檔保留
                AppLog.Info("Memory", "已將舊記憶從畢業版資料夾複製到 pro 專屬記憶（畢業版原檔保留）。");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Memory", "舊記憶遷移失敗（將以現有/空白記憶啟動）", ex);
            }
        }
    }
}
