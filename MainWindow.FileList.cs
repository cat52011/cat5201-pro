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
    // MainWindow 部分類別 — 側邊欄存檔清單、新增 / 刪除 / 就地改名（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        private void NewFile_Click(object sender, RoutedEventArgs e)
        {
            // 目前的存檔有節點在跑：留在背景繼續（見 MainWindow.Sessions.cs）。
            StashActiveProjectIfRunning();

            StartUI.Visibility = Visibility.Visible;
            MainUI.Visibility = Visibility.Collapsed;
            SetRandomStartMessage();

            _hasStarted = false;
            _currentFilePath = null;
            RefreshCurrentFileLabel();
            ClearAll();

            _fileNameLockedByUser = false;
            _lastAppliedAutoKeyword = "";
            _lastInitialTopSnapshot = "";
            ClearPerNodeStateExceptBackground();

            _editingNode = null;
            _editingReason = EditReason.None;

            // Auto / 進階解析是使用者的個人化設定，不隨「開新存檔」重設——
            // 他上次明明開著，建一個新檔就被偷偷關掉，看起來就像程式自己把模式改掉了。
            // 這裡只重新同步 UI 反映目前（全域）的設定。
            SyncAutoModeSwitches();
        }

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is FileItem item && File.Exists(item.FullPath))
            {
                LoadState(item.FullPath);
            }
        }

        private void FileList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TrySelectItemUnderMouse(FileList, e.GetPosition(FileList), out var item) == false)
                return;

            if (item == null || !File.Exists(item.FullPath))
                return;

            var cmStyle = (Style)FindResource("FileContextMenuStyle");
            var miStyle = (Style)FindResource("FileMenuItemStyle");
            var sepStyle = (Style)FindResource("FileSeparatorStyle");

            var menu = new ContextMenu
            {
                Style = cmStyle,
                PlacementTarget = FileList
            };

            var miRename = new MenuItem { Header = UiLanguage.P("重新命名", "Rename"), Style = miStyle };
            miRename.Click += (_, __) => BeginInlineRename(item);

            var miDelete = new MenuItem { Header = UiLanguage.P("刪除", "Delete"), Style = miStyle };
            miDelete.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F")!);
            miDelete.Click += (_, __) => DeleteFile(item.FullPath);

            var sep = new Separator { Style = sepStyle };

            menu.Items.Add(miRename);
            menu.Items.Add(sep);
            menu.Items.Add(miDelete);

            menu.IsOpen = true;
            e.Handled = true;
        }

        private static bool TrySelectItemUnderMouse(ListBox listBox, Point pos, out FileItem? selectedItem)
        {
            selectedItem = null;

            var element = listBox.InputHitTest(pos) as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = VisualTreeHelper.GetParent(element);

            if (element is not ListBoxItem item)
                return false;

            listBox.SelectedItem = item.DataContext;
            selectedItem = listBox.SelectedItem as FileItem;
            return selectedItem != null;
        }

        private void DeleteFile(string path)
        {
            var name = DisplayNameFromPath(path);

            // 有節點在跑的存檔（目前畫面或背景）不能刪：刪了執行中的節點就沒有地方寫回結果。
            if (IsProjectRunning(path))
            {
                MenuConfirmDialog.ShowMessage(this, UiLanguage.P("還在執行", "Still running"),
                    UiLanguage.P($"「{name}」還有節點在執行，等執行完再刪除。", $"\"{name}\" still has nodes running. Delete it after they finish."), this);
                return;
            }

            // 只問一句。原本那兩行說明（會搬到「最近刪除」、全域記憶不受影響）是寫給
            // 「怕按下去就沒了」的人看的，但它每一次刪檔都出現，久了就是雜訊——
            // 真正想知道保留規則的人會去「最近刪除」那一頁看。
            bool ok = MenuConfirmDialog.ShowDeleteConfirm(
                owner: this,
                title: UiLanguage.P("刪除確認", "Confirm delete"),
                message: UiLanguage.P($"要刪除「{name}」嗎？", $"Delete \"{name}\"?"),
                resourceHost: this);

            if (!ok) return;

            try
            {
                // 產出檔案不跟著刪——那是使用者花錢跑出來的成品。
                // 但它的資料夾沒有主人了，所以集中到 _generated\other。
                GeneratedFileOrganizer.MoveToOther(
                    System.IO.Path.Combine(SavesDir, "_generated"), DisplayNameFromPath(path));

                // 存檔自己的記憶跟著走；全域記憶留著。復原時會原樣放回記憶庫。
                var memories = _nodeService?.Memories.TakeByFileKey(DisplayNameFromPath(path));
                Trash.MoveToTrash(path, Store.GetAttachmentFolder(path), memories?.ToList());
                RefreshTrashEntryPoint();

                if (!string.IsNullOrEmpty(_currentFilePath) &&
                    string.Equals(_currentFilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFilePath = null;
                    _hasStarted = false;
                    RefreshCurrentFileLabel();
                    _fileNameLockedByUser = false;
                    _lastAppliedAutoKeyword = "";
                    _lastInitialTopSnapshot = "";
                    _attachmentsByNode.Clear();
                    _nodeModelsById.Clear();
                    _nodeTaskModesById.Clear();
                    // 同上：刪掉目前存檔不代表使用者要換回手動模式。
                    SyncAutoModeSwitches();
                }

                RefreshFileList();
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, UiLanguage.P("錯誤", "Error"), UiLanguage.P($"刪除失敗：{ex.Message}", $"Delete failed: {ex.Message}"), this);
            }
        }

        // ===== 就地改名（右鍵→重新命名）：名稱原地變輸入框，Enter 確認 / Esc 取消 / 失焦確認 =====

        private void BeginInlineRename(FileItem item)
        {
            if (item == null)
                return;

            // 背景執行中的存檔不能改名：它的節點設定裡記的附件路徑還是舊名稱，執行完再改才安全。
            if (SessionFor(item.FullPath) != null)
            {
                MenuConfirmDialog.ShowMessage(this, UiLanguage.P("還在執行", "Still running"),
                    UiLanguage.P($"「{item.DisplayName}」還有節點在背景執行，等執行完再改名。", $"\"{item.DisplayName}\" still has nodes running in the background. Rename it after they finish."), this);
                return;
            }

            // 一次只編輯一個：先收掉其他項的編輯狀態。
            if (FileList.ItemsSource is IEnumerable<FileItem> list)
            {
                foreach (var other in list)
                    other.IsEditing = false;
            }

            item.EditText = item.DisplayName;
            item.IsEditing = true;
        }

        private void FileRenameEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // DataTrigger 切 Visible 時把焦點與全選帶進輸入框（Loaded 只發一次，不能用）。
            if (sender is TextBox tb && e.NewValue is bool visible && visible)
            {
                tb.Dispatcher.BeginInvoke(new Action(() =>
                {
                    tb.Focus();
                    tb.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void FileRenameEditor_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb)
                return;

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitInlineRename(tb);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (tb.DataContext is FileItem item)
                    item.IsEditing = false; // 丟棄輸入，回顯示狀態
            }
        }

        private void FileRenameEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                CommitInlineRename(tb); // 失焦＝確認（ChatGPT 行為）；內部有 IsEditing guard 防重入
        }

        private void CommitInlineRename(TextBox tb)
        {
            if (tb.DataContext is not FileItem item || !item.IsEditing)
                return;

            item.IsEditing = false; // 先收編輯狀態（Enter 後的 LostFocus 不會重複提交）

            string newName = (item.EditText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, item.DisplayName, StringComparison.Ordinal))
                return; // 空白或沒改 → 視為取消

            PerformRename(item.FullPath, newName);
        }

        private void PerformRename(string oldPath, string requestedName)
        {
            var newName = NormalizeKeywordForFileName(requestedName);
            if (string.IsNullOrWhiteSpace(newName))
                return;

            try
            {
                var oldBaseName = DisplayNameFromPath(oldPath);

                // 磁碟事務（Move+.bak+附件資料夾+路徑改寫+鎖定旗標+失敗回滾）整包在 ProjectStore。
                var result = Store.Rename(oldPath, newName);
                if (!result.Ok)
                {
                    if (!string.IsNullOrWhiteSpace(result.Error))
                        MenuConfirmDialog.ShowMessage(this, UiLanguage.P("重新命名失敗", "Rename failed"), result.Error, this);
                    return;
                }

                bool isCurrent = !string.IsNullOrEmpty(_currentFilePath) &&
                                 string.Equals(_currentFilePath, oldPath, StringComparison.OrdinalIgnoreCase);

                // 產出資料夾跟著改名（目前開著的存檔：改節點記的路徑，下面 SaveState 存回；其他存檔：直接改它的 JSON）。
                MoveGeneratedFolderForRename(oldBaseName, DisplayNameFromPath(result.NewPath), isCurrent ? null : result.NewPath);

                if (isCurrent)
                {
                    _currentFilePath = result.NewPath;
                    _fileNameLockedByUser = true;

                    UpdateAttachmentRelativePathsInMemory(oldBaseName, DisplayNameFromPath(result.NewPath));
                    RefreshAllNodeAttachmentUIs();

                    RefreshCurrentFileLabel();

                    SaveState();
                }

                RefreshFileList();
                SelectFileInList(result.NewPath);
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, UiLanguage.P("錯誤", "Error"), UiLanguage.P($"重新命名失敗：{ex.Message}", $"Rename failed: {ex.Message}"), this);
            }
        }

        // MoveAttachmentFolderSafely / .bak sidecar / 附件路徑改寫 → 已抽出到 ProjectStore（第 1 刀）。

        /// <summary>
        /// 存檔改名（手動或自動命名）後，產出資料夾跟著改名，並把記的路徑改到新位置。
        /// otherSavePath＝null：改的是目前開著的存檔，更新畫面上各節點記的路徑（呼叫端接著 SaveState 存回）；
        /// 否則直接改那個存檔的 JSON。
        /// 2026-11-04：原本改名不搬產出資料夾，同一個聊天的檔案分散在新舊兩個名稱的資料夾。
        /// 搬移失敗不影響改名本身：檔案留在原處，預覽仍可用檔名找到（ResolveGeneratedFile）。
        /// </summary>
        private void MoveGeneratedFolderForRename(string oldBaseName, string newBaseName, string? otherSavePath)
        {
            IReadOnlyDictionary<string, string> map;
            try { map = GeneratedFileOrganizer.RenameSaveFolder(GeneratedFilesRoot, oldBaseName, newBaseName); }
            catch (Exception ex)
            {
                AppLog.Warn("Output", $"產出資料夾沒有跟著改名：{oldBaseName} → {newBaseName}", ex);
                return;
            }
            if (map.Count == 0)
                return;

            if (otherSavePath != null)
            {
                RewriteOutputPathsInSave(otherSavePath, map);
                return;
            }

            foreach (var n in MainCanvas.Children.OfType<NodeControl>())
            {
                var paths = n.GetOutputFilePaths();
                var remapped = paths.Select(p => GeneratedFileOrganizer.Remap(p, map)).ToList();
                if (!paths.SequenceEqual(remapped, StringComparer.Ordinal))
                    n.RestoreOutputFiles(remapped);

                string? image = n.GetOutputImagePath();
                string newImage = GeneratedFileOrganizer.Remap(image, map);
                if (!string.IsNullOrEmpty(image) && !string.Equals(image, newImage, StringComparison.Ordinal))
                    n.SetOutputImage(newImage);
            }
        }

        /// <summary>把某個存檔 JSON 裡各節點的產出路徑（OutputFilePaths、OutputImagePath）依對照改到新位置。</summary>
        private static void RewriteOutputPathsInSave(string savePath, IReadOnlyDictionary<string, string> map)
        {
            try
            {
                if (!System.IO.File.Exists(savePath)) return;
                var root = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(savePath));
                if (root?["Nodes"] is not System.Text.Json.Nodes.JsonArray nodes) return;

                bool changed = false;
                foreach (var n in nodes)
                {
                    if (n?["OutputFilePaths"] is System.Text.Json.Nodes.JsonArray paths)
                        for (int i = 0; i < paths.Count; i++)
                        {
                            string? p = paths[i]?.GetValue<string>();
                            string q = GeneratedFileOrganizer.Remap(p, map);
                            if (p != null && q != p) { paths[i] = q; changed = true; }
                        }

                    if (n?["OutputImagePath"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var img))
                    {
                        string q = GeneratedFileOrganizer.Remap(img, map);
                        if (q != img) { n["OutputImagePath"] = q; changed = true; }
                    }
                }

                if (changed)
                    AtomicFile.WriteAllText(savePath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Output", $"改名後更新 {System.IO.Path.GetFileName(savePath)} 的產出路徑失敗", ex);
            }
        }

        private void RefreshAllNodeAttachmentUIs()
        {
            foreach (var node in MainCanvas.Children.OfType<NodeControl>())
            {
                node.RefreshAttachmentsUI();
            }
        }

        private void SelectFileInList(string fullPath)
        {
            if (FileList.ItemsSource is not IEnumerable<FileItem> list) return;

            var target = list.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (target == null) return;

            FileList.SelectedItem = target;
            FileList.ScrollIntoView(target);
        }
    }
}
