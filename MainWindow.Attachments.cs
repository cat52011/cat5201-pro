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
    // MainWindow 部分類別 — 節點附件與產出檔開啟（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        private string GetAttachmentFolderForFile(string filePath)
            => Store.GetAttachmentFolder(filePath);

        private string? GetCurrentAttachmentFolder()
        {
            if (string.IsNullOrWhiteSpace(_currentFilePath)) return null;
            var folder = GetAttachmentFolderForFile(_currentFilePath);
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static bool IsImageExt(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
        }

        private static string MimeFromExt(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".md" => "text/markdown",
                ".csv" => "text/csv",
                ".json" => "application/json",
                _ => "application/octet-stream"
            };
        }

        public void AddAttachmentsForNode(NodeControl node, IEnumerable<string> filePaths)
        {
            var folder = GetCurrentAttachmentFolder();
            if (folder == null)
            {
                MenuConfirmDialog.ShowMessage(this, UiLanguage.P("提示", "Notice"), UiLanguage.P("目前尚未建立檔案，無法上傳附件。", "No project file has been created yet, so attachments can't be uploaded."), this);
                return;
            }

            if (!_attachmentsByNode.TryGetValue(node.Id, out var list))
            {
                list = new List<AttachmentInfo>();
                _attachmentsByNode[node.Id] = list;
            }

            foreach (var src in filePaths ?? Array.Empty<string>())
            {
                try
                {
                    if (!File.Exists(src)) continue;

                    var ext = System.IO.Path.GetExtension(src);
                    var mime = MimeFromExt(ext);
                    var kind = IsImageExt(ext) ? "image"
                        : ext.ToLowerInvariant() == ".pdf" ? "pdf"
                        : ext.ToLowerInvariant() is ".html" or ".htm" ? "html"
                        : "file";

                    var safeName = System.IO.Path.GetFileName(src);
                    var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
                    var dest = System.IO.Path.Combine(folder, uniqueName);

                    File.Copy(src, dest, overwrite: false);

                    var rel = System.IO.Path.Combine(DisplayNameFromPath(_currentFilePath!), uniqueName);

                    list.Add(new AttachmentInfo
                    {
                        FileName = safeName,
                        RelativePath = rel,
                        MimeType = mime,
                        Kind = kind
                    });
                }
                catch (Exception ex) { AppLog.Warn("Attachments", "附件複製進專案失敗（該附件被略過）", ex); }
            }

            SaveState();
        }

        public IReadOnlyList<AttachmentInfo> GetAttachmentsForNode(INodeContext node)
        {
            if (node == null)
                return Array.Empty<AttachmentInfo>();

            if (_attachmentsByNode.TryGetValue(node.Id, out var list))
                return list.ToList();

            return Array.Empty<AttachmentInfo>();
        }

        // 執行用「有效附件」＝此節點自己的附件 + 沿上游鏈所有祖先的附件（按 RelativePath 去重）。
        // 為什麼：附件按 node.Id 隔離儲存，使用者通常只在源頭節點掛檔案；下游節點（{{input}} 只帶上游
        // 輸出文字）原本拿不到上游附件。工作流鏈執行時，下游應「繼承」上游掛的檔案，才看得到原始輸入。
        // 順序：最遠祖先在前、自己在最後，讓 LLM 把上游附件當輸入材料。visited 防環、多層鏈安全。
        public IReadOnlyList<AttachmentInfo> GetEffectiveAttachmentsForNode(INodeContext nodeContext)
        {
            // Slice B1 cast 橋：介面契約收 INodeContext，但上游鏈走訪需要具體控制項（runtime 流動的本來就是 NodeControl）。
            var node = nodeContext as NodeControl;
            if (node == null)
                return Array.Empty<AttachmentInfo>();

            var chain = new List<NodeControl>();
            var visited = new HashSet<Guid>();
            var up = GetFirstUpstreamNode(node);
            while (up != null && visited.Add(up.Id))
            {
                chain.Add(up);
                up = GetFirstUpstreamNode(up);
            }
            chain.Reverse();   // 最遠祖先排前面
            chain.Add(node);   // 自己最後

            var ordered = new List<AttachmentInfo>();
            var seenRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in chain)
            {
                foreach (var a in GetAttachmentsForNode(n))
                {
                    // RelativePath 非空且已收錄過 → 跳過重複；空路徑不去重，照收。
                    if (!string.IsNullOrEmpty(a.RelativePath) && !seenRel.Add(a.RelativePath))
                        continue;
                    ordered.Add(a);
                }
            }

            return ordered;
        }

        // 「同一條有向鏈」的節點 ID 集合＝此節點 + 所有上游祖先 + 所有下游後代（沿連線方向）。
        // 用途：記憶召回的跨鏈隔離——畫布上不同分支（兄弟/旁系，例如另一個掛附件的節點）的執行記憶
        // 不應互相注入污染；只有同一條鏈上的節點才彼此「詳細可見」，旁支只靠 NodeContextService 的
        // 「支線摘要」大概知道。沿有向邊 BFS，所以共同母節點的不同子分支彼此不在對方的鏈集合內。
        public HashSet<string> GetSameChainNodeIds(NodeControl node)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node == null)
                return ids;

            var conns = GetConnectionsForContext().ToList();
            ids.Add(node.Id.ToString());

            // 祖先：沿入邊（EndNode==cursor → 取 StartNode）向上 BFS。
            var visited = new HashSet<Guid> { node.Id };
            var queue = new Queue<NodeControl>();
            queue.Enqueue(node);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var c in conns)
                {
                    if (ReferenceEquals(c.EndNode, cur) && c.StartNode != null && visited.Add(c.StartNode.Id))
                    {
                        ids.Add(c.StartNode.Id.ToString());
                        queue.Enqueue(c.StartNode);
                    }
                }
            }

            // 後代：沿出邊（StartNode==cursor → 取 EndNode）向下 BFS。
            visited = new HashSet<Guid> { node.Id };
            queue.Enqueue(node);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var c in conns)
                {
                    if (ReferenceEquals(c.StartNode, cur) && c.EndNode != null && visited.Add(c.EndNode.Id))
                    {
                        ids.Add(c.EndNode.Id.ToString());
                        queue.Enqueue(c.EndNode);
                    }
                }
            }

            return ids;
        }

        public void OpenAttachment(string relativePath)
        {
            try
            {
                var abs = System.IO.Path.Combine(AttachmentsRootDir, relativePath);
                if (!File.Exists(abs))
                {
                    MenuConfirmDialog.ShowMessage(this, UiLanguage.P("開啟失敗", "Couldn't open"), UiLanguage.P("找不到附件檔案（可能已被移動或刪除）。", "Attachment not found (it may have been moved or deleted)."), this);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = abs,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, UiLanguage.P("錯誤", "Error"), UiLanguage.P($"開啟附件失敗：{ex.Message}", $"Couldn't open attachment: {ex.Message}"), this);
            }
        }

        /// <summary>用系統預設程式開啟生成的檔案（報告 / 簡報 deck）。限制只能開啟 _generated 資料夾內的檔案。</summary>
        public void OpenGeneratedFile(string fullPath)
        {
            try
            {
                fullPath = ResolveGeneratedFile(fullPath) ?? "";
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                {
                    MenuConfirmDialog.ShowMessage(this, UiLanguage.P("開啟失敗", "Couldn't open"), UiLanguage.P("找不到生成的檔案（可能已被刪除）。", "Generated file not found (it may have been deleted)."), this);
                    return;
                }

                // 安全檢查：整個產出根目錄都算合法——別的存檔的檔案、以及 other 裡的，都要打得開。
                string rootFull = System.IO.Path.GetFullPath(GeneratedFilesRoot);
                string targetFull = System.IO.Path.GetFullPath(fullPath);
                if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    MenuConfirmDialog.ShowMessage(this, UiLanguage.P("開啟失敗", "Couldn't open"), UiLanguage.P("檔案不在允許開啟的範圍內。", "This file is outside the folders the app is allowed to open."), this);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = targetFull,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, UiLanguage.P("錯誤", "Error"), UiLanguage.P($"開啟檔案失敗：{ex.Message}", $"Couldn't open file: {ex.Message}"), this);
            }
        }

        // ===== Artifact 即時預覽（大型 overlay；點檔案 chip / 圖片開啟）=====























        public void RemoveAttachment(NodeControl node, string relativePath)
        {
            if (!_attachmentsByNode.TryGetValue(node.Id, out var list))
                return;

            var hit = list.FirstOrDefault(a => string.Equals(a.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return;

            try
            {
                var abs = System.IO.Path.Combine(AttachmentsRootDir, hit.RelativePath);
                if (File.Exists(abs))
                {
                    File.Delete(abs);
                }
            }
            catch (Exception ex) { AppLog.Warn("Attachments", "附件實體檔刪除失敗（清單已移除，檔案殘留）", ex); }

            list.Remove(hit);
            if (list.Count == 0)
                _attachmentsByNode.Remove(node.Id);

            SaveState();
        }

        // 附件相對路徑改寫的磁碟版已在 ProjectStore;這裡只剩「已載入節點」的記憶體同步。
        private void UpdateAttachmentRelativePathsInMemory(string oldBaseName, string newBaseName)
        {
            // 只改目前畫面上的節點：表裡也有背景執行中存檔的節點，它們的附件屬於別的存檔名稱。
            var onCanvas = MainCanvas.Children.OfType<NodeControl>().Select(n => n.Id).ToHashSet();
            foreach (var kv in _attachmentsByNode.Where(kv => onCanvas.Contains(kv.Key)))
            {
                foreach (var a in kv.Value)
                {
                    a.RelativePath = ProjectStore.ReplaceAttachmentRelativeBase(a.RelativePath, oldBaseName, newBaseName);
                }
            }
        }
    }
}
