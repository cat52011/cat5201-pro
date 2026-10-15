using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace Cat5201
{
    // MainWindow 部分類別 — 最近刪除（刪掉的存檔留 7 天，可完整復原）
    //
    // 刪除存檔時，畫布、附件與「只屬於這個存檔」的記憶會整包搬進 _trash；
    // 全域記憶（跨專案的個人化）不跟著走。復原會把檔案搬回原位、記憶放回記憶庫。
    public partial class MainWindow
    {
        public sealed class TrashItem
        {
            public string Id { get; init; } = "";
            public string DisplayName { get; init; } = "";
            public string Detail { get; init; } = "";
        }

        public ObservableCollection<TrashItem> TrashItems { get; } = new();

        /// <summary>啟動時：清掉過期的，並決定側邊欄的「最近刪除」要不要出現。</summary>
        private void InitializeTrash()
        {
            try
            {
                Trash.PurgeExpired();
                RefreshTrashEntryPoint();
            }
            catch (Exception ex) { AppLog.Warn("Trash", "初始化最近刪除失敗", ex); }
        }

        /// <summary>沒有東西時不顯示入口——空的「最近刪除」只是雜訊。</summary>
        private void RefreshTrashEntryPoint()
        {
            if (TrashButton == null) return;

            int count = Trash.List().Count;
            TrashButton.Content = $"最近刪除（{count}）";
            TrashButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenTrash_Click(object sender, RoutedEventArgs e)
        {
            RefreshTrashList();
            if (TrashOverlay != null) TrashOverlay.Visibility = Visibility.Visible;
        }

        private void CloseTrash_Click(object sender, RoutedEventArgs e)
        {
            if (TrashOverlay != null) TrashOverlay.Visibility = Visibility.Collapsed;
        }

        private void RefreshTrashList()
        {
            TrashItems.Clear();

            var now = DateTime.UtcNow;
            foreach (var entry in Trash.List())
            {
                int daysLeft = entry.DaysLeft(now);
                string memory = entry.MemoryCount > 0 ? $"、記憶 {entry.MemoryCount} 筆" : "";

                TrashItems.Add(new TrashItem
                {
                    Id = entry.Id,
                    DisplayName = entry.DisplayName,
                    Detail = $"{entry.DeletedAtUtc.ToLocalTime():MM/dd HH:mm} 刪除{memory}　·　" +
                             (daysLeft <= 0 ? "今天內自動清除" : $"{daysLeft} 天後自動清除")
                });
            }

            if (TrashList != null) TrashList.ItemsSource = TrashItems;
            if (TrashEmptyText != null)
                TrashEmptyText.Visibility = TrashItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RestoreTrashItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TrashItem item) return;

            var result = Trash.Restore(item.Id, SavesDir, Store.AttachmentsRootDir);
            if (!result.Ok)
            {
                MenuConfirmDialog.ShowMessage(this, "復原失敗", result.Error, this);
                return;
            }

            // 記憶放回記憶庫；FileKey 跟著新的檔名走（復原時可能改名，例如原名已被佔用）。
            if (result.Memories.Count > 0 && _nodeService != null)
            {
                string key = DisplayNameFromPath(result.RestoredPath);
                _nodeService.Memories.AddRange(result.Memories.Select(m => new MemoryItem
                {
                    Id = m.Id,
                    Scope = m.Scope,
                    Category = m.Category,
                    PreferenceKey = m.PreferenceKey,
                    FileKey = key,
                    SourceNodeId = m.SourceNodeId,
                    Title = m.Title,
                    Content = m.Content,
                    CreatedAtUtc = m.CreatedAtUtc
                }));
            }

            RefreshFileList();
            RefreshTrashList();
            RefreshTrashEntryPoint();

            MenuConfirmDialog.ShowMessage(this, "已復原",
                $"「{DisplayNameFromPath(result.RestoredPath)}」已復原" +
                (result.Memories.Count > 0 ? $"，記憶 {result.Memories.Count} 筆也放回去了。" : "。"), this);
        }

        private void PurgeTrashItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TrashItem item) return;

            bool ok = MenuConfirmDialog.ShowDeleteConfirm(this, "永久刪除",
                $"「{item.DisplayName}」將被永久刪除，包含附件與該存檔的記憶。\n這個動作無法復原。", this);
            if (!ok) return;

            Trash.Purge(item.Id);
            RefreshTrashList();
            RefreshTrashEntryPoint();
        }
    }
}
