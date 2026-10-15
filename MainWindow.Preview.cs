using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Cat5201
{
    /// <summary>
    /// 產出檔案的預覽：開啟、渲染（圖片 / 影片 / 網頁 / 文字 / 簡報）、
    /// 單頁就地編輯與重生、復原，以及把結果寫回 pptx 與對照 PDF。
    ///
    /// 從 MainWindow.xaml.cs 拆出來的——那個檔案六千多行、近三百個方法，
    /// 而預覽這一組自成一塊，也是最近改動最頻繁的地方。
    /// </summary>
    public partial class MainWindow
    {
        // §7.2：重生簡報中的單一張投影片 → 重建 .pptx、換掉舊檔與 chip、更新節點的投影片清單。
        // §7.2：簡報預覽視窗（WebView2）按「重生這張」時，前端 postMessage 進來這裡。
        private async void OnPreviewWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg;
            try { msg = e.TryGetWebMessageAsString() ?? ""; }
            catch { return; }

            if (string.IsNullOrWhiteSpace(msg))
                return;

            int order;
            string action;
            string heading = "";
            var bullets = new List<string>();
            string instruction = "";

            try
            {
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                action = root.TryGetProperty("action", out var a) ? (a.GetString() ?? "") : "";
                if (!root.TryGetProperty("order", out var o) || !o.TryGetInt32(out order))
                    return;

                if (root.TryGetProperty("heading", out var h))
                    heading = h.GetString() ?? "";

                if (root.TryGetProperty("instruction", out var ins))
                    instruction = (ins.GetString() ?? "").Trim();

                if (root.TryGetProperty("bullets", out var bs) && bs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in bs.EnumerateArray())
                    {
                        string text = b.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                            bullets.Add(text.Trim());
                    }
                }
            }
            catch { return; }

            if (string.Equals(action, "regen", StringComparison.OrdinalIgnoreCase))
                await RegenerateSlideFromPreviewAsync(order, instruction);
            else if (string.Equals(action, "edit", StringComparison.OrdinalIgnoreCase))
                await ApplySlideEditFromPreviewAsync(order, heading, bullets);
            else if (string.Equals(action, "image", StringComparison.OrdinalIgnoreCase))
                await ChangeSlideImageFromPreviewAsync(order, instruction, remove: false);
            else if (string.Equals(action, "imageRemove", StringComparison.OrdinalIgnoreCase))
                await ChangeSlideImageFromPreviewAsync(order, "", remove: true);
            // 以下三個是「沒有大綱」的簡報用的：版面不是我們排的，但文字與圖都能就地換。
            else if (string.Equals(action, "editText", StringComparison.OrdinalIgnoreCase))
                await ApplySlideTextDirectAsync(order, bullets);
            else if (string.Equals(action, "rewriteText", StringComparison.OrdinalIgnoreCase))
                await RegenerateSlideFromPreviewAsync(order, instruction);
            else if (string.Equals(action, "replaceImage", StringComparison.OrdinalIgnoreCase))
                await ReplaceSlideImageInPlaceAsync(order, instruction);
            else if (string.Equals(action, "removeImage", StringComparison.OrdinalIgnoreCase))
                await RemoveSlideImagesInPlaceAsync(order);
        }

        /// <summary>
        /// 預覽裡就地編輯某一頁的文字 → 更新大綱、重建 .pptx，並讓對照 PDF 跟著更新（PDF 永遠跟著 pptx）。
        /// 覆蓋前留一版，可用「回到上一版」退回。
        /// </summary>
        private async Task ApplySlideEditFromPreviewAsync(int order, string heading, List<string> bullets)
        {
            string path = _previewPath ?? "";
            var node = _previewOwnerNode;

            if (_previewSlideRegenBusy || node == null ||
                string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _previewSlideRegenBusy = true;

            // 關掉預覽視窗之後這件事仍然在跑（path 與 node 進來時就抓好了），
            // 但使用者看不到任何東西在動，會以為自己把它取消了——所以在節點上掛一個提示。
            node.SetLoadingHint($"正在套用第 {order} 頁的修改…");
            try
            {
                var native = NativeDeckStorage.Read(path);
                if (native == null) return; // Never flatten an external designer's slide structure.
                var outline = native.Outline;

                var slides = outline.Slides.Select(s => s.Order == order
                    ? new PresentationSlidePayload
                    {
                        Order = s.Order,
                        Kind = s.Kind,
                        Heading = string.IsNullOrWhiteSpace(heading) ? s.Heading : heading,
                        Bullets = bullets,
                        ImageBytes = s.ImageBytes
                    }
                    : s).ToList();

                var updated = new PresentationOutlinePayload
                {
                    Title = outline.Title,
                    Topic = outline.Topic,
                    Slides = slides,
                    SlideCount = slides.Count,
                    RequestedSlideCount = outline.RequestedSlideCount,
                    PipelineId = outline.PipelineId,
                    ModelId = outline.ModelId,
                    AgentId = outline.AgentId,
                    SourceSummary = outline.SourceSummary
                };

                await WriteDeckFilesAsync(path, updated, native.Cover);

                node.UpdatePresentationOutline(updated);
                SaveState();
                AppLog.Info("Preview", $"就地編輯第 {order} 頁並存回");

                if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                    OpenPreview(path, node);
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, "儲存修改", "存回簡報失敗：" + ex.Message, this);
            }
            finally
            {
                _previewSlideRegenBusy = false;
                node.SetLoadingHint(null);
            }
        }

        /// <summary>
        /// 預覽裡替某一頁重新生成／新增／移除配圖。圖片是真的要花錢的呼叫，所以先過每日預算檢查。
        /// 封面頁的圖存在 native.Cover，內容頁的圖存在該頁的 ImageBytes。
        /// </summary>
        private async Task ChangeSlideImageFromPreviewAsync(int page, string instruction, bool remove)
        {
            string path = _previewPath ?? "";
            var node = _previewOwnerNode;

            if (_previewSlideRegenBusy || node == null || _nodeService == null ||
                string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _previewSlideRegenBusy = true;

            // 關掉預覽視窗之後這件事仍然在跑（path 與 node 進來時就抓好了），
            // 但使用者看不到任何東西在動，會以為自己把它取消了——所以在節點上掛一個提示。
            node.SetLoadingHint($"正在更新第 {page} 頁的圖…");
            try
            {
                var native = NativeDeckStorage.Read(path);
                if (native == null)
                {
                    MenuConfirmDialog.ShowMessage(this, "保留簡報設計", "這份簡報未包含可安全編輯的原始大綱，無法在這裡換圖。請用 PowerPoint 修改。", this);
                    return;
                }

                var sourceOrder = NativeDeckStorage.SourceOrderForPage(native, page);
                if (!sourceOrder.HasValue) return;

                var outline = native.Outline;
                var slide = outline.Slides.FirstOrDefault(s => s.Order == sourceOrder.Value);
                if (slide == null) return;

                bool isCover = string.Equals(slide.Kind, "cover", StringComparison.OrdinalIgnoreCase);
                byte[]? cover = native.Cover;

                if (remove)
                {
                    if (isCover) cover = null; else slide.ImageBytes = null;
                }
                else
                {
                    if (!CheckDailyBudgetAllows(out string budgetMessage))
                    {
                        MenuConfirmDialog.ShowMessage(this, "已達今日花費上限", budgetMessage, this);
                        return;
                    }

                    using var cts = new CancellationTokenSource();
                    var (png, error) = await _nodeService.RegenerateSlideImageAsync(
                        node, outline, sourceOrder.Value, instruction, cts.Token);

                    if (png == null || png.Length == 0)
                    {
                        MenuConfirmDialog.ShowMessage(this, "這一頁的配圖",
                            string.IsNullOrWhiteSpace(error) ? "配圖沒有產生出來。" : error, this);
                        if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                            OpenPreview(path, node);
                        return;
                    }

                    if (isCover) cover = png; else slide.ImageBytes = png;
                }

                await WriteDeckFilesAsync(path, outline, cover);
                node.UpdatePresentationOutline(outline);
                SaveState();

                if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                    OpenPreview(path, node);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, "這一頁的配圖", "換圖失敗：" + ex.Message, this);
            }
            finally
            {
                _previewSlideRegenBusy = false;
                node.SetLoadingHint(null);
            }
        }

        /// <summary>覆蓋 .pptx 並讓同一次產出的對照 PDF 跟著重建；兩者都先留上一版。</summary>
        /// <summary>
        /// 重建並存回簡報檔（以及它的對照 PDF）。
        ///
        /// 排版計算是純 CPU 工作，丟到背景執行緒跑——九頁含圖的簡報要十幾秒，
        /// 留在 UI 執行緒上會讓整個視窗凍住。檔案寫入與還原仍回到呼叫端的執行緒上做。
        /// </summary>
        private async Task WriteDeckFilesAsync(string pptxPath, PresentationOutlinePayload outline, byte[]? coverPng)
        {
            // 這份簡報的所有對照 PDF 都要更新：產出時 pptx / pdf 的檔名時間戳可能差幾秒，
            // 只更新其中一份的話，使用者點到的那個 chip 會永遠停在舊版。
            var pdfTargets = GeneratedFileCompanion.ResolveRewriteTargets(pptxPath);

            // 個人化關掉對照 PDF 時就不花那幾秒重建——但硬碟上「已經有」的 PDF 還是要更新。
            // 放著不管會留下一份跟簡報對不起來的舊 PDF，那比慢更糟。
            var liveTargets = pdfTargets.Where(File.Exists).ToList();
            bool needPdf = DeliveryOptions.CompanionPdf || liveTargets.Count > 0;

            // Build both before replacing either; layout failure must not destroy a working deck.
            var (pptx, pdf) = await Task.Run(() => (
                PptxBuilder.Build(outline, coverPng),
                needPdf ? DeckPdfBuilder.Build(outline, coverPng) : null));

            var writeTargets = pdf == null
                ? new List<string>()
                : (DeliveryOptions.CompanionPdf ? pdfTargets.ToList() : liveTargets);

            byte[] oldPptx = File.ReadAllBytes(pptxPath);
            var oldPdfs = writeTargets.ToDictionary(
                p => p,
                p => File.Exists(p) ? File.ReadAllBytes(p) : null,
                StringComparer.OrdinalIgnoreCase);

            GeneratedFileHistory.SaveVersion(pptxPath);
            foreach (var target in writeTargets)
                GeneratedFileHistory.SaveVersion(target);

            try
            {
                File.WriteAllBytes(pptxPath, pptx);
                foreach (var target in writeTargets)
                    File.WriteAllBytes(target, pdf!);

                GeneratedFileCompanion.Register(pptxPath, writeTargets.Count > 0 ? writeTargets[0] : null);
            }
            catch
            {
                File.WriteAllBytes(pptxPath, oldPptx);
                foreach (var kv in oldPdfs)
                    if (kv.Value != null) File.WriteAllBytes(kv.Key, kv.Value);
                throw;
            }
        }

        /// <summary>這張投影片實際會印成幾頁（內容太長會被拆成續頁）。</summary>
        private static int CountPagesForSlide(NativeDeckStorage.Data? native, int sourceOrder)
        {
            if (native == null) return 0;

            int count = 0;
            for (int page = 1; page <= 200; page++)
            {
                var owner = NativeDeckStorage.SourceOrderForPage(native, page);
                if (owner == null) break;
                if (owner.Value == sourceOrder) count++;
            }
            return count;
        }

        /// <summary>
        /// 使用者自己打的文字直接寫回去——不花錢、不等模型。
        /// 這是「每一頁各自有編輯按鈕」裡最常用的一個：改錯字、改措辭。
        /// </summary>
        private async Task ApplySlideTextDirectAsync(int order, List<string> lines)
        {
            string path = _previewPath ?? "";
            var node = _previewOwnerNode;
            if (node == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            var deck = PptxContentPreview.Read(path);
            var slide = deck?.Slides.FirstOrDefault(x => x.Order == order);
            if (slide == null) return;

            if (lines.Count != slide.Lines.Count)
            {
                MenuConfirmDialog.ShowMessage(this, "套用文字",
                    $"這一頁原本有 {slide.Lines.Count} 段，你給了 {lines.Count} 段。\n" +
                    "行數必須一樣——多一行或少一行就對不回原本的段落，版面會錯位。", this);
                return;
            }

            GeneratedFileHistory.SaveVersion(path);
            int changed = PptxTextEditor.ReplaceSlideText(path, order, lines);

            if (changed == 0)
            {
                MenuConfirmDialog.ShowMessage(this, "套用文字", "文字沒有變化，檔案保持原樣。", this);
                return;
            }

            AppLog.Info("Preview", $"第 {order} 頁直接改了 {changed} 段文字（版面未動）");
            if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                OpenPreview(path, node);

            await Task.CompletedTask;
        }

        /// <summary>這一頁的配圖全部移除（面板上的「移除配圖」；畫布上也能單張刪）。</summary>
        private async Task RemoveSlideImagesInPlaceAsync(int order)
        {
            string path = _previewPath ?? "";
            var node = _previewOwnerNode;
            if (node == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            var deck = PptxContentPreview.Read(path);
            var slide = deck?.Slides.FirstOrDefault(x => x.Order == order);
            if (slide == null || slide.Images.Count == 0)
            {
                MenuConfirmDialog.ShowMessage(this, "移除配圖", $"第 {order} 頁沒有圖可以移除。", this);
                return;
            }

            GeneratedFileHistory.SaveVersion(path);
            var edits = slide.Images
                .Select(i => new PptxSlideEditor.ImageEdit(i.Index, 0, 0, 0, 0, Delete: true))
                .ToList();

            int changed = PptxSlideEditor.Apply(path, order, images: edits);
            AppLog.Info("Preview", $"第 {order} 頁移除了 {changed} 張配圖");

            if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                OpenPreview(path, node);

            await Task.CompletedTask;
        }

        /// <summary>
        /// 換掉這一頁的圖：重新生成一張，蓋掉原本那張的內容，位置與大小不變。
        /// </summary>
        private async Task ReplaceSlideImageInPlaceAsync(int order, string instruction)
        {
            string path = _previewPath ?? "";
            var node = _previewOwnerNode;
            if (node == null || _nodeService == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            var deck = PptxContentPreview.Read(path);
            var slide = deck?.Slides.FirstOrDefault(x => x.Order == order);
            if (slide == null || slide.Images.Count == 0)
            {
                MenuConfirmDialog.ShowMessage(this, "換圖", $"第 {order} 頁沒有圖可以換。", this);
                return;
            }

            if (!CheckDailyBudgetAllows(out string budget))
            {
                MenuConfirmDialog.ShowMessage(this, "已達今日花費上限", budget, this);
                return;
            }

            // 借用既有的單頁配圖機制：用這一頁的文字拼一份只有一張投影片的大綱，
            // 這樣配圖企劃、藝術方向、「不生成地圖／商標」的守則都跟其他路徑一致。
            var outline = new PresentationOutlinePayload
            {
                Title = slide.Lines.Count > 0 ? slide.Lines[0] : "",
                Topic = "",
                Slides = new List<PresentationSlidePayload>
                {
                    new()
                    {
                        Order = 1,
                        Kind = "content",
                        Heading = slide.Lines.Count > 0 ? slide.Lines[0] : "",
                        Bullets = slide.Lines.Skip(1).ToList(),
                    },
                },
            };

            node.SetLoadingHint($"正在換第 {order} 頁的圖…");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var (png, error) = await _nodeService.RegenerateSlideImageAsync(node, outline, 1, instruction, cts.Token);

                if (png == null || png.Length == 0)
                {
                    MenuConfirmDialog.ShowMessage(this, "換圖",
                        string.IsNullOrWhiteSpace(error) ? "這次沒有生成出圖片。" : error, this);
                    return;
                }

                GeneratedFileHistory.SaveVersion(path);
                if (!PptxTextEditor.ReplaceSlideImage(path, order, png))
                {
                    MenuConfirmDialog.ShowMessage(this, "換圖", "圖生出來了，但寫回檔案失敗。", this);
                    return;
                }

                AppLog.Info("Preview", $"第 {order} 頁已換圖（位置與大小未動）");
                if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                    OpenPreview(path, node);
            }
            finally { node.SetLoadingHint(null); }
        }

        /// <summary>
        /// 沒有大綱的簡報：依指示改寫第 order 頁的文字，寫回同一個檔案，版面不動。
        /// 行數必須一樣——多一行或少一行就對不回原本的段落，那種情況直接放棄並說清楚。
        /// </summary>
        private async Task EditSlideTextInPlaceAsync(string path, NodeControl node, int order, string instruction)
        {
            if (string.IsNullOrWhiteSpace(instruction))
            {
                MenuConfirmDialog.ShowMessage(this, "改這一頁的文字",
                    "這份簡報是由模型直接排版的，程式內不能重做它的版面，但可以改文字。\n\n" +
                    "請在輸入框寫出要怎麼改（例如：第 2 段改短一點、把「虛以藝術」改成「侘寂美學」），再按重新生成。\n" +
                    "要改版面的話只能整份重做（清空頁碼欄位）。", this);
                return;
            }

            var deck = PptxContentPreview.Read(path);
            var slide = deck?.Slides.FirstOrDefault(x => x.Order == order);
            if (slide == null || slide.Lines.Count == 0)
            {
                MenuConfirmDialog.ShowMessage(this, "改這一頁的文字", $"第 {order} 頁沒有可以改的文字。", this);
                return;
            }

            if (!CheckDailyBudgetAllows(out string budget))
            {
                MenuConfirmDialog.ShowMessage(this, "已達今日花費上限", budget, this);
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var rewritten = await _nodeService!.RewriteSlideTextAsync(node, slide.Lines, instruction, cts.Token);

            if (rewritten.Count != slide.Lines.Count)
            {
                MenuConfirmDialog.ShowMessage(this, "改這一頁的文字",
                    "這次改寫的段落數跟原本對不起來，為了不讓版面錯位，沒有寫回去。\n請換個說法再試一次。", this);
                return;
            }

            GeneratedFileHistory.SaveVersion(path);
            int changed = PptxTextEditor.ReplaceSlideText(path, order, rewritten);

            if (changed == 0)
            {
                MenuConfirmDialog.ShowMessage(this, "改這一頁的文字", "文字沒有變化，檔案保持原樣。", this);
                return;
            }

            AppLog.Info("Preview", $"第 {order} 頁就地改了 {changed} 段文字（版面未動）：{System.IO.Path.GetFileName(path)}");

            if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                OpenPreview(path, node);
            else
                AppLog.Info("Preview", $"預覽已關閉，第 {order} 頁的文字仍已寫回");
        }

        // 在預覽視窗就地重生第 order 張：重建大綱 → 重生那張 → 覆蓋同一個 .pptx → 重新渲染預覽。
        private async Task RegenerateSlideFromPreviewAsync(int order, string instruction)
        {
            string path = _previewPath ?? "";
            var node = _previewOwnerNode;

            if (_previewSlideRegenBusy || node == null || _nodeService == null ||
                string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _previewSlideRegenBusy = true;

            // 關掉預覽視窗之後這件事仍然在跑（path 與 node 進來時就抓好了），
            // 但使用者看不到任何東西在動，會以為自己把它取消了——所以在節點上掛一個提示。
            node.SetLoadingHint($"正在重做第 {order} 頁…");
            try
            {
                var native = NativeDeckStorage.Read(path);
                if (native == null)
                {
                    // 沒有可重建的大綱（Claude 排版的、外部來的、手動改過的）：
                    // 版面不能重做，但**文字可以就地改**——把每一段的字換掉，位置、字級、
                    // 顏色、圖片全部不動。改一個錯字不該要整份重做（好幾分鐘＋好幾毛錢）。
                    await EditSlideTextInPlaceAsync(path, node, order, instruction);
                    return;
                }
                if (!CheckDailyBudgetAllows(out string budgetMessage))
                {
                    MenuConfirmDialog.ShowMessage(this, "已達今日花費上限", budgetMessage, this);
                    return;
                }
                var sourceOrder = NativeDeckStorage.SourceOrderForPage(native, order);
                if (!sourceOrder.HasValue) return;
                order = sourceOrder.Value;
                var outline = native.Outline;

                string userInput = !string.IsNullOrWhiteSpace(node.GetPresentationUserInput())
                    ? node.GetPresentationUserInput()
                    : outline.Title;

                using var cts = new CancellationTokenSource();

                // 「這句話是不是在要圖」交給模型判斷——關鍵字表只認得繁體中文的講法，
                // 使用者用簡體、英文、日文講同一件事就會被漏掉。API 不通時才退回關鍵字。
                bool wantsImage = !string.IsNullOrWhiteSpace(instruction)
                                  && await _nodeService.WantsIllustrationsAsync(instruction, cts.Token);

                if (!string.IsNullOrWhiteSpace(instruction))
                {
                    userInput = userInput.TrimEnd() + "\n\n【這一頁的修改要求】\n" + instruction.Trim();

                    // 配圖是另一個步驟做的；不講清楚的話，文字模型會為了「加圖」去刪字騰出版面。
                    if (wantsImage)
                        userInput += "\n（配圖由另一個步驟處理，你只負責這一頁的文字，不要為了配圖刪減內容。）";
                }

                var updated = await _nodeService.RegeneratePresentationSlideAsync(node, outline, order, userInput, cts.Token);

                if (updated == null)
                {
                    // 重生失敗：重新渲染原檔（恢復按鈕可用狀態）。
                    if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                        OpenPreview(path, node);
                    return;
                }

                // 重建前先保留既有封面圖，避免重生一張內容頁後封面圖被洗掉。
                byte[]? coverPng = native.Cover;
                string extraNote = "";

                // 修改要求裡如果提到要圖，就真的去生一張——這句話送給「改寫文字」的模型是不會生出圖的，
                // 使用者卻會以為自己已經要求過了（2026-09-25 實測：輸入「增加符合此頁內容的附圖」什麼也沒發生）。
                if (wantsImage)
                {
                    var slide = updated.Slides.FirstOrDefault(s => s.Order == order);
                    var (png, imageError) = await _nodeService.RegenerateSlideImageAsync(
                        node, updated, order, instruction, cts.Token);

                    if (png is { Length: > 0 } && slide != null)
                    {
                        if (string.Equals(slide.Kind, "cover", StringComparison.OrdinalIgnoreCase)) coverPng = png;
                        else slide.ImageBytes = png;
                    }
                    else
                    {
                        extraNote = "這一頁的配圖沒有生成：" +
                                    (string.IsNullOrWhiteSpace(imageError) ? "圖片步驟沒有產出。" : imageError);
                    }
                }

                int pagesBefore = CountPagesForSlide(native, order);

                // 覆蓋同一個檔（路徑不變 → chip / 預覽都還指向同一份）；先留上一版，PDF 跟著重建。
                await WriteDeckFilesAsync(path, updated, coverPng);

                node.UpdatePresentationOutline(updated);
                SaveState();

                // 重新渲染預覽（若使用者還停在這份）。
                if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
                    OpenPreview(path, node);
                else
                    AppLog.Info("Preview", $"預覽已關閉，第 {order} 頁仍完成並寫回：{System.IO.Path.GetFileName(path)}");

                // 重寫後內容變短就不再需要續頁，那一頁會消失。不講的話看起來就像「它把我的頁刪掉了」。
                int pagesAfter = CountPagesForSlide(NativeDeckStorage.Read(path), order);
                if (pagesAfter < pagesBefore)
                {
                    extraNote = $"這張投影片原本因為內容太長被拆成 {pagesBefore} 頁，重寫後變短，已合併成 {pagesAfter} 頁。" +
                                (string.IsNullOrWhiteSpace(extraNote) ? "" : "\n\n" + extraNote);
                }

                if (!string.IsNullOrWhiteSpace(extraNote))
                    MenuConfirmDialog.ShowMessage(this, "這一頁已更新", extraNote, this);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(this, "重生投影片", "重生投影片失敗：" + ex.Message, this);
            }
            finally
            {
                _previewSlideRegenBusy = false;
                node.SetLoadingHint(null);
            }
        }

        /// <summary>
        /// 缺少 PDF 時以原生場景預覽；外部簡報不抽文字重建版面。
        ///
        /// 被 PowerPoint 手動改過的簡報仍然照常顯示內容，只是關掉單頁編輯：
        /// 內容明明讀得出來，卻只給一句「請開啟原始簡報」，使用者會以為檔案壞了。
        /// </summary>
        private string BuildDeckPreviewHtml(string path)
        {
            var read = NativeDeckStorage.ReadDetailed(path);

            if (read.State == NativeDeckStorage.State.Native && read.Data != null)
                return DeckHtmlRenderer.Build(read.Data.Outline, new[] { read.Data.Cover }, allowEdit: true);

            if (read.State == NativeDeckStorage.State.ManuallyEdited && read.Data != null)
            {
                string html = DeckHtmlRenderer.Build(read.Data.Outline, new[] { read.Data.Cover }, allowEdit: false);
                return InsertDeckBanner(
                    html,
                    "這份簡報在 PowerPoint 裡被改過",
                    "下面顯示的是本程式最後一次產出的版本，不是你手動修改後的樣子。" +
                    "單頁編輯已關閉，以免覆蓋掉你的修改——要看實際版面請用「外部開啟」。" +
                    "若想讓本程式重新接管，請重新產生一份新的簡報。");
            }

            // 沒有版面資料的簡報（Claude 在沙箱裡排的、外部拿來的）：照實際幾何畫出版面，
            // 編輯則走每一頁下面的表單——跟有大綱的那條路徑同一種操作方式。
            var extracted = PptxContentPreview.Read(path);
            if (extracted != null)
                return BuildExtractedDeckHtml(extracted);

            return "<!doctype html><html lang='zh-TW'><meta charset='utf-8'>" +
                   "<body style='font-family:Microsoft JhengHei;padding:48px;color:#1b1b1f'>" +
                   "<h2 style='margin:0 0 12px'>這份簡報讀不開</h2>" +
                   "<p style='color:#5f6368;line-height:1.7'>檔案可能損壞，或不是有效的 PPTX。<br>" +
                   "請用右上角的「用系統程式開啟」確認。</p>" +
                   "</body></html>";
        }

        /// <summary>
        /// 版面預覽：照 pptx 的實際幾何畫出來（唯讀），編輯走每一頁下面的表單。
        ///
        /// 曾經做過「點字直接改、點圖拖曳縮放」的直接操作版本，使用者試用後要求退回表單
        /// （2026-10-15：「不要可以直接修改，回到原本的修改方式」）。
        /// 畫布只負責「看」，所有會改到檔案的動作都在表單裡、按下去才發生——
        /// 這樣也跟有大綱那條路徑的操作方式一致。
        /// </summary>
        private static string BuildExtractedDeckHtml(PptxContentPreview.Deck deck)
        {
            // 版面與配色完全沿用內建版型預覽那一套（DeckHtmlRenderer.Css）：
            // 同一個視窗裡兩種簡報長得不一樣，使用者只會覺得是壞掉
            //（2026-10-15：「字體 顏色所有設計都要一樣」）。
            // 強調色跟那邊一樣依主題決定，這樣兩種簡報的面板連色調都一致。
            string heading = deck.Slides.SelectMany(x => x.Lines).FirstOrDefault(l => l.Length > 2) ?? "";
            string accent = DeckArtDirection.ForTopic(heading).Accent;

            const double CanvasWidthPx = 1100;
            double scale = CanvasWidthPx / Math.Max(1, deck.WidthPt);
            double canvasHeight = deck.HeightPt * scale;

            var sb = new System.Text.StringBuilder();
            sb.Append("<!doctype html><html lang='zh-TW'><meta charset='utf-8'><style>")
              // ── 以下每一條都對齊 DeckHtmlRenderer.Css 的值
              .Append("*{box-sizing:border-box}")
              .Append("body{margin:0;padding:28px 24px 64px;background:#ECECE9;")
              .Append("font-family:'Microsoft JhengHei',system-ui,sans-serif;color:#1B1B19;-webkit-font-smoothing:antialiased}")
              .Append(".slide{max-width:").Append(F(CanvasWidthPx)).Append("px;margin:0 auto 28px}")
              .Append(".page{position:relative;width:100%;height:").Append(F(canvasHeight)).Append("px;")
              .Append("background:#fff;overflow:hidden;box-shadow:0 2px 6px #0000001a,0 12px 28px #00000014}")
              .Append(".tx{position:absolute;overflow:hidden;z-index:2}")
              .Append(".pic{position:absolute;object-fit:cover;z-index:1}")
              .Append(".pno{position:absolute;right:10px;bottom:8px;font:600 11px/1 'Segoe UI';color:#C9C9C2}")
              .Append("details{margin-top:10px;background:#fff;border:1px solid #DFDFDA;border-radius:6px;overflow:hidden}")
              .Append("details[open]{border-color:#").Append(accent).Append("55}")
              .Append("summary{display:flex;align-items:center;gap:12px;padding:11px 16px;cursor:pointer;")
              .Append("list-style:none;font-size:13px;user-select:none}")
              .Append("summary::-webkit-details-marker{display:none}")
              .Append("summary:hover{background:#F6F6F3}")
              .Append(".pg{font:600 12px/1 'Segoe UI';letter-spacing:1.5px;color:#").Append(accent).Append("}")
              .Append(".ttl{flex:1;color:#44443F;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}")
              .Append(".act{color:#77776F;font-size:12px}")
              .Append("details[open] .act::after{content:'（收合）'}")
              .Append(".panel{display:grid;grid-template-columns:1fr 1fr;gap:22px;padding:4px 16px 18px;border-top:1px solid #EDEDE8}")
              .Append(".col{display:flex;flex-direction:column;min-width:0}")
              .Append("label{display:block;margin-top:14px;font-size:12px;color:#6E6E66;letter-spacing:.3px}")
              .Append("textarea{display:block;width:100%;margin-top:6px;padding:10px 12px;")
              .Append("font:14px/1.7 'Microsoft JhengHei';color:#1B1B19;background:#FBFBF9;")
              .Append("border:1px solid #DDDDD7;border-radius:4px;resize:vertical}")
              .Append("textarea:focus{outline:none;border-color:#").Append(accent)
              .Append(";background:#fff;box-shadow:0 0 0 3px #").Append(accent).Append("22}")
              .Append(".row{display:flex;align-items:center;gap:12px;margin-top:12px;flex-wrap:wrap}")
              .Append("button{padding:8px 18px;font:13px 'Microsoft JhengHei';color:#2A2A26;background:#fff;")
              .Append("border:1px solid #CFCFC8;border-radius:4px;cursor:pointer}")
              .Append("button:hover:not(:disabled){background:#F4F4F0;border-color:#B6B6AE}")
              .Append("button.primary{color:#fff;background:#").Append(accent).Append(";border-color:#").Append(accent).Append("}")
              .Append("button.primary:hover:not(:disabled){filter:brightness(1.08)}")
              .Append("button:disabled{opacity:.45;cursor:default}")
              .Append(".imgrow{grid-column:1/-1;padding-top:6px;border-top:1px solid #EFEFEA}")
              .Append(".note{grid-column:1/-1;margin:10px 0 0;padding:8px 11px;font-size:11.5px;line-height:1.6;")
              .Append("color:#6E6E66;background:#F6F6F3;border-radius:5px}")
              .Append(".hint{font-size:11.5px;color:#88887F;line-height:1.5}")
              .Append(".status{grid-column:1/-1;margin:2px 0 0;padding:9px 12px;font-size:12.5px;color:#").Append(accent)
              .Append(";background:#").Append(accent).Append("10;border-radius:4px}")
              .Append(".lead{max-width:").Append(F(CanvasWidthPx)).Append("px;margin:0 auto 14px;font-size:11.5px;")
              .Append("color:#88887F;line-height:1.6}")
              .Append("@media(max-width:820px){.panel{grid-template-columns:1fr}}")
              .Append("</style><body>");

            sb.Append("<div class='lead'>這份簡報的版面不是本程式排的，下面是依檔案裡的實際座標重畫的近似版面")
              .Append("（陰影、漸層等特效沒有重現）。要改內容，用每一頁下面的「編輯這一頁」。</div>");

            foreach (var slide in deck.Slides)
            {
                sb.Append("<div class='slide'><div class='page'>");

                foreach (var shape in slide.Shapes)
                {
                    string box =
                        $"left:{F(shape.XPt * scale)}px;top:{F(shape.YPt * scale)}px;" +
                        $"width:{F(shape.WidthPt * scale)}px;height:{F(shape.HeightPt * scale)}px;";

                    if (shape.Image != null)
                    {
                        sb.Append("<img class='pic' style=\"").Append(box)
                          .Append("\" src=\"data:").Append(shape.Image.ContentType).Append(";base64,")
                          .Append(Convert.ToBase64String(shape.Image.Bytes)).Append("\"/>");
                        continue;
                    }

                    sb.Append("<div class='tx' style=\"").Append(box);
                    if (shape.FillColor != null) sb.Append("background:").Append(shape.FillColor).Append(';');
                    sb.Append("\">");

                    foreach (var line in shape.Lines)
                    {
                        sb.Append("<div style=\"font-size:").Append(F(line.SizePt * scale))
                          .Append("px;line-height:1.32;color:").Append(line.Color).Append(';')
                          .Append(line.Bold ? "font-weight:700;" : "")
                          .Append("text-align:").Append(line.Align).Append(";white-space:pre-wrap\">")
                          .Append(System.Net.WebUtility.HtmlEncode(line.Text))
                          .Append("</div>");
                    }

                    sb.Append("</div>");
                }

                sb.Append("<div class='pno'>").Append(slide.Order.ToString("00")).Append("</div></div>");

                AppendPageControls(sb, slide);

                sb.Append("</div>");
            }

            sb.Append(PanelScript).Append("</body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// 每一頁下面的編輯面板。結構、字體、顏色、圓角全部跟內建版型那一套相同——
        /// 同一個視窗裡兩種簡報長得不一樣，使用者只會覺得是壞掉。
        /// </summary>
        private static void AppendPageControls(System.Text.StringBuilder sb, PptxContentPreview.Slide slide)
        {
            string heading = slide.Lines.FirstOrDefault(l => l.Length > 2) ?? "";

            sb.Append("<details data-page='").Append(slide.Order).Append("'>")
              .Append("<summary><span class='pg'>").Append(slide.Order.ToString("00")).Append("</span>")
              .Append("<span class='ttl'>").Append(System.Net.WebUtility.HtmlEncode(heading)).Append("</span>")
              .Append("<span class='act'>編輯這一頁</span></summary>")
              .Append("<div class='panel'>");

            // 左欄：直接改這一頁的文字
            sb.Append("<div class='col'><label>這一頁的文字（一行一段，可直接增刪字）</label>")
              .Append("<textarea class='lines' rows='").Append(Math.Clamp(slide.Lines.Count, 4, 12)).Append("'>")
              .Append(System.Net.WebUtility.HtmlEncode(string.Join("\n", slide.Lines)))
              .Append("</textarea>")
              .Append("<div class='row'><button class='primary' data-action='editText'>儲存文字</button>")
              .Append("<span class='hint'>只改文字，版面與其他頁不動（可用「回到上一版」還原）</span></div>")
              .Append("</div>");

            // 右欄：交給模型重做這一頁
            sb.Append("<div class='col'><label>重新生成這一頁</label>")
              .Append("<textarea class='instruction' rows='3' placeholder='想怎麼改？例如：改寫得更精簡、多強調成本效益、換一個切入角度（留空＝照原需求重寫）'></textarea>")
              .Append("<div class='row'><button data-action='rewriteText'>只重新生成這一頁</button></div>")
              .Append("<span class='hint'>這份簡報的版面不是本程式排的，所以只會改寫文字、版面維持原樣</span>")
              .Append("</div>");

            sb.Append("<p class='note'>行數不要增減——增減會讓版面錯位。")
              .Append("頁首、頁碼、條列序號也是段落，會一起列出來；不想動的就原樣留著。</p>");

            if (slide.Images.Count > 0)
            {
                sb.Append("<div class='imgrow'><label>這一頁的配圖（目前有 ").Append(slide.Images.Count).Append(" 張）</label>")
                  .Append("<textarea class='image-brief' rows='2' placeholder='想要什麼畫面？例如：清晨霧氣中的湖面與岩石、紙與金屬的質感靜物（留空＝依這一頁的內容自動決定）'></textarea>")
                  .Append("<div class='row'><button data-action='replaceImage'>換一張配圖</button>")
                  .Append("<button data-action='removeImage'>移除配圖</button>")
                  .Append("<span class='hint'>生成一張圖會實際呼叫圖片模型（")
                  .Append(ModelCostEstimator.ImageUnitCostText(AgentRuntime.DeckImageSize, "high"))
                  .Append("）；地圖、國旗、商標這類查得到正確答案的東西不會生成</span></div></div>");
            }

            sb.Append("<p class='status' hidden></p></div></details>");
        }

        /// <summary>面板按鈕 → postMessage（跟內建版型預覽同一條通道）。</summary>
        private const string PanelScript =
            "<script>document.addEventListener('click',e=>{" +
            "const b=e.target.closest('.panel button[data-action]');if(!b||!window.chrome?.webview)return;" +
            "const d=b.closest('details'),a=b.dataset.action,s=d.querySelector('.status');" +
            "d.querySelectorAll('button').forEach(x=>x.disabled=true);s.hidden=false;" +
            "s.textContent={editText:'正在寫回文字…',replaceImage:'正在生成新的圖…通常要一兩分鐘。'," +
            "removeImage:'正在移除配圖…'}[a]||'正在重新生成這一頁…';" +
            "window.chrome.webview.postMessage(JSON.stringify({action:a,order:Number(d.dataset.page)," +
            "bullets:(d.querySelector('.lines')?.value||'').split('\\n')," +
            "instruction:(a==='replaceImage'?d.querySelector('.image-brief')?.value:d.querySelector('.instruction')?.value)||''}));" +
            "});</script>";

        private static string F(double value)
            => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>在既有簡報預覽 HTML 的 body 最前面插一條說明橫幅。</summary>
        private static string InsertDeckBanner(string html, string title, string detail)
        {
            string banner =
                "<div style=\"font-family:Microsoft JhengHei;margin:16px;padding:14px 18px;border-radius:10px;" +
                "background:#fff4e5;border:1px solid #ffd8a8;color:#8a4b00;line-height:1.65\">" +
                "<div style=\"font-weight:700;margin-bottom:4px\">" + System.Net.WebUtility.HtmlEncode(title) + "</div>" +
                "<div style=\"font-size:13px\">" + System.Net.WebUtility.HtmlEncode(detail) + "</div></div>";

            int at = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return banner + html;

            int close = html.IndexOf('>', at);
            if (close < 0) return banner + html;

            return html.Substring(0, close + 1) + banner + html.Substring(close + 1);
        }

        private string? _previewPath;


        private NodeControl? _previewOwnerNode;

        private bool _previewWebMsgHooked;

        private bool _previewSlideRegenBusy;

        private bool _previewMediaPlaying;

        /// <summary>在 app 內預覽生成的檔案；無法內嵌渲染的格式給 fallback + 用系統程式開啟。</summary>
        public void OpenPreview(string? fullPath) => OpenPreview(fullPath, null);

        /// <summary>
        /// async void 的進入點：預覽開啟途中任何例外都必須在這裡收掉，
        /// 否則會變成未處理例外把整個程式帶掉。
        /// </summary>
        public async void OpenPreview(string? fullPath, NodeControl? owner)
        {
            try
            {
                await OpenPreviewAsync(fullPath, owner);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Preview", "開啟預覽失敗", ex);
                ShowFallback("預覽失敗：" + ex.Message);
            }
        }

        private async Task OpenPreviewAsync(string? fullPath, NodeControl? owner)
        {
            // 檔案可能被搬過（整理成子資料夾、存檔刪除後集中到 other、使用者自己改了存放位置），
            // 節點裡記的卻是當初那條路徑。先用檔名在根目錄底下找一次再說找不到。
            fullPath = ResolveGeneratedFile(fullPath);

            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                MenuConfirmDialog.ShowMessage(this, "預覽失敗", "找不到生成的檔案（可能已被刪除）。", this);
                return;
            }

            _previewOwnerNode = owner;

            // 安全檢查：整個產出根目錄都算合法（與 OpenGeneratedFile 一致）——
            // 別的存檔的檔案、以及被刪存檔集中到 other 的，都要預覽得到。
            string rootFull = System.IO.Path.GetFullPath(GeneratedFilesRoot);
            string targetFull = System.IO.Path.GetFullPath(fullPath);
            if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                MenuConfirmDialog.ShowMessage(this, "預覽失敗", "檔案不在允許預覽的範圍內。", this);
                return;
            }

            _previewPath = targetFull;
            PreviewFileName.Text = System.IO.Path.GetFileName(targetFull);

            HideAllPreviewRenderers();
            PreviewOverlay.Visibility = Visibility.Visible;

            // 產出檔＋知道是哪個節點做的 → 可以就地重新生成（並補修改指示）。
            bool canRegen = _previewOwnerNode != null &&
                            System.IO.Path.GetExtension(targetFull).ToLowerInvariant()
                                is ".pptx" or ".docx" or ".xlsx" or ".pdf" or ".png" or ".mp4";
            if (PreviewRegenBar != null)
            {
                PreviewRegenBar.Visibility = canRegen ? Visibility.Visible : Visibility.Collapsed;
                if (canRegen && PreviewRegenButton != null)
                    PreviewRegenButton.IsEnabled = !_previewOwnerNode!.IsGenerating;

                // 只有簡報能指定頁碼重生（其他檔案類型整份重做）。
                if (PreviewPageBox != null)
                    PreviewPageBox.Visibility = canRegen && targetFull.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                RefreshPreviewUndoState();
            }

            string ext = System.IO.Path.GetExtension(targetFull).TrimStart('.').ToLowerInvariant();

            try
            {
                switch (ext)
                {
                    case "png": case "jpg": case "jpeg": case "gif": case "bmp": case "webp":
                        ShowImagePreview(targetFull);
                        break;

                    case "mp4": case "mov": case "webm": case "m4v":
                        ShowMediaPreview(targetFull);
                        break;

                    case "html": case "htm": case "pdf":
                        await ShowWebPreviewAsync(targetFull);
                        break;

                    case "docx":
                        // 跟簡報同一套：同一次產出的配對 PDF 就是使用者會拿到的那份版面
                        // （封面、頁首頁尾、字體、表格樣式都在），有就直接顯示它。
                        // 抽文字重排的 HTML 只有內容對得上，拿來當預覽會讓人以為報告長那樣。
                        string? reportPdf = GeneratedFileCompanion.FindCompanionPdf(targetFull);
                        if (reportPdf != null)
                            await ShowWebPreviewAsync(reportPdf, targetFull);
                        else
                            await ShowHtmlContentAsync(
                                ArtifactHtmlRenderer.BuildDocxHtml(ArtifactTextExtractor.ExtractDocx(targetFull)),
                                targetFull);
                        break;

                    case "pptx":
                        string? deckPdf = GeneratedFileCompanion.FindCompanionPdf(targetFull);
                        if (NativeDeckStorage.Read(targetFull) != null)
                            await ShowHtmlContentAsync(BuildDeckPreviewHtml(targetFull), targetFull);
                        else if (deckPdf != null)
                            await ShowWebPreviewAsync(deckPdf, targetFull);
                        else
                            await ShowHtmlContentAsync(BuildDeckPreviewHtml(targetFull), targetFull);
                        if (PreviewPageBox != null)
                            PreviewPageBox.Visibility = canRegen && NativeDeckStorage.Read(targetFull) != null
                                ? Visibility.Visible : Visibility.Collapsed;
                        break;

                    case "xlsx":
                        // 表格不走配對 PDF：那份是「報告版」的排版，不是試算表本身。
                        // 改用帶顯示格式與標題色的 grid，預覽就跟在 Excel 裡看到的一致。
                        await ShowHtmlContentAsync(
                            ArtifactHtmlRenderer.BuildXlsxHtml(ArtifactTextExtractor.ExtractXlsxGrid(targetFull)),
                            targetFull);
                        break;

                    case "txt": case "md": case "csv": case "json":
                        ShowTextPreview(File.ReadAllText(targetFull));
                        break;

                    default:
                        ShowFallback($"「.{ext}」格式無法在這裡預覽，可用系統程式開啟。");
                        break;
                }
            }
            catch (Exception ex)
            {
                ShowFallback($"預覽失敗：{ex.Message}");
            }
        }

        private void ShowImagePreview(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();

                PreviewImage.Source = bmp;
                PreviewImageScroll.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowFallback($"圖片載入失敗：{ex.Message}");
            }
        }

        private void ShowMediaPreview(string path)
        {
            PreviewMedia.Visibility = Visibility.Visible;
            PreviewMedia.Source = new Uri(path);
            PreviewMedia.Play();
            _previewMediaPlaying = true;
        }

        private async Task ShowWebPreviewAsync(string path, string? sourcePath = null)
        {
            PreviewLoading.Visibility = Visibility.Visible;
            PreviewWeb.Visibility = Visibility.Visible;

            try
            {
                await PreviewWeb.EnsureCoreWebView2Async();

                // 等待初始化期間若已關閉或切換到別的檔案，放棄這次導覽。
                if (PreviewOverlay.Visibility != Visibility.Visible ||
                    !string.Equals(_previewPath, sourcePath ?? path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // WebView2 的 PDF 檢視器會依網址快取：單頁重生後檔案已經換了，導覽到同一個
                // file:// 網址還是會顯示舊的那一份。用檔案的修改時間當版本參數，內容變了網址才變。
                string url = new Uri(path).AbsoluteUri;
                try
                {
                    long stamp = File.GetLastWriteTimeUtc(path).Ticks;
                    url += (url.Contains('?') ? "&" : "?") + "v=" + stamp;
                }
                catch { /* 取不到時間就照原網址走 */ }

                PreviewWeb.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                PreviewWeb.Visibility = Visibility.Collapsed;
                ShowFallback($"無法載入預覽器（WebView2）：{ex.Message}");
            }
            finally
            {
                PreviewLoading.Visibility = Visibility.Collapsed;
            }
        }

        // 把渲染好的 HTML 字串丟進 WebView2 顯示（DOCX → markdown 排版、PPTX → 投影片卡片）。
        private async Task ShowHtmlContentAsync(string html, string sourcePath)
        {
            PreviewLoading.Visibility = Visibility.Visible;
            PreviewWeb.Visibility = Visibility.Visible;

            try
            {
                await PreviewWeb.EnsureCoreWebView2Async();

                if (!_previewWebMsgHooked)
                {
                    PreviewWeb.CoreWebView2.WebMessageReceived += OnPreviewWebMessage;
                    _previewWebMsgHooked = true;
                }

                if (PreviewOverlay.Visibility != Visibility.Visible ||
                    !string.Equals(_previewPath, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // NavigateToString 有約 2MB 字串上限：含封面圖（base64）的簡報會超過，導致
                // 「Value does not fall within the expected range」。改寫成暫存 HTML 檔再用 file:// 導覽，無大小限制。
                string tempHtml = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cat5201_preview.html");
                File.WriteAllText(tempHtml, html, new System.Text.UTF8Encoding(false));
                string uri = new Uri(tempHtml).AbsoluteUri + "?t=" + DateTime.Now.Ticks;
                PreviewWeb.CoreWebView2.Navigate(uri);
            }
            catch (Exception ex)
            {
                PreviewWeb.Visibility = Visibility.Collapsed;
                ShowFallback($"無法載入預覽器（WebView2）：{ex.Message}");
            }
            finally
            {
                PreviewLoading.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowTextPreview(string? text)
        {
            PreviewText.Text = string.IsNullOrWhiteSpace(text) ? "（沒有可顯示的文字內容）" : text;
            PreviewTextScroll.Visibility = Visibility.Visible;
        }

        private void ShowFallback(string message)
        {
            PreviewFallbackText.Text = message;
            PreviewFallback.Visibility = Visibility.Visible;
        }

        private void HideAllPreviewRenderers()
        {
            PreviewImageScroll.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;

            PreviewTextScroll.Visibility = Visibility.Collapsed;
            PreviewFallback.Visibility = Visibility.Collapsed;
            PreviewLoading.Visibility = Visibility.Collapsed;

            PreviewMedia.Visibility = Visibility.Collapsed;
            try { PreviewMedia.Stop(); PreviewMedia.Close(); } catch { }
            PreviewMedia.Source = null;
            _previewMediaPlaying = false;

            PreviewWeb.Visibility = Visibility.Collapsed;
            try { PreviewWeb.CoreWebView2?.Navigate("about:blank"); } catch { }
        }

        private void ClosePreview()
        {
            HideAllPreviewRenderers();
            PreviewOverlay.Visibility = Visibility.Collapsed;
            _previewPath = null;
            _previewOwnerNode = null;
        }

        private void ClosePreview_Click(object sender, RoutedEventArgs e) => ClosePreview();

        private void PreviewRegenInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (PreviewRegenHint != null && PreviewRegenInput != null)
                PreviewRegenHint.Visibility = string.IsNullOrEmpty(PreviewRegenInput.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        /// <summary>「上一版 / 下一版」按鈕的可用狀態：該方向有備份才亮。</summary>
        private void RefreshPreviewUndoState()
        {
            bool hasPath = !string.IsNullOrWhiteSpace(_previewPath);

            if (PreviewUndoButton != null)
                PreviewUndoButton.IsEnabled = hasPath && GeneratedFileHistory.HasPrevious(_previewPath);

            if (PreviewRedoButton != null)
                PreviewRedoButton.IsEnabled = hasPath && GeneratedFileHistory.HasNext(_previewPath);
        }

        // 新版不如舊版時退回：原檔與對照 PDF 一起退，預覽立刻重新整理。
        private void PreviewUndo_Click(object sender, RoutedEventArgs e) =>
            StepPreviewVersion(forward: false);

        // 退回之後又想要新版：不用重新生成（也不用再付一次錢），直接把那一版拿回來。
        private void PreviewRedo_Click(object sender, RoutedEventArgs e) =>
            StepPreviewVersion(forward: true);

        /// <summary>
        /// 版本前後切換。兩個方向的步驟完全一樣（檔案、對照 PDF、節點大綱、預覽），
        /// 差別只在呼叫哪一個還原方法，所以共用同一段——分開寫兩份的話，
        /// 「對照 PDF 要先問出來」這種容易漏掉的細節就會只修對一邊。
        /// </summary>
        private void StepPreviewVersion(bool forward)
        {
            string path = _previewPath ?? "";
            if (string.IsNullOrWhiteSpace(path))
                return;

            bool available = forward
                ? GeneratedFileHistory.HasNext(path)
                : GeneratedFileHistory.HasPrevious(path);

            string label = forward ? "下一版" : "上一版";

            if (!available)
            {
                MenuConfirmDialog.ShowMessage(this, label, $"沒有可以切換的{label}。", this);
                return;
            }

            // 對照 PDF 要在還原「之前」先問出來：FindCompanionPdf 會比對 pptx 的雜湊，
            // 一旦 pptx 已經換成另一版，雜湊就對不上、回 null，PDF 於是停在原本那一版——
            // 結果就是只換了簡報，PDF 沒跟上（兩份內容從此不一致）。
            var companions = GeneratedFileCompanion.ResolveRewriteTargets(path)
                .Where(File.Exists)
                .ToList();

            bool ok = forward
                ? GeneratedFileHistory.RestoreNext(path)
                : GeneratedFileHistory.RestorePrevious(path);

            if (ok)
            {
                foreach (var companion in companions)
                {
                    if (forward) GeneratedFileHistory.RestoreNext(companion);
                    else GeneratedFileHistory.RestorePrevious(companion);
                }

                // 還原後重新建立配對（雜湊已經是這一版的那一組）。
                if (companions.Count > 0)
                    GeneratedFileCompanion.Register(path, companions[0]);

                // 節點保存的大綱要跟著換，否則下次單頁重生會以另一版為底本。
                if (_previewOwnerNode != null && path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
                {
                    var restored = NativeDeckStorage.Read(path)?.Outline;
                    if (restored != null)
                        _previewOwnerNode.UpdatePresentationOutline(restored);
                    SaveState();
                }

                AppLog.Info("Preview", $"已切換到{label}：" + System.IO.Path.GetFileName(path));
                OpenPreview(path, _previewOwnerNode);
            }
            else
            {
                MenuConfirmDialog.ShowMessage(this, label, $"沒有可以切換的{label}。", this);
            }

            RefreshPreviewUndoState();
        }

        // 預覽視窗「重新生成」：用原需求 + 這裡輸入的修改指示，重跑產生這個檔案的節點。
        // 有填頁碼且是簡報 → 只重做那一頁（覆蓋同一個檔，先留上一版）。
        private async void PreviewRegenerate_Click(object sender, RoutedEventArgs e)
        {
            var node = _previewOwnerNode;
            if (node == null)
                return;

            if (node.IsGenerating)
            {
                MenuConfirmDialog.ShowMessage(this, "重新生成", "這個節點正在執行中，請等它跑完再重新生成。", this);
                return;
            }

            if (!CheckDailyBudgetAllows(out string budgetMessage))
            {
                MenuConfirmDialog.ShowMessage(this, "已達今日花費上限", budgetMessage, this);
                return;
            }

            string instruction = PreviewRegenInput?.Text?.Trim() ?? "";
            string pageText = PreviewRegenPage?.Text?.Trim() ?? "";

            // 單頁重生：留在預覽裡就地更新，不必重跑整個節點。
            if (!string.IsNullOrWhiteSpace(pageText) &&
                int.TryParse(pageText, out int pageNumber) &&
                pageNumber > 0 &&
                (_previewPath ?? "").EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                await RegenerateSlideFromPreviewAsync(pageNumber, instruction);
                if (PreviewRegenPage != null) PreviewRegenPage.Text = "";
                if (PreviewRegenInput != null) PreviewRegenInput.Text = "";
                RefreshPreviewUndoState();
                return;
            }

            // 沒填頁碼＝整份重跑。簡報可以單頁重生，別讓使用者以為按下去只會改一頁。
            if ((_previewPath ?? "").EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) &&
                !MenuConfirmDialog.ShowConfirm(this, "重新生成整份簡報",
                    "沒有指定頁碼，這會重跑整個節點、整份簡報重做（費用與時間都是整份的）。\n\n只想改某一頁的話：取消後在預覽裡展開那一頁的「編輯第 N 頁」，用「只重新生成這一頁」。",
                    this, "整份重做", "取消"))
            {
                return;
            }

            ClosePreview();
            if (PreviewRegenInput != null)
                PreviewRegenInput.Text = "";

            AppLog.Info("Preview", string.IsNullOrWhiteSpace(instruction)
                ? "預覽重新生成（無額外指示）"
                : "預覽重新生成，附修改指示");

            await node.RegenerateWithInstructionAsync(instruction);
        }

        private void PreviewOverlay_BackgroundClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => ClosePreview();

        // 點卡片本身不關閉（阻止冒泡到背景）。
        private void PreviewCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => e.Handled = true;

        private void PreviewOpenExternal_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_previewPath))
                OpenGeneratedFile(_previewPath);
        }

        private void PreviewMedia_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_previewMediaPlaying)
                PreviewMedia.Pause();
            else
                PreviewMedia.Play();
            _previewMediaPlaying = !_previewMediaPlaying;
        }

        // 唯讀 TextBox 會吃掉滾輪事件，這裡在 tunnel 階段先攔下來捲外層 ScrollViewer。
        private void PreviewTextScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void PreviewMedia_MediaOpened(object sender, RoutedEventArgs e) { }

        private void PreviewMedia_MediaEnded(object sender, RoutedEventArgs e)
        {
            PreviewMedia.Position = TimeSpan.Zero;
            PreviewMedia.Pause();
            _previewMediaPlaying = false;
        }
    }
}
