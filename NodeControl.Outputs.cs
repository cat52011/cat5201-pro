using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Cat5201
{
    // NodeControl 部分類別 — 附件、產出檔案、簡報與輸出圖片（自 NodeControl.xaml.cs 原樣搬出）
    public partial class NodeControl
    {
        internal void RefreshAttachmentsUI()
        {
            if (_parent == null)
            {
                _attachments.Clear();

                if (AttachmentListHost != null)
                    AttachmentListHost.Visibility = Visibility.Collapsed;

                if (ModelSelector != null &&
                    AttachmentColumn != null &&
                    AttachmentSplitterColumn != null &&
                    ModelColumn != null)
                {
                    AttachmentColumn.Width = new GridLength(0);
                    AttachmentSplitterColumn.Width = new GridLength(0);
                    ModelColumn.Width = new GridLength(1, GridUnitType.Star);

                    Grid.SetColumn(ModelSelector, 0);
                    Grid.SetColumnSpan(ModelSelector, 3);
                }

                return;
            }

            var list = _parent.GetAttachmentsForNode(this);

            _attachments.Clear();
            foreach (var a in list)
            {
                _attachments.Add(new AttachmentVm
                {
                    FileName = a.FileName,
                    RelativePath = a.RelativePath,
                    Kind = a.Kind
                });
            }

            if (AttachmentListHost != null &&
                ModelSelector != null &&
                AttachmentColumn != null &&
                AttachmentSplitterColumn != null &&
                ModelColumn != null)
            {
                bool hasAttachments = _attachments.Count > 0;

                if (hasAttachments)
                {
                    AttachmentListHost.Visibility = Visibility.Visible;

                    AttachmentColumn.Width = new GridLength(68, GridUnitType.Star);
                    AttachmentSplitterColumn.Width = new GridLength(2);
                    ModelColumn.Width = new GridLength(32, GridUnitType.Star);

                    Grid.SetColumn(ModelSelector, 2);
                    Grid.SetColumnSpan(ModelSelector, 1);
                }
                else
                {
                    AttachmentListHost.Visibility = Visibility.Collapsed;

                    AttachmentColumn.Width = new GridLength(0);
                    AttachmentSplitterColumn.Width = new GridLength(0);
                    ModelColumn.Width = new GridLength(1, GridUnitType.Star);

                    Grid.SetColumn(ModelSelector, 0);
                    Grid.SetColumnSpan(ModelSelector, 3);
                }
            }

            if (AttachmentScroll != null)
                AttachmentScroll.ScrollToHorizontalOffset(0);
        }

        private void AttachmentItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent == null) return;
            if (_isAttachmentDragging) return;

            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement fe && fe.Tag is AttachmentVm vm)
                {
                    _parent.OpenAttachment(vm.RelativePath);
                    e.Handled = true;
                }
            }
        }

        private void OutputFileItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent == null) return;

            if (sender is FrameworkElement fe && fe.Tag is OutputFileVm vm)
            {
                _parent.OpenPreview(vm.FullPath, this);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 清掉輸出檔案連結，並刪除磁碟上對應的實體檔案。
        /// 重跑 / 清除輸出文字 / 刪除節點時呼叫。可從背景執行緒呼叫。
        /// </summary>
        public void ClearOutputFiles()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearOutputFiles);
                return;
            }

            foreach (var path in _pendingFilePaths)
            {
                try
                {
                    if (System.IO.File.Exists(path))
                        System.IO.File.Delete(path);
                }
                catch (Exception ex) { AppLog.Warn("Node", $"暫存檔清理失敗（磁碟會殘檔）：{path}", ex); }
            }

            // 檔案刪光後把空資料夾一起收掉（程式碼檔在每次執行自己的子資料夾裡，報告在聊天資料夾裡）。
            // 往上最多收到產出「根目錄」為止，不會碰到它本身或更上層。
            // 2026-11-04：原本以「目前聊天名稱」的資料夾為界、而且只收程式碼資料夾——聊天在兩輪之間改過名的話，
            // 舊名稱的資料夾不在界內，刪光後留下空殼（使用者看到好幾個點進去是空的資料夾）。
            string? genRoot = null;
            try { genRoot = _parent == null ? null : System.IO.Path.GetFullPath(_parent.GetGeneratedFilesRootDir()); }
            catch { genRoot = null; }
            if (genRoot != null)
            {
                var dirs = _pendingFilePaths
                    .Select(System.IO.Path.GetDirectoryName)
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 程式資料夾裡的「修正前」（審查後自動修正留下的上一版）屬於同一輪產出，跟著清掉；
                // 它沒有列在輸出 chip 裡，不清的話會讓整個程式資料夾一直刪不掉。
                foreach (var dir in _pendingFilePaths
                             .Where(p => OutputFileCategory.Of(p).Label is "程式碼")
                             .Select(System.IO.Path.GetDirectoryName)
                             .Where(d => !string.IsNullOrEmpty(d))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!System.IO.Directory.Exists(dir)) continue;
                        foreach (var archive in System.IO.Directory.GetDirectories(dir!, ReviewAutoFix.ArchiveFolderName + "*"))
                        {
                            string name = System.IO.Path.GetFileName(archive);
                            if (name == ReviewAutoFix.ArchiveFolderName ||
                                System.Text.RegularExpressions.Regex.IsMatch(name, "^" + ReviewAutoFix.ArchiveFolderName + @"_\d+$"))
                                System.IO.Directory.Delete(archive, recursive: true);
                        }
                    }
                    catch (Exception ex) { AppLog.Warn("Node", $"清理修正前的檔案失敗：{dir}", ex); }
                }

                foreach (var dir in dirs)
                    GeneratedFileOrganizer.RemoveEmptyDirs(dir, genRoot);
            }

            _pendingFilePaths.Clear();

            _outputFiles.Clear();
            if (OutputFileHost != null)
                OutputFileHost.Visibility = Visibility.Collapsed;

            _outputImagePath = null;
            if (OutputImage != null)
                OutputImage.Source = null;
            if (OutputImageHost != null)
                OutputImageHost.Visibility = Visibility.Collapsed;
        }

        /// <summary>設定本次執行產生、可在輸出區點擊開啟的檔案。可從背景執行緒呼叫。</summary>
        public void SetOutputFiles(IReadOnlyList<GeneratedFilePayload> files)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetOutputFiles(files));
                return;
            }

            _outputFiles.Clear();
            _pendingFilePaths.Clear();

            if (files != null)
            {
                // 同類的放在一起：簡報、報告、表格、PDF、程式碼、圖片…（程式碼與文件不再混在一起）。
                foreach (var f in OutputFileCategory.Sort(files, x => x?.FileName))
                {
                    if (f == null || !f.Success || string.IsNullOrWhiteSpace(f.FilePath))
                        continue;

                    _outputFiles.Add(new OutputFileVm
                    {
                        FileName = f.FileName,
                        FullPath = f.FilePath
                    });
                    _pendingFilePaths.Add(f.FilePath);
                }
            }

            if (OutputFileHost != null)
                OutputFileHost.Visibility = _outputFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===== §7.2 簡報單張重生（重生 UI 在預覽視窗，這裡只保存重生需要的脈絡） =====

        /// <summary>本次簡報產生後，把大綱 + .pptx 路徑 + 原始請求存起來，供預覽視窗單張重生時用。可從背景執行緒呼叫。</summary>
        public void SetPresentationDeck(
            PresentationOutlinePayload outline, string pptxPath, string userInput, string sourceSummary)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetPresentationDeck(outline, pptxPath, userInput, sourceSummary));
                return;
            }

            if (outline?.Slides == null || outline.Slides.Count == 0)
            {
                ClearPresentationDeck();
                return;
            }

            _presentationDeck = new PresentationDeckContext
            {
                Outline = outline,
                PptxPath = pptxPath ?? "",
                UserInput = userInput ?? "",
                SourceSummary = sourceSummary ?? ""
            };
        }

        public void ClearPresentationDeck()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ClearPresentationDeck);
                return;
            }

            _presentationDeck = null;
        }

        public PresentationOutlinePayload? GetPresentationOutline() => _presentationDeck?.Outline;
        public string GetPresentationUserInput() => _presentationDeck?.UserInput ?? "";
        public string GetPresentationPptxPath() => _presentationDeck?.PptxPath ?? "";
        public string GetPresentationSourceSummary() => _presentationDeck?.SourceSummary ?? "";

        /// <summary>預覽視窗單張重生成功、覆蓋了同一個 .pptx 後，更新節點保存的大綱（路徑不變）。</summary>
        public void UpdatePresentationOutline(PresentationOutlinePayload updatedOutline)
        {
            if (_presentationDeck != null && updatedOutline != null)
                _presentationDeck.Outline = updatedOutline;
        }

        /// <summary>取得目前輸出區的可開啟檔案完整路徑，用於存檔持久化。</summary>
        public IReadOnlyList<string> GetOutputFilePaths() => _pendingFilePaths.ToList();

        /// <summary>取得目前輸出區 inline 顯示的圖片路徑，用於存檔持久化。</summary>
        public string? GetOutputImagePath() => _outputImagePath;

        /// <summary>從存檔還原輸出區檔案 chip（路徑指向的檔案需仍存在）。可從背景執行緒呼叫。</summary>
        public void RestoreOutputFiles(IEnumerable<string>? paths)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => RestoreOutputFiles(paths));
                return;
            }

            _outputFiles.Clear();
            _pendingFilePaths.Clear();

            if (paths != null)
            {
                foreach (var raw in OutputFileCategory.Sort(paths, System.IO.Path.GetFileName))
                {
                    // 檔案可能被搬過（整理成各存檔子資料夾、刪存檔後集中到 other、
                    // 使用者自己改了存放位置）。只認原本那條路徑的話，chip 會**無聲消失**，
                    // 而且下一次存檔就把空的狀態寫回去——2026-10-15 就這樣弄丟了兩個存檔的附件清單。
                    string? p = _parent?.ResolveGeneratedFile(raw) ?? raw;

                    if (string.IsNullOrWhiteSpace(p) || !System.IO.File.Exists(p))
                    {
                        if (!string.IsNullOrWhiteSpace(raw))
                            AppLog.Warn("Output", $"附件找不到，已從清單移除：{System.IO.Path.GetFileName(raw)}");
                        continue;
                    }

                    _outputFiles.Add(new OutputFileVm
                    {
                        FileName = System.IO.Path.GetFileName(p),
                        FullPath = p
                    });
                    _pendingFilePaths.Add(p);
                }
            }

            if (OutputFileHost != null)
                OutputFileHost.Visibility = _outputFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>設定本次執行產生、要在輸出區直接顯示的圖片（生成圖片任務）。可從背景執行緒呼叫。</summary>
        public void SetOutputImage(string? path)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetOutputImage(path));
                return;
            }

            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                _outputImagePath = null;
                if (OutputImage != null)
                    OutputImage.Source = null;
                if (OutputImageHost != null)
                    OutputImageHost.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                // OnLoad：完整讀進記憶體，避免鎖住磁碟檔案（之後仍可刪除 / 開啟）。
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();

                if (OutputImage != null)
                    OutputImage.Source = bmp;
                _outputImagePath = path;
                if (OutputImageHost != null)
                    OutputImageHost.Visibility = Visibility.Visible;
            }
            catch
            {
                _outputImagePath = null;
                if (OutputImage != null)
                    OutputImage.Source = null;
                if (OutputImageHost != null)
                    OutputImageHost.Visibility = Visibility.Collapsed;
            }
        }

        private void OutputImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent != null && !string.IsNullOrWhiteSpace(_outputImagePath))
            {
                _parent.OpenPreview(_outputImagePath);
                e.Handled = true;
            }
        }

        // 輸出區（文字+圖片+檔案）統一捲動：唯讀 TextBox 會吃掉滾輪事件，這裡在 tunnel 階段先攔下來捲外層。
        private void OutputScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is System.Windows.Controls.ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void AttachmentDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;

            if (sender is Button btn && btn.Tag is AttachmentVm vm)
            {
                bool ok = MenuConfirmDialog.ShowDeleteConfirm(
                    owner: _parent,
                    title: UiLanguage.P("刪除確認", "Confirm delete"),
                    message: UiLanguage.P($"確定要刪除附件？\n{vm.FileName}", $"Delete this attachment?\n{vm.FileName}"),
                    resourceHost: _parent);

                if (!ok) return;

                _parent.RemoveAttachment(this, vm.RelativePath);
                RefreshAttachmentsUI();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void AttachmentScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (AttachmentScroll == null) return;

            _isAttachmentMouseDown = true;
            _isAttachmentDragging = false;
            _attachDragStart = e.GetPosition(AttachmentScroll);
            _attachScrollStartX = AttachmentScroll.HorizontalOffset;
        }

        private void AttachmentScroll_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isAttachmentMouseDown) return;
            if (AttachmentScroll == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var cur = e.GetPosition(AttachmentScroll);
            var dx = cur.X - _attachDragStart.X;

            if (!_isAttachmentDragging)
            {
                if (Math.Abs(dx) < AttachmentDragThreshold) return;

                _isAttachmentDragging = true;
                AttachmentScroll.CaptureMouse();
                e.Handled = true;
            }

            AttachmentScroll.ScrollToHorizontalOffset(_attachScrollStartX - dx);
            e.Handled = true;
        }

        private void AttachmentScroll_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (AttachmentScroll == null) return;

            _isAttachmentMouseDown = false;

            if (_isAttachmentDragging)
            {
                _isAttachmentDragging = false;
                AttachmentScroll.ReleaseMouseCapture();
                e.Handled = true;
            }
        }
    }
}
