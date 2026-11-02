using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// Presentation Agent v1：把 final answer 確定性地拆解成投影片大綱，並渲染成 Marp 相容的 Markdown deck。
    /// 純字串處理、無外部依賴、不額外呼叫 LLM（重用最終合成的內容以節省成本）。
    /// 解析規則：短的非條列行視為段落標題（一張投影片），條列行 / 長段落視為該投影片的重點。
    /// </summary>
    public static class PresentationOutlineBuilder
    {
        // 每張投影片最多重點數，超過則拆成「（續）」投影片，避免單頁爆滿。
        private const int MaxBulletsPerSlide = 6;

        // 被視為「段落標題」的非條列行最大長度。
        private const int MaxHeadingLength = 24;

        public sealed class Request
        {
            public string UserInput { get; init; } = "";
            public string FinalAnswer { get; init; } = "";
            public AgentWorkspace? Workspace { get; init; }
            public string PipelineId { get; init; } = "";
            public string ModelId { get; init; } = "";
            public string AgentId { get; init; } = "";
        }

        public static PresentationOutlinePayload Build(Request request)
        {
            request ??= new Request();

            string title = BuildTitle(request.UserInput);
            int requested = DetectRequestedSlideCount(request.UserInput);

            var slides = new List<PresentationSlidePayload>();
            int order = 1;

            // 封面
            slides.Add(new PresentationSlidePayload
            {
                Order = order++,
                Kind = "cover",
                Heading = title,
                Bullets = Array.Empty<string>()
            });

            // 內容投影片
            foreach (var section in ParseSections(request.FinalAnswer))
            {
                foreach (var chunk in ChunkBullets(section.Bullets, MaxBulletsPerSlide))
                {
                    bool isContinuation = chunk.IsContinuation;
                    slides.Add(new PresentationSlidePayload
                    {
                        Order = order++,
                        Kind = "content",
                        Heading = isContinuation ? section.Heading + "（續）" : section.Heading,
                        Bullets = chunk.Bullets
                    });
                }
            }

            // 資料來源投影片（若有 verified_facts 帶來源）
            var sourceBullets = BuildSourceBullets(request.Workspace);
            if (sourceBullets.Count > 0)
            {
                slides.Add(new PresentationSlidePayload
                {
                    Order = order++,
                    Kind = "sources",
                    Heading = "資料來源",
                    Bullets = sourceBullets
                });
            }

            return new PresentationOutlinePayload
            {
                Title = title,
                Topic = OneLine(request.UserInput),
                Slides = slides,
                SlideCount = slides.Count,
                RequestedSlideCount = requested,
                PipelineId = request.PipelineId,
                ModelId = request.ModelId,
                AgentId = request.AgentId,
                SourceSummary = ""
            };
        }

        /// <summary>
        /// 把大綱渲染成 Marp 相容的 Markdown deck：YAML front matter + 以 --- 分隔的投影片。
        /// 同時也是可直接閱讀的純 Markdown。
        /// </summary>
        public static string RenderMarkdownDeck(PresentationOutlinePayload outline)
            => RenderMarkdownDeck(outline, null);

        // coverImageFileName：非 null 時，在封面投影片嵌入圖片（與 .md 同資料夾，用相對檔名引用）。
        public static string RenderMarkdownDeck(PresentationOutlinePayload outline, string? coverImageFileName)
        {
            outline ??= new PresentationOutlinePayload();

            var sb = new StringBuilder();

            // Marp front matter（讓檔案可直接被 Marp 轉成投影片；對純文字閱讀也無害）。
            sb.AppendLine("---");
            sb.AppendLine("marp: true");
            sb.AppendLine("paginate: true");
            sb.AppendLine("---");
            sb.AppendLine();

            var metadata = BuildMetadataLine(outline);

            bool first = true;
            foreach (var slide in outline.Slides ?? Array.Empty<PresentationSlidePayload>())
            {
                if (!first)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }
                first = false;

                if (string.Equals(slide.Kind, "cover", StringComparison.Ordinal))
                {
                    sb.AppendLine($"# {slide.Heading}");
                    if (!string.IsNullOrWhiteSpace(outline.Topic))
                    {
                        sb.AppendLine();
                        sb.AppendLine(outline.Topic);
                    }
                    if (!string.IsNullOrWhiteSpace(coverImageFileName))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"![{slide.Heading}]({coverImageFileName})");
                    }
                    if (!string.IsNullOrWhiteSpace(metadata))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"> {metadata}");
                    }
                }
                else
                {
                    sb.AppendLine($"## {slide.Heading}");
                    if (slide.Bullets != null && slide.Bullets.Count > 0)
                    {
                        sb.AppendLine();
                        foreach (var bullet in slide.Bullets)
                            sb.AppendLine($"- {bullet}");
                    }
                }
            }

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        private static string BuildMetadataLine(PresentationOutlinePayload outline)
        {
            var parts = new List<string>
            {
                $"產生時間：{DateTime.Now:yyyy-MM-dd HH:mm}"
            };

            if (!string.IsNullOrWhiteSpace(outline.AgentId))
                parts.Add($"代理：{outline.AgentId}");
            if (!string.IsNullOrWhiteSpace(outline.ModelId))
                parts.Add($"模型：{outline.ModelId}");
            if (!string.IsNullOrWhiteSpace(outline.PipelineId))
                parts.Add($"管線：{outline.PipelineId}");

            return string.Join(" ｜ ", parts);
        }

        private sealed class Section
        {
            public string Heading = "";
            public List<string> Bullets = new List<string>();
        }

        private static IReadOnlyList<Section> ParseSections(string finalAnswer)
        {
            var sections = new List<Section>();
            Section? current = null;

            string text = (finalAnswer ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

            void EnsureSection(string defaultHeading)
            {
                if (current == null)
                {
                    current = new Section { Heading = defaultHeading };
                    sections.Add(current);
                }
            }

            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                bool hadHash = line.StartsWith("#", StringComparison.Ordinal);
                string stripped = StripMarkers(line);
                if (stripped.Length == 0)
                    continue;

                string? bulletText = TryExtractBullet(line);

                if (bulletText != null)
                {
                    EnsureSection("重點");
                    current!.Bullets.Add(bulletText);
                    continue;
                }

                bool looksLikeHeading =
                    hadHash ||
                    (stripped.Length <= MaxHeadingLength && !EndsLikeSentence(stripped));

                if (looksLikeHeading)
                {
                    current = new Section { Heading = stripped };
                    sections.Add(current);
                }
                else
                {
                    // 一般段落：當成目前段落的一條重點。
                    EnsureSection("說明");
                    current!.Bullets.Add(stripped);
                }
            }

            // 移除完全沒有重點的空段落（例如只有標題、沒有內容）。
            return sections.Where(s => s.Bullets.Count > 0).ToList();
        }

        private sealed class BulletChunk
        {
            public bool IsContinuation;
            public IReadOnlyList<string> Bullets = Array.Empty<string>();
        }

        private static IEnumerable<BulletChunk> ChunkBullets(List<string> bullets, int maxPerSlide)
        {
            if (bullets == null || bullets.Count == 0)
            {
                yield return new BulletChunk { IsContinuation = false, Bullets = Array.Empty<string>() };
                yield break;
            }

            bool firstChunk = true;
            for (int i = 0; i < bullets.Count; i += maxPerSlide)
            {
                yield return new BulletChunk
                {
                    IsContinuation = !firstChunk,
                    Bullets = bullets.Skip(i).Take(maxPerSlide).ToList()
                };
                firstChunk = false;
            }
        }

        private static IReadOnlyList<string> BuildSourceBullets(AgentWorkspace? workspace)
        {
            if (workspace == null)
                return Array.Empty<string>();

            var facts = workspace.GetByType("verified_facts")
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .SelectMany(p => p.Facts ?? Array.Empty<VerifiedFactItem>())
                .Where(f => f != null &&
                    (!string.IsNullOrWhiteSpace(f.SourceTitle) || !string.IsNullOrWhiteSpace(f.SourceUrl)))
                .GroupBy(f => (f.SourceTitle ?? "").Trim() + "|" + (f.SourceUrl ?? "").Trim())
                .Select(g => g.First())
                .Take(8)
                .ToList();

            var bullets = new List<string>();
            foreach (var f in facts)
            {
                string label = string.IsNullOrWhiteSpace(f.SourceTitle)
                    ? f.SourceUrl.Trim()
                    : f.SourceTitle.Trim();

                bullets.Add(string.IsNullOrWhiteSpace(f.SourceUrl)
                    ? label
                    : $"{label} ({f.SourceUrl.Trim()})");
            }

            return bullets;
        }

        // ---- 解析輔助 ----

        // 條列符號：- * • ・ 以及「1. / 1、 / 1)」等編號清單。
        private static readonly Regex NumberedBullet =
            new Regex(@"^\d+\s*[\.、\)]\s+", RegexOptions.Compiled);

        private static string? TryExtractBullet(string line)
        {
            if (line.Length == 0)
                return null;

            char c = line[0];
            if (c == '-' || c == '*' || c == '•' || c == '・')
            {
                string rest = line.Substring(1).TrimStart();
                return rest.Length > 0 ? rest : null;
            }

            var m = NumberedBullet.Match(line);
            if (m.Success)
            {
                string rest = line.Substring(m.Length).Trim();
                return rest.Length > 0 ? rest : null;
            }

            return null;
        }

        private static string StripMarkers(string line)
        {
            string s = line.TrimStart('#', ' ', '\t');
            // 去除粗體 / 標題殘留的星號與冒號尾巴。
            s = s.Trim().Trim('*').Trim();
            return s;
        }

        private static bool EndsLikeSentence(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;

            char last = s[s.Length - 1];
            // 句末標點（中英）或冒號代表這是內容句，不是段落標題。
            return "。．.！!？?；;，,、：:".IndexOf(last) >= 0;
        }

        private static readonly Regex SlideCountPattern =
            new Regex(@"(\d+)\s*(頁|張|頁簡報|張投影片|slides?|slide)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, int> CjkNumbers = new Dictionary<string, int>
        {
            ["三"] = 3, ["五"] = 5, ["十"] = 10, ["七"] = 7, ["八"] = 8, ["六"] = 6, ["四"] = 4, ["九"] = 9
        };

        public static int DetectRequestedSlideCount(string userInput)
        {
            string text = userInput ?? "";

            var m = SlideCountPattern.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                if (n >= 1 && n <= 50)
                    return n;
            }

            // 中文數字 + 頁/張
            foreach (var kv in CjkNumbers)
            {
                if (text.Contains(kv.Key + "頁", StringComparison.Ordinal) ||
                    text.Contains(kv.Key + "張", StringComparison.Ordinal))
                {
                    return kv.Value;
                }
            }

            return 0;
        }

        private static string BuildTitle(string userInput)
        {
            string text = OneLine(userInput);
            if (string.IsNullOrWhiteSpace(text))
                return "簡報";

            // 移除常見的祈使動詞，讓標題更像主題而非指令。
            foreach (var prefix in new[] { "幫我", "請", "麻煩", "幫忙" })
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                    text = text.Substring(prefix.Length).TrimStart();
            }

            if (text.Length > 40)
                text = text.Substring(0, 40).TrimEnd() + "…";

            return text;
        }

        private static string OneLine(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // ---- §7.1 投影片張數精準控制 ----

        /// <summary>
        /// 把大綱的「內容投影片」數量修正到剛好 <paramref name="requestedContentSlides"/> 張
        /// （封面、資料來源頁不計，原樣保留）。太多→把多餘投影片併進最後一張；太少→拆分重點最多的投影片。
        /// 內容不會遺失，只是重新分配。requested ≤ 0 時原樣返回。
        /// </summary>
        /// <summary>
        /// 把整份簡報壓到「使用者說的那個頁數」——含封面與來源頁。
        ///
        /// 使用者說五頁就是五頁。以前這個數字只算內容頁，封面與來源頁另計，
        /// 於是「5 頁」交出 7 頁；再加上排版時內容放不下又自動多拆一頁，就變成 8 頁
        ///（2026-10-27 實測）。所以這裡：① 扣掉封面與附錄頁再分配內容頁，
        /// ② 用 DeckScene 實際會排出幾頁來驗收，超出就再少一頁內容，直到排得下。
        /// </summary>
        /// <summary>
        /// 使用者說 N 頁：封面＋內容共 N 頁，最後再加 1 頁資料來源（共 N+1 頁）。
        ///
        /// 2026-10-29 使用者指示「簡報做要求頁數＋1 頁，給資料來源用」。
        /// 以前來源頁算在 N 裡面，5 頁的簡報實際只有 3 張內容，來源頁還常常被頁數上限擠掉。
        /// 做法：先把來源頁拿出來，其餘照舊壓到 N 頁，再把來源頁接回最後。沒有來源頁就是 N 頁。
        /// </summary>
        /// <summary>沒有任何外部來源時，資料來源頁上寫的話。</summary>
        public const string NoExternalSourcesNote = "本簡報沒有使用外部搜尋，內容依撰寫模型的既有知識整理，可能不是最新資訊。";

        /// <summary>
        /// 確保最後有一頁資料來源。沒有外部來源時照實寫「沒有查外部資料」，而不是讓那一頁消失。
        ///
        /// 2026-11-02 第三輪手測：手動 GPT 的簡報沒有搜尋，作者模型回傳的來源是空的，
        /// 程式就不放來源頁，「5 頁＋1 頁來源」只交出 5 頁。來源頁是使用者指定的固定頁，
        /// 而「這份內容沒有查證過」本身就是讀者該知道的來源資訊。
        /// </summary>
        public static PresentationOutlinePayload EnsureSourcesSlide(PresentationOutlinePayload outline)
        {
            if (outline?.Slides == null || outline.Slides.Count == 0)
                return outline ?? new PresentationOutlinePayload();
            if (outline.Slides.Any(x => IsKind(x, "sources")))
                return outline;

            var slides = outline.Slides.ToList();
            slides.Add(new PresentationSlidePayload
            {
                Order = slides.Count + 1,
                Kind = "sources",
                Heading = "資料來源",
                Bullets = new List<string>
                {
                    NoExternalSourcesNote,
                    "引用其中的數字、年份或事實之前，請以官方或原始資料再確認。",
                },
            });

            return new PresentationOutlinePayload
            {
                Title = outline.Title,
                Topic = outline.Topic,
                Slides = slides,
                SlideCount = slides.Count,
                RequestedSlideCount = outline.RequestedSlideCount,
                PipelineId = outline.PipelineId,
                ModelId = outline.ModelId,
                AgentId = outline.AgentId,
                SourceSummary = string.IsNullOrWhiteSpace(outline.SourceSummary) ? "未使用外部來源" : outline.SourceSummary,
                CreatedAtUtc = outline.CreatedAtUtc,
            };
        }

        public static PresentationOutlinePayload EnforcePageCountPlusSources(
            PresentationOutlinePayload outline, int requestedPages, byte[]? cover = null)
        {
            if (outline?.Slides == null || requestedPages <= 0)
                return outline ?? new PresentationOutlinePayload();

            var sources = outline.Slides.LastOrDefault(x => IsKind(x, "sources"));
            if (sources == null || outline.Slides.All(x => IsKind(x, "sources")))
                return EnforceTotalPageCount(outline, requestedPages, cover);

            var body = EnforceTotalPageCount(RemoveKind(outline, "sources"), requestedPages, cover);
            var slides = body.Slides.ToList();
            slides.Add(Reorder(sources, slides.Count + 1));

            return new PresentationOutlinePayload
            {
                Title = body.Title,
                Topic = body.Topic,
                Slides = slides,
                SlideCount = slides.Count,
                RequestedSlideCount = body.RequestedSlideCount,
                PipelineId = body.PipelineId,
                ModelId = body.ModelId,
                AgentId = body.AgentId,
                SourceSummary = body.SourceSummary,
                CreatedAtUtc = body.CreatedAtUtc,
            };
        }

        public static PresentationOutlinePayload EnforceTotalPageCount(
            PresentationOutlinePayload outline, int requestedTotalPages, byte[]? cover = null)
        {
            if (outline?.Slides == null || requestedTotalPages <= 0)
                return outline ?? new PresentationOutlinePayload();

            // 頁數少到連封面與來源頁都放不下時，要犧牲的是它們，不是內容。
            // 2026-10-28 AI 輔助審查：要求 1 頁仍輸出 3 頁（封面＋內容＋來源）——
            // 使用者說一頁就是一頁，附屬頁沒有優先權。
            var working = outline;
            int extras = working.Slides.Count(x => !IsKind(x, "content"));

            if (requestedTotalPages - extras < 1)
            {
                working = RemoveKind(working, "sources");
                extras = working.Slides.Count(x => !IsKind(x, "content"));
            }
            if (requestedTotalPages - extras < 1)
            {
                working = RemoveKind(working, "cover");
                extras = working.Slides.Count(x => !IsKind(x, "content"));
            }

            int contentTarget = Math.Max(1, requestedTotalPages - extras);

            var result = EnforceSlideCount(working, contentTarget);

            // 排版時一頁放不下會自動拆續頁，所以還要用真正的排版結果驗收。
            //
            // 注意不能靠「再少一張投影片」來收斂：合併投影片只是把重點全部擠到更少的頁面，
            // 排版照樣會把它拆回去，頁數反而更多（2026-10-27 實測：壓到 3 張反而排出 11 頁，
            // 其中一張有 35 個重點）。真正能減少頁數的是減少內容，所以這裡逐步砍每頁的重點數。
            for (int maxBullets = MaxBulletsPerSlide; maxBullets >= 1; maxBullets--)
            {
                if (DeckScene.CountPages(result.Slides, cover) <= requestedTotalPages)
                    break;

                result = CapBulletsPerSlide(result, maxBullets);
            }

            // 砍到剩一個重點還是超頁，代表問題不在「幾個重點」而在「那個重點有多長」：
            // 排版是照文字量拆頁的，一個 500 字的重點照樣會被拆成好幾頁
            //（2026-10-27 第三方審查：要求 5 頁、實際排出 8 頁，單一重點 568 字）。
            // 這時候只能縮短字數。這是最後手段，會砍掉內容，所以要留下紀錄。
            foreach (int cap in BulletLengthCaps)
            {
                if (DeckScene.CountPages(result.Slides, cover) <= requestedTotalPages)
                    break;

                result = CapBulletLength(result, cap);
            }

            int finalPages = DeckScene.CountPages(result.Slides, cover);
            if (finalPages > requestedTotalPages)
            {
                AppLog.Warn("Deck",
                    $"要求 {requestedTotalPages} 頁，壓到最小仍需 {finalPages} 頁——內容量超出版面能承載的極限。");
            }
            else if (finalPages < DeckScene.CountPages(outline.Slides, cover))
            {
                AppLog.Info("Deck",
                    $"為符合「{requestedTotalPages} 頁」已精簡內容：" +
                    $"{DeckScene.CountPages(outline.Slides, cover)} 頁 → {finalPages} 頁、" +
                    $"重點 {CountBullets(outline)} → {CountBullets(result)} 個。");
            }

            return result;
        }

        /// <summary>整份大綱的重點總數（用來量「為了守住頁數犧牲了多少內容」）。</summary>
        public static int CountBullets(PresentationOutlinePayload? outline)
            => outline?.Slides?.Sum(x => x.Bullets?.Count ?? 0) ?? 0;

        /// <summary>移除某一種附屬投影片（封面 / 來源頁），並重新編號。</summary>
        private static PresentationOutlinePayload RemoveKind(PresentationOutlinePayload outline, string kind)
        {
            var slides = outline.Slides.Where(x => !IsKind(x, kind)).ToList();
            if (slides.Count == 0) return outline;   // 不能把整份清空

            for (int i = 0; i < slides.Count; i++)
                slides[i] = Reorder(slides[i], i + 1);

            return new PresentationOutlinePayload
            {
                Title = outline.Title,
                Topic = outline.Topic,
                Slides = slides,
                SlideCount = slides.Count,
                RequestedSlideCount = outline.RequestedSlideCount,
                PipelineId = outline.PipelineId,
                ModelId = outline.ModelId,
                AgentId = outline.AgentId,
                SourceSummary = outline.SourceSummary,
                CreatedAtUtc = outline.CreatedAtUtc,
            };
        }

        /// <summary>逐步收緊的單一重點字數上限（最後手段，用來守住使用者指定的頁數）。</summary>
        private static readonly int[] BulletLengthCaps = { 220, 160, 120, 90, 70, 55, 40 };

        /// <summary>
        /// 把每個重點截到指定字數（在句讀處斷，斷不了才硬切）。
        /// </summary>
        private static PresentationOutlinePayload CapBulletLength(
            PresentationOutlinePayload outline, int maxChars)
        {
            static string Trim(string text, int max)
            {
                string t = (text ?? "").Trim();
                if (t.Length <= max) return t;

                // 盡量在標點處收尾，讀起來才不像被切斷。
                int cut = -1;
                for (int i = Math.Min(max, t.Length) - 1; i >= max / 2; i--)
                {
                    if ("，。；、）」？！,.;)".IndexOf(t[i]) >= 0) { cut = i; break; }
                }

                return cut > 0 ? t.Substring(0, cut + 1) : t.Substring(0, max) + "…";
            }

            var slides = outline.Slides.Select(s =>
            {
                if (!IsKind(s, "content") || s.Bullets == null || s.Bullets.Count == 0)
                    return s;

                return new PresentationSlidePayload
                {
                    Order = s.Order,
                    Kind = s.Kind,
                    Heading = s.Heading,
                    Bullets = s.Bullets.Select(b => Trim(b, maxChars)).ToList(),
                    ImageBytes = s.ImageBytes,
                    ImageSubject = s.ImageSubject,
                };
            }).ToList();

            return new PresentationOutlinePayload
            {
                Title = outline.Title,
                Topic = outline.Topic,
                Slides = slides,
                SlideCount = slides.Count,
                RequestedSlideCount = outline.RequestedSlideCount,
                PipelineId = outline.PipelineId,
                ModelId = outline.ModelId,
                AgentId = outline.AgentId,
                SourceSummary = outline.SourceSummary,
                CreatedAtUtc = outline.CreatedAtUtc,
            };
        }

        /// <summary>
        /// 每張內容投影片最多留幾個重點（多的捨去）。排版是照文字量拆頁的，
        /// 要讓頁數降下來只能減量——合併投影片沒有用。
        /// </summary>
        private static PresentationOutlinePayload CapBulletsPerSlide(
            PresentationOutlinePayload outline, int maxBullets)
        {
            var slides = outline.Slides.Select(s =>
            {
                if (!IsKind(s, "content") || s.Bullets == null || s.Bullets.Count <= maxBullets)
                    return s;

                return new PresentationSlidePayload
                {
                    Order = s.Order,
                    Kind = s.Kind,
                    Heading = s.Heading,
                    Bullets = s.Bullets.Take(maxBullets).ToList(),
                    ImageBytes = s.ImageBytes,
                    ImageSubject = s.ImageSubject,
                };
            }).ToList();

            return new PresentationOutlinePayload
            {
                Title = outline.Title,
                Topic = outline.Topic,
                Slides = slides,
                SlideCount = slides.Count,
                RequestedSlideCount = outline.RequestedSlideCount,
                PipelineId = outline.PipelineId,
                ModelId = outline.ModelId,
                AgentId = outline.AgentId,
                SourceSummary = outline.SourceSummary,
                CreatedAtUtc = outline.CreatedAtUtc,
            };
        }

        public static PresentationOutlinePayload EnforceSlideCount(
            PresentationOutlinePayload outline, int requestedContentSlides)
        {
            if (outline?.Slides == null || requestedContentSlides <= 0)
                return outline ?? new PresentationOutlinePayload();

            var cover = outline.Slides.FirstOrDefault(s => IsKind(s, "cover"));
            var contentDrafts = outline.Slides.Where(s => IsKind(s, "content"))
                .Select(s => new SlideDraft(s.Heading, s.Bullets))
                .ToList();
            var tail = outline.Slides
                .Where(s => !IsKind(s, "cover") && !IsKind(s, "content"))
                .ToList();

            if (contentDrafts.Count == 0)
                return outline; // 沒有可調整的內容投影片，原樣返回。

            if (contentDrafts.Count > requestedContentSlides)
                contentDrafts = MergeDown(contentDrafts, requestedContentSlides);
            else if (contentDrafts.Count < requestedContentSlides)
                contentDrafts = SplitUp(contentDrafts, requestedContentSlides);

            var finalSlides = new List<PresentationSlidePayload>();
            int order = 1;

            if (cover != null)
                finalSlides.Add(Reorder(cover, order++));

            foreach (var d in contentDrafts)
            {
                finalSlides.Add(new PresentationSlidePayload
                {
                    Order = order++,
                    Kind = "content",
                    Heading = string.IsNullOrWhiteSpace(d.Heading) ? "重點" : d.Heading,
                    Bullets = d.Bullets
                });
            }

            foreach (var s in tail)
                finalSlides.Add(Reorder(s, order++));

            return new PresentationOutlinePayload
            {
                Title = outline.Title,
                Topic = outline.Topic,
                Slides = finalSlides,
                SlideCount = finalSlides.Count,
                RequestedSlideCount = requestedContentSlides,
                PipelineId = outline.PipelineId,
                ModelId = outline.ModelId,
                AgentId = outline.AgentId,
                SourceSummary = outline.SourceSummary,
                CreatedAtUtc = outline.CreatedAtUtc
            };
        }

        private sealed class SlideDraft
        {
            public string Heading;
            public List<string> Bullets;

            public SlideDraft(string heading, IReadOnlyList<string>? bullets)
            {
                Heading = heading ?? "";
                Bullets = new List<string>(bullets ?? Array.Empty<string>());
            }
        }

        /// <summary>
        /// 標題一看就知道「刪掉會出事」的章節。
        ///
        /// 2026-10-28 第四輪 AI 輔助審查的指控：均勻取樣只是另一條**位置規則**，
        /// 「重要限制若在第四節，仍可能被刪除」。那是對的。
        /// 系統無法從使用者的需求推斷哪一節必要（那是本專題未解的問題），
        /// 但它**認得自己產生的章節標題**——結論、建議、限制、風險這幾種名字，
        /// 刪掉的代價遠高於刪掉一節背景介紹。所以保留依據從「純位置」改成「標題語意優先，位置次之」。
        ///
        /// 這不是完整的解法：使用者真正要的重點若掛在「天候判讀」這種中性標題下，依然救不回來。
        /// </summary>
        private static readonly string[] ProtectedHeadingHints =
        {
            "結論", "總結", "小結", "綜上", "重點回顧",
            "建議", "行動", "下一步", "後續",
            "限制", "風險", "注意", "警告", "免責",
            "來源", "參考", "引用",
        };

        internal static bool IsProtectedHeading(string? heading, IReadOnlyList<string>? extraHints = null)
        {
            string h = (heading ?? "").Trim();
            if (h.Length == 0) return false;

            foreach (string hint in ProtectedHeadingHints)
                if (h.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;

            foreach (string hint in extraHints ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(hint)
                    && h.Contains(hint.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// 把 N 張內容投影片壓成 target 張。
        ///
        /// 關鍵是「留哪幾張」，這一段被連續三輪審查各打穿一次：
        ///   1. 原本是 Take(target)——留前面幾張、其餘倒進尾巴再被字數上限截掉，
        ///      等於整份簡報的後半段消失（九節壓五頁只剩第 1、2、3 節）。
        ///   2. 改成均勻取樣＋固定保留最末節之後，結論救回來了，
        ///      但第四輪指出這仍然只是位置規則：排在中段的「風險與限制」照樣被刪。
        ///   3. 現在的順序是：**先挑標題語意上不能刪的章節**（結論／建議／限制／風險／來源），
        ///      再用均勻取樣把剩下的額度填滿，並且永遠保留最末節。
        ///
        /// 沒被選中的節，其內容併入前一張被保留的投影片，次序不亂。
        /// </summary>
        private static List<SlideDraft> MergeDown(
            List<SlideDraft> drafts, int target, IReadOnlyList<string>? protectedHints = null)
        {
            if (target >= drafts.Count)
                return drafts;

            var picks = new List<int>();

            // ① 最末節永遠留（結論／下一步多半在那裡）。
            picks.Add(drafts.Count - 1);

            // ② 標題語意上不能刪的章節，由後往前收——靠後的通常是結論與建議。
            if (picks.Count < target)
                for (int i = drafts.Count - 2; i >= 0 && picks.Count < target; i--)
                    if (IsProtectedHeading(drafts[i].Heading, protectedHints))
                        picks.Add(i);

            // ③ 剩下的額度用均勻取樣填滿，讓整份簡報的脈絡不只剩尾巴。
            for (int i = 0; i < target && picks.Count < target; i++)
            {
                int idx = target == 1
                    ? drafts.Count - 1
                    : (int)Math.Round(i * (drafts.Count - 1.0) / (target - 1));
                if (!picks.Contains(idx))
                    picks.Add(idx);
            }

            // ④ 四捨五入撞號導致還不夠時，從前往後補滿。
            for (int i = 0; picks.Count < target && i < drafts.Count; i++)
                if (!picks.Contains(i))
                    picks.Add(i);

            picks.Sort();

            var kept = new List<SlideDraft>();
            foreach (int idx in picks)
                kept.Add(new SlideDraft(drafts[idx].Heading, new List<string>(drafts[idx].Bullets)));

            // 沒被選中的節：併進「它前面最近的那一張保留投影片」。
            for (int i = 0; i < drafts.Count; i++)
            {
                if (picks.Contains(i)) continue;

                int slot = 0;
                for (int k = 0; k < picks.Count; k++)
                    if (picks[k] < i) slot = k;

                if (!string.IsNullOrWhiteSpace(drafts[i].Heading))
                    kept[slot].Bullets.Add(drafts[i].Heading);
                kept[slot].Bullets.AddRange(drafts[i].Bullets);
            }

            return kept;
        }

        /// <summary>
        /// 這次壓縮之後，原本的內容標題還有哪些沒被留下來。
        /// 用途是**對使用者誠實**：被刪掉的是哪幾個主題，比「少了幾個重點」有用得多。
        /// </summary>
        public static IReadOnlyList<string> DroppedHeadings(
            PresentationOutlinePayload before, PresentationOutlinePayload after)
        {
            if (before?.Slides == null || after?.Slides == null)
                return Array.Empty<string>();

            var kept = after.Slides
                .Where(x => IsKind(x, "content"))
                .Select(x => (x.Heading ?? "").Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return before.Slides
                .Where(x => IsKind(x, "content"))
                .Select(x => (x.Heading ?? "").Trim())
                .Where(h => h.Length > 0 && !kept.Contains(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 太少：反覆把「重點最多」的投影片對半拆，直到達到 target；真的拆不動才補空白「補充」頁。
        private static List<SlideDraft> SplitUp(List<SlideDraft> drafts, int target)
        {
            int guard = 0;
            while (drafts.Count < target && guard++ < 500)
            {
                int idx = -1, max = 1;
                for (int i = 0; i < drafts.Count; i++)
                {
                    if (drafts[i].Bullets.Count > max)
                    {
                        max = drafts[i].Bullets.Count;
                        idx = i;
                    }
                }

                if (idx < 0)
                    break; // 全是 ≤1 重點，無法再拆。

                var src = drafts[idx];
                int half = src.Bullets.Count / 2;

                var second = new SlideDraft(
                    src.Heading.EndsWith("（續）", StringComparison.Ordinal) ? src.Heading : src.Heading + "（續）",
                    src.Bullets.Skip(half).ToList());
                src.Bullets = src.Bullets.Take(half).ToList();

                drafts.Insert(idx + 1, second);
            }

            while (drafts.Count < target)
                drafts.Add(new SlideDraft("補充", Array.Empty<string>()));

            return drafts;
        }

        private static bool IsKind(PresentationSlidePayload s, string kind)
            => string.Equals(s?.Kind, kind, StringComparison.OrdinalIgnoreCase);

        private static PresentationSlidePayload Reorder(PresentationSlidePayload s, int order)
            => new PresentationSlidePayload
            {
                Order = order,
                Kind = s.Kind,
                Heading = s.Heading,
                Bullets = s.Bullets,
                ImageBytes = s.ImageBytes,
                ImageSubject = s.ImageSubject,
            };
    }
}
