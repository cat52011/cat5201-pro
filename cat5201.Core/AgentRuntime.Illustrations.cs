using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    // AgentRuntime 的一部分：配圖：封面、內容頁與報告插圖的企劃與生成。
    // （2026-11-03 依職責拆檔：方法原樣搬出，沒有改任何邏輯；主流程在 AgentRuntime.cs。）
    public sealed partial class AgentRuntime
    {
        /// <summary>
        /// Image Gen v1：呼叫 DALL-E 3 依使用者描述生成圖片，存成 .png、加入 GeneratedFile artifact，
        /// 並在答案尾端附上一句說明（圖片本體由輸出區直接顯示，檔案 chip 可開啟原圖）。
        /// 生成失敗不影響主答案，只把 generate_image 階段標記為 failed。
        /// </summary>
        // 通用配圖 brief：把一段內容素材交給 Claude，萃取成「一個具體、單一焦點、可畫的英文畫面提示」，
        // 並套用統一的簡約資訊示意風格指示。封面、（後續）內容頁、報告章節配圖共用，確保調性一致。
        private async Task<string> BuildIllustrationBriefAsync(
            INodeContext node, string material, string contextLabel, CancellationToken ct, DeckArtDirection? art = null)
        {
            using var usagePurpose = UsageMeter.Purpose("配圖企劃");
            if (string.IsNullOrWhiteSpace(material))
                return "";

            string briefPrompt =
                $"你是簡報 / 報告配圖的視覺設計師。下面是一份「{contextLabel}」要表達的內容。\n" +
                "請從中找出『一個最能代表它的核心概念』，寫成一段【具體、精煉、單一焦點】的英文圖片提示，描述畫面主體、構圖與象徵元素。\n\n" +
                "嚴格規則：\n" +
                "1. 只輸出最終英文圖片提示本身，不要任何解釋、前後綴、引號或 markdown。\n" +
                "2. 選擇內容中可具體描繪的景物、物件或作品。禁止用上升箭頭、硬幣、燈泡、握手或拼貼圖示代替內容；資料與趨勢留給原生圖表。若沒有合適主體，回傳空字串。\n" +
                "3. 不要捏造任何文字、商標、數據或品牌名。\n" +
                "   畫「類型」不要畫「特定個體」：溫帶雨林與湖面的晨霧可以，某座山的正確外形不行；" +
                "法式主菜的擺盤特寫可以，某家餐廳的招牌菜照片不行。\n" +
                "   只有地圖、國旗、商標、統計圖表、特定真人長相這幾類才回傳空字串（畫出來一定是錯的）；" +
                "主題是真實的地方、食物、產業並不代表不能配圖——那些都畫得出漂亮又誠實的示意畫面。\n" +
                $"4. {(art == null ? IllustrationStyle.BriefStyleGuidance : IllustrationStyle.StyleFor(art))}\n" +
                "5. 生成圖只能是示意，不冒充真實景點、作品或事件的證據；保留完整主體，不加入圖表、文字或數據。\n\n" +
                "=== 內容 ===\n" + material;

            var decision = new NodeExecutionDecision
            {
                RequestedAgentId = "image-agent",
                ActualAgentId = "image-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(_scope.DocumentAuthorModel(AiModels.Claude_Sonnet5)),
                ModelId = AiModelHelper.NormalizeNodeModel(_scope.DocumentAuthorModel(AiModels.Claude_Sonnet5)),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = "Illustration Brief",
                ResolverReason = "先把內容轉成具體、單一焦點的配圖畫面，避免生出無意義的抽象圖。",
                StatusLabel = "Auto",
                // 不鎖單一模型：某一家沒額度時要能換一家，不然整個配圖流程會被一家公司的餘額綁死。
                ForceSingleModel = false
            };

            try
            {
                var r = await _executeWithFallbackAsync(node, briefPrompt, decision, null, false, ct);

                // 呼叫失敗（餘額不足、金鑰無效、斷網…）跟「這頁沒有適合入畫的主體」是兩回事，
                // 混在一起會讓使用者看到完全錯誤的理由（2026-09-25 實測：Anthropic 沒額度，卻回報「沒有合適主體」）。
                if (!r.IsSuccess)
                {
                    NoteImageSkipped(FailureExplainer.Describe(r.ErrorMessage) + "（配圖企劃步驟）");
                    AppLog.Warn("AgentRuntime", "配圖企劃失敗，改用本地簡化提示：" + FirstChars(r.ErrorMessage, 160));
                    return LocalIllustrationBrief(material);
                }

                string brief = (r.Text ?? "").Trim();

                // 查得到正確答案的東西（地圖／國旗／商標／圖表…）不配圖：畫出來的一定不是真的那個。
                if (IllustrationStyle.IsFactualSubject(brief))
                {
                    AppLog.Info("AgentRuntime", "配圖主題屬於可查證的真實事物（地圖／標誌／圖表等），已略過配圖");
                    return "";
                }

                return brief;
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        /// <summary>
        /// 寫提示的模型全都不能用時的保底：直接用這一頁的標題／主題當畫面主體。
        /// 品質不如模型寫的，但「有一張切題的圖」遠比「因為某一家沒額度就整份變純文字」好。
        /// </summary>
        private static string FirstChars(string? text, int max)
        {
            string t = (text ?? "").Trim();
            return t.Length <= max ? t : t[..max];
        }

        private static string LocalIllustrationBrief(string material)
        {
            string text = (material ?? "")
                .Replace("使用者指定的畫面：", " ")
                .Replace("簡報標題：", " ")
                .Replace("主題：", " ")
                .Replace("\r", " ").Replace("\n", " ")
                .Trim();

            if (text.Length > 120) text = text[..120];
            if (text.Length == 0) return "";

            string brief = $"A single evocative subject representing: {text}";
            return IllustrationStyle.IsFactualSubject(brief) ? "" : brief;
        }

        // 封面 brief 的素材：標題 + 主題 + 前幾個內容頁的重點，讓封面圖能代表整份簡報、與內容呼應。
        private static string BuildCoverBriefMaterial(PresentationOutlinePayload outline)
            => BuildCoverBriefMaterial(outline, null);

        /// <summary>
        /// 封面要畫什麼的素材。avoidSubjects＝內容頁照片已經畫的主體：封面改畫能代表整體的另一個畫面，
        /// 不要再畫一次（2026-11-06 京都簡報：封面和內容頁都是伏見稻荷的鳥居）。
        /// </summary>
        internal static string BuildCoverBriefMaterial(PresentationOutlinePayload outline, IReadOnlyCollection<string>? avoidSubjects)
        {
            if (outline == null) return "";
            string avoid = avoidSubjects == null || avoidSubjects.Count == 0
                ? ""
                : "\n\n內容頁已經有下面這些主體的照片，封面不要再畫同一個主體，改畫能代表整份內容的另一個畫面：\n" +
                  string.Join("\n", avoidSubjects.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => "- " + x.Trim()));
            return BuildCoverBriefMaterialCore(outline) + avoid;
        }

        private static string BuildCoverBriefMaterialCore(PresentationOutlinePayload outline)
        {

            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(outline.Title)) sb.AppendLine($"簡報標題：{outline.Title}");
            if (!string.IsNullOrWhiteSpace(outline.Topic)) sb.AppendLine($"主題：{outline.Topic}");

            var contentSlides = (outline.Slides ?? System.Array.Empty<PresentationSlidePayload>())
                .Where(s => string.Equals(s.Kind, "content", System.StringComparison.OrdinalIgnoreCase))
                .Take(4);
            foreach (var s in contentSlides)
            {
                string firstBullet = (s.Bullets != null && s.Bullets.Count > 0) ? s.Bullets[0] : "";
                sb.AppendLine($"- {s.Heading}：{firstBullet}".TrimEnd('：', ' '));
            }

            return sb.ToString().Trim();
        }

        // 簡報內容頁智慧配圖數量上限（控制成本）。
        private const int MaxContentSlideImages = 3;

        /// <summary>簡報配圖的尺寸：版面上的圖欄是直的（約 3:4），用直式圖才不會裁掉一半。</summary>
        public const string DeckImageSize = "1024x1536";

        /// <summary>配圖品質（個人化設定，見 <see cref="DeliveryOptions.IllustrationQuality"/>）。</summary>
        public static string IllustrationQuality => DeliveryOptions.IllustrationQuality;

        // 配圖為什麼沒生出來（沒金鑰、沒有適合入畫的主體、API 失敗…）。
        // 生圖是並行的，所以要鎖；只留第一個原因，訊息才不會變成一串雜訊。
        private readonly object _imageSkipLock = new();
        private string? _imageSkipReason;

        private void NoteImageSkipped(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return;
            lock (_imageSkipLock) _imageSkipReason ??= reason.Trim();
        }

        private string? ImageSkipReason()
        {
            lock (_imageSkipLock) return _imageSkipReason;
        }

        /// <summary>
        /// 單頁配圖：預覽裡針對某一頁重新生成／新增配圖。
        /// 回傳圖片位元組；失敗時 Png 為 null，Error 是可以直接顯示給使用者的中文原因。
        /// </summary>
        public async Task<(byte[]? Png, string Error)> RegenerateSlideImageAsync(
            INodeContext node,
            PresentationOutlinePayload outline,
            int slideOrder,
            string instruction,
            CancellationToken ct)
        {
            EnsureScopeFor(node);

            var slide = outline?.Slides?.FirstOrDefault(s => s.Order == slideOrder);
            if (slide == null)
                return (null, "找不到這一頁。");

            bool isCover = string.Equals(slide.Kind, "cover", StringComparison.OrdinalIgnoreCase);
            var art = DeckArtDirection.ForOutline(outline);

            // 使用者自己寫的描述優先；沒寫就用這一頁的內容（封面用整份的重點）。
            var material = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(instruction))
                material.AppendLine("使用者指定的畫面：" + instruction.Trim()).AppendLine();
            material.AppendLine(isCover
                ? BuildCoverBriefMaterial(outline!)
                : $"{slide.Heading}\n{string.Join("\n", slide.Bullets ?? Array.Empty<string>())}");

            try
            {
                node.SetLoadingHint($"正在重做第 {slideOrder} 頁的配圖");

                string brief = await BuildIllustrationBriefAsync(
                    node, material.ToString(), isCover ? "簡報封面" : "簡報內容頁", ct, art);

                if (string.IsNullOrWhiteSpace(brief))
                {
                    return (null, IllustrationStyle.IsFactualSubject(instruction)
                        ? "地圖、國旗、商標、真實地標或真人這類「查得到正確答案」的東西，生成出來不會是真的那個，反而變成錯誤資訊，所以不產生。\n\n可以改描述成氛圍或材質畫面，或自己放一張正確的圖。"
                        : "這一頁沒有適合入畫的具體主體。可以在上面的描述欄寫清楚想要的畫面再試一次。");
                }

                var imageService = new OpenAIImageService("gpt-image-2");
                using var reservation = SpendReservations.TryReserveWithinBudget(
                    "單頁配圖", ModelCostEstimator.ImageCostUsd(DeckImageSize, IllustrationQuality));
                if (reservation == null)
                    return (null, $"會超過今日預算上限（{SpendLedger.CommittedDisplay()}）");
                var image = await imageService.GenerateAsync(
                    IllustrationStyle.Compose(brief, art, isCover), DeckImageSize, ct, IllustrationQuality);

                if (!image.Success || image.PngBytes == null || image.PngBytes.Length == 0)
                    return (null, FailureExplainer.Describe(image.ErrorMessage));

                return (image.PngBytes, "");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("AgentRuntime", "單頁配圖失敗", ex);
                return (null, FailureExplainer.Describe(ex.Message));
            }
            finally
            {
                node.SetLoadingHint(null);
            }
        }

        // 智慧內容頁配圖：由 Claude 從內容頁挑「最適合用圖表達」的頁並寫配圖提示，
        // 再用 gpt-image-2 生成與內容呼應的圖、填入 slide.ImageBytes（由 PptxBuilder / DeckPdfBuilder 嵌入）。
        // 缺 OPENAI_API_KEY / 任何失敗一律略過，不影響簡報主體。
        // 內容頁配圖：與封面同一套藝術方向，色盤一致才像同一份作品。
        private async Task GenerateSlideIllustrationsAsync(
            INodeContext node, PresentationOutlinePayload outline, CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("簡報配圖");
            if (outline?.Slides == null) return;

            var contentSlides = outline.Slides
                .Where(s => string.Equals(s.Kind, "content", System.StringComparison.OrdinalIgnoreCase))
                .Where(s => s.Bullets.Count(b => !string.IsNullOrEmpty(PptxBuilder.FindKeyFigure(b))) < 2)
                .ToList();
            var sections = contentSlides
                .Select(s => (s.Order, s.Heading ?? "",
                    string.Join("；", (s.Bullets ?? System.Array.Empty<string>()).Take(3))))
                .ToList();

            var plan = await PlanIllustrationsAsync(node, sections, MaxContentSlideImages, "簡報", ct,
                DeckArtDirection.ForOutline(outline));
            if (plan.Count == 0)
            {
                NoteImageSkipped("內容頁沒有挑到適合配圖的主體（數據頁、流程頁本來就不配圖）。");
                return;
            }

            OpenAIImageService imageService;
            try { imageService = new OpenAIImageService("gpt-image-2"); }
            catch (Exception ex)
            {
                NoteImageSkipped(FailureExplainer.Describe(ex.Message));
                return;  // 沒有 OPENAI_API_KEY → 略過內容頁配圖
            }

            var art = DeckArtDirection.ForOutline(outline);

            // 每張圖要一兩分鐘，三張排隊就是五、六分鐘；同時發只等最慢的那張。
            var jobs = outline.Slides
                .Where(s => string.Equals(s.Kind, "content", System.StringComparison.OrdinalIgnoreCase))
                .Where(s => plan.TryGetValue(s.Order, out var b) && !string.IsNullOrWhiteSpace(b))
                .Select(async slide =>
                {
                    try
                    {
                        using var reservation = SpendReservations.TryReserveWithinBudget(
                            "簡報配圖", ModelCostEstimator.ImageCostUsd(DeckImageSize, IllustrationQuality));
                        if (reservation == null)
                        {
                            // 這些工作是並行的，不是迴圈——這一張放棄就好，其餘各自會得到同樣結果。
                            NoteImageSkipped($"會超過今日預算上限（{SpendLedger.CommittedDisplay()}）");
                            return;
                        }
                        var img = await imageService.GenerateAsync(
                            IllustrationStyle.Compose(plan[slide.Order], art), DeckImageSize, ct, IllustrationQuality);
                        if (img.Success && img.PngBytes != null && img.PngBytes.Length > 0)
                        {
                            slide.ImageBytes = img.PngBytes;
                            slide.ImageSubject = plan[slide.Order];
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppLog.Warn("AgentRuntime", "內容配圖生成失敗（已略過）", ex);
                        NoteImageSkipped(FailureExplainer.Describe(ex.Message));
                    }
                })
                .ToList();

            await Task.WhenAll(jobs);
        }

        // 用一次 Claude 呼叫，從內容區塊挑最多 maxCount 個適合配圖的，並寫具體英文配圖提示。
        // sections：(id, title, body) 清單（簡報＝內容頁，報告＝章節）；回傳 id → 英文提示。任何失敗回空字典。
        private async Task<Dictionary<int, string>> PlanIllustrationsAsync(
            INodeContext node,
            IReadOnlyList<(int id, string title, string body)> sections,
            int maxCount, string contextLabel, CancellationToken ct, DeckArtDirection? art = null,
            string? extraRule = null)
        {
            using var usagePurpose = UsageMeter.Purpose("配圖企劃");
            var result = new Dictionary<int, string>();
            if (sections == null || sections.Count == 0) return result;

            var sb = new System.Text.StringBuilder();
            foreach (var s in sections)
            {
                string body = (s.body ?? "").Replace("\n", " ").Trim();
                if (body.Length > 120) body = body.Substring(0, 120);
                sb.AppendLine($"[{s.id}] {s.title}：{body}".TrimEnd('：', ' '));
            }

            string planPrompt =
                $"你是{contextLabel}配圖總監。下面是內容區塊清單（每行開頭 [編號]）。\n" +
                $"最多挑 {maxCount} 個有具體景物、物件、作品或可見場景的區塊，沒有合適的就回空陣列，禁止為湊數配圖。\n" +
                (string.IsNullOrWhiteSpace(extraRule) ? "" : extraRule!.Trim() + "\n") +
                "數據、成長率、市場比較、因果或流程頁不要生成象徵圖，讓文字與原生圖表解釋資訊。禁止上升箭頭、燈泡、硬幣和無關拼貼。\n" +
                "畫「類型」不要畫「特定個體」：溫帶雨林的晨霧可以，某座山的正確外形不行；法式擺盤特寫可以，某家餐廳的招牌菜不行。\n" +
                "只有地圖、國旗、商標、統計圖表、特定真人長相這幾類才不要畫；主題是真實的地方或食物並不代表不能配圖。\n" +
                "英文提示直接描寫與該頁主張相關的單一主體；生成圖片是示意，不能冒充特定真實作品或現場照片。\n" +
                "只輸出 JSON（不要 markdown 圍欄、不要多餘文字），格式：{\"items\":[{\"id\":編號,\"prompt\":\"英文提示\"}]}。\n" +
                $"風格參考：{(art == null ? IllustrationStyle.BriefStyleGuidance : IllustrationStyle.StyleFor(art))}\n\n" +
                "=== 內容區塊 ===\n" + sb;

            var decision = new NodeExecutionDecision
            {
                RequestedAgentId = "image-agent",
                ActualAgentId = "image-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(_scope.DocumentAuthorModel(AiModels.Claude_Sonnet5)),
                ModelId = AiModelHelper.NormalizeNodeModel(_scope.DocumentAuthorModel(AiModels.Claude_Sonnet5)),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = "Illustration Plan",
                ResolverReason = "挑選適合配圖的區塊並寫具體配圖提示。",
                StatusLabel = "Auto",
                ForceSingleModel = false
            };

            try
            {
                var r = await _executeWithFallbackAsync(node, planPrompt, decision, null, false, ct);

                // 模型不能用時照實說，並用本地提示挑前兩段保底，不要整份變成純文字。
                if (!r.IsSuccess)
                {
                    NoteImageSkipped(FailureExplainer.Describe(r.ErrorMessage) + "（配圖企劃步驟）");
                    foreach (var section in sections.Take(Math.Min(2, maxCount)))
                    {
                        string local = LocalIllustrationBrief($"{section.title} {section.body}");
                        if (!string.IsNullOrWhiteSpace(local))
                            result[section.id] = local;
                    }
                    return result;
                }

                if (string.IsNullOrWhiteSpace(r.Text)) return result;

                string json = ExtractFirstJsonObject(r.Text);
                if (string.IsNullOrWhiteSpace(json)) return result;

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("items", out var arr) &&
                    arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (result.Count >= maxCount) break;
                        if (!el.TryGetProperty("id", out var o) ||
                            !el.TryGetProperty("prompt", out var p)) continue;

                        int id;
                        if (o.ValueKind == System.Text.Json.JsonValueKind.Number) id = o.GetInt32();
                        else if (!int.TryParse(o.GetString(), out id)) continue;

                        string prompt = (p.GetString() ?? "").Trim();
                        if (IllustrationStyle.IsFactualSubject(prompt))
                        {
                            AppLog.Info("AgentRuntime", $"第 {id} 段的配圖主題是可查證的真實事物，已略過配圖");
                            continue;
                        }
                        if (sections.Any(s => s.id == id) && !string.IsNullOrWhiteSpace(prompt))
                            result[id] = prompt;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLog.Warn("AgentRuntime", "附加產出步驟失敗（已略過，主答案不受影響）", ex); }

            return result;
        }

        // 從模型回傳抓出第一個完整 JSON 物件（容忍圍欄 / 前後雜訊）。
        private static string ExtractFirstJsonObject(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            return (start < 0 || end <= start) ? "" : text.Substring(start, end - start + 1);
        }

        // 報告智慧配圖數量上限（控制成本）。
        private const int MaxReportImages = 3;

        // 階段3 報告智慧配圖（同簡報規格）：解析 markdown 的 ## 章節，由 Claude 挑適合的章節，
        // 生成與內容呼應的圖、寫成 png，並在該章節標題行後插入 markdown 圖片語法
        // （DocxReportBuilder / PdfReportBuilder 會嵌入）。缺金鑰 / 任何失敗一律回原 markdown。
        private async Task<string> IllustrateReportMarkdownAsync(
            INodeContext node, string markdown, string genDir, CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("報告配圖");
            if (string.IsNullOrWhiteSpace(markdown)) return markdown;

            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // 收集 ## 章節：id = 標題行索引、body = 到下一個標題前的文字。
            var sections = new List<(int id, string title, string body)>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("## ", System.StringComparison.Ordinal)) continue;

                string title = lines[i].Substring(3).Trim();
                var bodySb = new System.Text.StringBuilder();
                for (int j = i + 1;
                     j < lines.Length &&
                     !lines[j].StartsWith("## ", System.StringComparison.Ordinal) &&
                     !lines[j].StartsWith("# ", System.StringComparison.Ordinal);
                     j++)
                {
                    bodySb.Append(lines[j]).Append(' ');
                }
                sections.Add((i, title, bodySb.ToString().Trim()));
            }

            if (sections.Count == 0) return markdown;

            var plan = await PlanIllustrationsAsync(node, sections, MaxReportImages, "報告", ct);
            if (plan.Count == 0) return markdown;

            OpenAIImageService imageService;
            try { imageService = new OpenAIImageService("gpt-image-2"); }
            catch { return markdown; }  // 沒有 OPENAI_API_KEY → 不配圖

            // 生圖、存 png，記錄要在哪一行（章節標題行）後插入圖片語法。
            var inserts = new Dictionary<int, string>();
            foreach (var (id, title, _) in sections)
            {
                if (!plan.TryGetValue(id, out var brief) || string.IsNullOrWhiteSpace(brief)) continue;

                try
                {
                    var img = await imageService.GenerateAsync(
                        IllustrationStyle.Compose(brief), "1024x1024", ct, IllustrationQuality);
                    if (!img.Success || img.PngBytes == null || img.PngBytes.Length == 0) continue;

                    System.IO.Directory.CreateDirectory(genDir);
                    string path = System.IO.Path.Combine(genDir, $"report_img_{System.Guid.NewGuid():N}.png");
                    await System.IO.File.WriteAllBytesAsync(path, img.PngBytes, ct);

                    inserts[id] = $"![{title}]({path})";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLog.Warn("AgentRuntime", "報告配圖生成失敗（已略過）", ex); }
            }

            if (inserts.Count == 0) return markdown;

            // 重組：在每個被選中的 ## 章節標題行後插入「空行 + 圖片語法 + 空行」。
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.AppendLine(lines[i]);
                if (inserts.TryGetValue(i, out var imgMd))
                {
                    sb.AppendLine();
                    sb.AppendLine(imgMd);
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 依指示改寫某一頁的文字，**行數必須一樣**。
        ///
        /// 由 Claude 排版的簡報沒有可重建的大綱，所以單頁不能重做版面；
        /// 但「改文字」不需要重建版面——把每一段的字換掉就好，位置、字級、顏色、圖片全部不動。
        /// 行數一樣是硬條件：多一行或少一行就對不回原本的段落。
        /// </summary>
        public async Task<IReadOnlyList<string>> RewriteSlideTextAsync(
            INodeContext node, IReadOnlyList<string> lines, string instruction, CancellationToken ct)
        {
            if (lines == null || lines.Count == 0)
                return Array.Empty<string>();

            EnsureScopeFor(node);
            using var usagePurpose = UsageMeter.Purpose("單頁改字");

            var numbered = string.Join("\n", lines.Select((l, i) => $"{i + 1}. {l}"));
            string prompt =
                "下面是一張投影片上的文字，每一行是一個段落。請依使用者的指示改寫。\n\n" +
                "嚴格規則：\n" +
                $"1. 輸出**正好 {lines.Count} 行**，一行對應一段，順序不變。多一行或少一行都會讓版面錯位。\n" +
                "2. 每行開頭保留「編號. 」的格式，不要加引號、不要 markdown、不要任何解釋。\n" +
                "3. 長度盡量接近原文：變太長會擠出版面外。\n" +
                "4. 指示沒有要求改的段落，原文照抄。\n\n" +
                "=== 使用者的指示 ===\n" + (instruction ?? "").Trim() + "\n\n" +
                "=== 這一頁的文字 ===\n" + numbered;

            var decision = new NodeExecutionDecision
            {
                RequestedAgentId = "general-agent",
                ActualAgentId = "general-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(_scope.DocumentAuthorModel(AiModels.Claude_Sonnet5)),
                ModelId = AiModelHelper.NormalizeNodeModel(_scope.DocumentAuthorModel(AiModels.Claude_Sonnet5)),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = "Slide Text Rewrite",
                ResolverReason = "只改這一頁的文字，不動版面。",
                StatusLabel = "Auto",
                ForceSingleModel = true,
            };

            try
            {
                var r = await _executeWithFallbackAsync(node, prompt, decision, null, false, ct);
                if (!r.IsSuccess) return Array.Empty<string>();

                var parsed = ParseNumberedLines(r.Text ?? "", lines.Count);
                if (parsed.Count != lines.Count)
                {
                    AppLog.Warn("Preview", $"單頁改字回了 {parsed.Count} 行，需要 {lines.Count} 行，放棄這次改寫");
                    return Array.Empty<string>();
                }

                return parsed;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("Preview", "單頁改字失敗", ex);
                return Array.Empty<string>();
            }
        }

        /// <summary>把「1. xxx」這種編號清單拆回純文字；數量不符就原樣回傳讓呼叫端放棄。</summary>
        private static List<string> ParseNumberedLines(string text, int expected)
        {
            var result = new List<string>();

            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\d{1,3})[.、)]\s*(.*)$");
                if (!m.Success) continue;

                result.Add(m.Groups[2].Value.Trim());
                if (result.Count == expected) break;
            }

            return result;
        }
    }
}
