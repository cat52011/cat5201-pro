using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 簡報的「藝術方向」：依主題自動選一套視覺語言（家族、色盤、字體、版面個性），
    /// 內建排版器與 Claude 文件技能都吃同一份設定，所以兩條路徑的風格判斷一致。
    ///
    /// 2026-09-24 收斂成兩大家族（使用者指定的兩種風格）：
    ///   editorial＝藝術／時尚雜誌：明體／襯線大標、字距拉開的小標、髮絲線、超大留白、大頁碼。
    ///   corporate＝商業科技／銀行業正式：嚴謹格線、頁首頁尾、半粗黑標題、列間細線、克制的強調色。
    /// 家族決定「排版語言」，Id 決定「色盤與圖像語言」，所以同一家族裡展覽風和雜誌風仍然看得出差別。
    /// </summary>
    public sealed record DeckArtDirection
    {
        public string Id { get; private init; } = "modern";
        /// <summary>中文名稱（決策窗／說明用）。</summary>
        public string DisplayName { get; private init; } = "現代簡約";
        /// <summary>版面家族：editorial（雜誌）或 corporate（商業正式）。</summary>
        public string Family { get; private init; } = "corporate";
        /// <summary>給 Claude 文件技能的藝術指導（英文，技能的設計準則用英文最準）。</summary>
        public string DirectionForModel { get; private init; } = "";

        public string Background { get; private init; } = "FFFFFF";
        /// <summary>主要文字色。</summary>
        public string Ink { get; private init; } = "111111";
        /// <summary>次要／說明文字色。</summary>
        public string Muted { get; private init; } = "6B7280";
        public string Accent { get; private init; } = "1F5AE0";
        /// <summary>反白頁（封面／引述）的底色。</summary>
        public string InverseBackground { get; private init; } = "111111";
        public string InverseInk { get; private init; } = "FFFFFF";
        /// <summary>反白頁上的次要文字色（不設定時＝主文字色，會太搶）。</summary>
        public string InverseMuted { get; private init; } = "B8BCC4";

        /// <summary>標題字體（西文）。CJK 用 DisplayEaFont。</summary>
        public string DisplayLatinFont { get; private init; } = "Segoe UI Semibold";
        /// <summary>標題字體（中文）：只用 Windows 內建，PowerPoint 開啟時才不會掉字。</summary>
        public string DisplayEaFont { get; private init; } = "Microsoft JhengHei";
        public string BodyLatinFont { get; private init; } = "Segoe UI";
        public string BodyEaFont { get; private init; } = "Microsoft JhengHei";

        /// <summary>相容舊呼叫端（pptx 佈景主題字體）。</summary>
        public string HeadingLatinFont => DisplayLatinFont;
        public string EastAsianFont => BodyEaFont;

        /// <summary>標題是否用字距放大的小標（雜誌／展覽風）。</summary>
        public bool UsesKicker => Family == "editorial";
        /// <summary>封面是否走深色滿版。</summary>
        public bool DarkCover { get; private init; }

        // ===== 主題判斷 =====

        private static readonly (string Id, string[] Keywords)[] Rules =
        {
            ("gallery", new[]
            {
                "展覽", "策展", "美術館", "博物館", "藝術", "藝廊", "作品集", "攝影展", "個展", "雙年展", "裝置",
                "exhibition", "gallery", "museum", "curator", "portfolio", "biennale"
            }),
            ("editorial", new[]
            {
                "設計", "品牌", "視覺", "平面", "字體", "排版", "時尚", "美學", "建築", "室內", "攝影", "文化", "雜誌",
                // 高檔餐飲／生活風格：這類主題天生就是雜誌版面，不該落到泛用的商業簡約風。
                "美食", "餐廳", "餐飲", "料理", "餐點", "菜單", "主廚", "甜點", "烘焙", "品酒", "葡萄酒", "咖啡",
                "米其林", "精品", "香水", "珠寶", "旅宿", "飯店", "策展級",
                "design", "brand", "typography", "editorial", "fashion", "architecture", "aesthetic", "creative",
                "cuisine", "restaurant", "gastronomy", "dining", "chef", "michelin", "pastry", "patisserie",
                "wine", "sommelier", "luxury", "hospitality"
            }),
            ("nature", new[]
            {
                "旅遊", "旅行", "自然", "生態", "海島", "島嶼", "海岸", "山林", "森林", "國家公園", "環境", "永續", "氣候", "農業", "野生",
                "travel", "nature", "island", "ecology", "wildlife", "sustainable", "climate", "tourism"
            }),
            ("finance", new[]
            {
                "財報", "股價", "營收", "投資", "市場", "獲利", "毛利", "估值", "基金", "經濟", "預算", "成本", "產業分析",
                "finance", "revenue", "earnings", "investor", "valuation", "market", "budget", "roi"
            }),
            ("tech", new[]
            {
                "ai", "人工智慧", "模型", "演算法", "系統", "軟體", "程式", "資料", "平台", "架構", "雲端", "資安", "自動化",
                "machine learning", "software", "platform", "infrastructure", "data", "api", "agent"
            }),
            ("academic", new[]
            {
                "研究", "論文", "文獻", "實驗", "教學", "課程", "學術", "理論", "調查", "方法論",
                "research", "thesis", "curriculum", "academic", "study", "methodology"
            }),
        };

        /// <summary>
        /// 這一份簡報的配色變體（0 ＝ 原始配色）。由 ForOutline 依簡報身分決定，
        /// 同一份簡報每次重算都一樣，不同簡報才會不一樣。
        /// </summary>
        public int Variant { get; private init; }

        /// <summary>
        /// 依「整份簡報」挑選藝術方向——包含配色變體。
        ///
        /// 只看主題字串的話，主題相近的簡報每一次都會長得一模一樣：
        /// 使用者連做三份餐飲簡報，拿到三份同樣的米色＋朱紅。
        /// 變體的種子取自標題、副標與建立時間，所以：
        ///   · 同一份簡報重新渲染（預覽 / pptx / pdf 三條路徑）結果一致，單頁編輯也不會換色；
        ///   · 不同時間做的兩份簡報就算主題相同，也會拿到不同的配色。
        /// </summary>
        public static DeckArtDirection ForOutline(PresentationOutlinePayload? outline)
        {
            if (outline == null) return ForTopic(null);

            return ForTopic(
                $"{outline.Title} {outline.Topic}",
                VariantSeed($"{outline.Title}|{outline.Topic}|{outline.CreatedAtUtc.Ticks}"));
        }

        /// <summary>穩定的非負雜湊（string.GetHashCode 每次啟動都不同，不能用）。</summary>
        private static int VariantSeed(string key)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in key ?? "")
                    hash = hash * 31 + c;
                return Math.Abs(hash == int.MinValue ? 0 : hash);
            }
        }

        /// <summary>依主題文字挑選藝術方向；都沒命中時用現代簡約。</summary>
        public static DeckArtDirection ForTopic(string? topic) => ForTopic(topic, 0);

        /// <summary>依主題文字挑選藝術方向，並套用第 <paramref name="seed"/> 號配色變體。</summary>
        public static DeckArtDirection ForTopic(string? topic, int seed)
        {
            string text = (topic ?? "").ToLowerInvariant();
            // Engineering design is not a visual-design brief; ASCII keywords must be whole words.
            foreach (string phrase in new[] { "系統設計", "軟體設計", "程式設計", "資料庫設計" })
                text = text.Replace(phrase, phrase.Replace("設計", ""));
            text = text.Replace("software design", "software").Replace("system design", "system");

            if (!string.IsNullOrWhiteSpace(text))
            {
                var choice = Rules.Select(rule => (rule.Id, Score: rule.Keywords.Count(k => k.Any(c => c > 127)
                    ? text.Contains(k, StringComparison.OrdinalIgnoreCase)
                    : Regex.IsMatch(text, @"(?<![a-z])" + Regex.Escape(k) + @"(?![a-z])", RegexOptions.IgnoreCase))))
                    .OrderByDescending(rule => rule.Score).First();
                if (choice.Score > 0) return WithVariant(ById(choice.Id), seed);
            }

            return WithVariant(ById("modern"), seed);
        }

        /// <summary>
        /// 每個藝術方向的替代強調色與反白底色。
        ///
        /// 只換強調色與反白頁底色，不動背景與主文字色——那兩個決定了「這是哪一種簡報」，
        /// 換掉會變成另一套設計；強調色換掉只是同一套設計的另一個調子。
        /// </summary>
        private static readonly Dictionary<string, (string Accent, string InverseBackground)[]> Palettes = new()
        {
            ["gallery"]  = new[] { ("C9A227", "EFEBE3"), ("B5563C", "1A1715"), ("8C9A7E", "EFEBE3") },
            ["editorial"]= new[] { ("C2432C", "141210"), ("1F4E5F", "141210"), ("A8761F", "2A2320") },
            ["nature"]   = new[] { ("2F6B4F", "1B2A22"), ("3E6B86", "16242C"), ("8A6B2F", "25231B") },
            ["finance"]  = new[] { ("B08D3F", "0E2441"), ("1F5AE0", "0E2441"), ("1F6B57", "10261C") },
            ["tech"]     = new[] { ("4CC2FF", "E9EEF3"), ("7C8BFF", "E9EEF3"), ("48E0A0", "E9EEF3") },
            ["academic"] = new[] { ("2F4858", "1F2937"), ("7A4A2F", "2B2320"), ("3A5A3F", "1E2B22") },
            ["modern"]   = new[] { ("1F5AE0", "16181D"), ("D4502E", "16181D"), ("117A68", "10201D") },
        };

        private static DeckArtDirection WithVariant(DeckArtDirection art, int seed)
        {
            if (seed <= 0 || !Palettes.TryGetValue(art.Id, out var palette) || palette.Length <= 1)
                return art;

            int index = seed % palette.Length;
            if (index == 0) return art;

            var (accent, inverseBackground) = palette[index];

            return art with
            {
                Variant = index,
                Accent = accent,
                InverseBackground = inverseBackground,
            };
        }

        public static DeckArtDirection ById(string id) => id switch
        {
            // ── 藝術／時尚雜誌家族 ──
            "gallery" => new DeckArtDirection
            {
                Id = "gallery",
                DisplayName = "藝術展覽",
                Family = "editorial",
                Background = "0D0D0C",
                Ink = "EFEBE3",
                Muted = "938B7E",
                Accent = "C9A227",
                InverseBackground = "EFEBE3",
                InverseInk = "0D0D0C",
                InverseMuted = "6E675C",
                DisplayLatinFont = "Georgia",
                DisplayEaFont = "PMingLiU",
                DarkCover = true,
                DirectionForModel =
                    "Art-exhibition catalogue: near-black canvas (#0D0D0C), warm off-white type (#EFEBE3), brass accent (#C9A227). " +
                    "Gallery-wall spacing — enormous margins, one idea per slide, small letterspaced labels above serif (Chinese: Mincho/明體) display headings. " +
                    "Images hung like framed works with generous surrounding space; captions small, quiet and precise. Hairline rules only. No boxes, no shadows, no bullet clutter."
            },
            "editorial" => new DeckArtDirection
            {
                Id = "editorial",
                DisplayName = "雜誌編輯",
                Family = "editorial",
                Background = "F2EEE6",
                Ink = "141210",
                Muted = "7A7268",
                Accent = "C2432C",
                InverseBackground = "141210",
                InverseInk = "F2EEE6",
                InverseMuted = "A79E92",
                DisplayLatinFont = "Georgia",
                DisplayEaFont = "PMingLiU",
                DirectionForModel =
                    "Fashion/culture magazine: warm paper ground (#F2EEE6), near-black ink (#141210), vermilion accent (#C2432C). " +
                    "Strong typographic hierarchy — oversized serif (Chinese: Mincho/明體) headlines, small letterspaced kickers, hairline rules, wide columns and whitespace. " +
                    "Asymmetric layouts, pull quotes set large, oversized numerals for lists, big folio numbers. Editorial restraint: no icon clutter, no drop shadows, no boxes."
            },
            "nature" => new DeckArtDirection
            {
                Id = "nature",
                DisplayName = "自然旅遊",
                Family = "editorial",
                Background = "F4F2EB",
                Ink = "1B2A22",
                Muted = "6F7C72",
                Accent = "2F6B4F",
                InverseBackground = "1B2A22",
                InverseInk = "F4F2EB",
                InverseMuted = "9AAAA0",
                DisplayLatinFont = "Georgia",
                DisplayEaFont = "PMingLiU",
                DirectionForModel =
                    "Travel/nature editorial: sand-paper ground (#F4F2EB), deep forest ink (#1B2A22), moss accent (#2F6B4F). " +
                    "Large landscape imagery with generous margins, horizon-like hairline rules, airy serif type, place-name kickers. Calm, spacious, documentary feel."
            },

            // ── 商業科技／銀行業正式家族 ──
            "finance" => new DeckArtDirection
            {
                Id = "finance",
                DisplayName = "財經數據",
                Family = "corporate",
                Background = "FFFFFF",
                Ink = "0E2441",
                Muted = "64748B",
                Accent = "B08D3F",
                InverseBackground = "0E2441",
                InverseInk = "FFFFFF",
                InverseMuted = "9FB0C6",
                DisplayLatinFont = "Segoe UI Semibold",
                DisplayEaFont = "Microsoft JhengHei",
                DirectionForModel =
                    "Institutional finance deck (bank/annual-report register): white ground, deep navy ink (#0E2441), restrained gold accent (#B08D3F). " +
                    "Strict grid, ruled header and footer with page numbers, numbered sections, hairline separators between rows. " +
                    "Data-forward — oversized key figures with small captions, tables with hairline rules, direct labels instead of legends. " +
                    "Every number carries its unit, period and source. Sober, precise, no decoration."
            },
            "tech" => new DeckArtDirection
            {
                Id = "tech",
                DisplayName = "科技產品",
                Family = "corporate",
                Background = "0A0E13",
                Ink = "E9EEF3",
                Muted = "8493A2",
                Accent = "4CC2FF",
                InverseBackground = "E9EEF3",
                InverseInk = "0A0E13",
                InverseMuted = "5B6672",
                DisplayLatinFont = "Segoe UI Semibold",
                DisplayEaFont = "Microsoft JhengHei",
                DarkCover = true,
                DirectionForModel =
                    "Enterprise product-launch dark theme: near-black ground (#0A0E13), cool light type (#E9EEF3), electric blue accent (#4CC2FF). " +
                    "Tight grid, ruled header and footer, big statement headlines, small letterspaced labels, diagram-first slides drawn with clean geometry. High contrast, minimal text."
            },
            "academic" => new DeckArtDirection
            {
                Id = "academic",
                DisplayName = "學術研究",
                Family = "corporate",
                Background = "FBFAF7",
                Ink = "1F2937",
                Muted = "6B7280",
                Accent = "2F4858",
                InverseBackground = "1F2937",
                InverseInk = "FBFAF7",
                InverseMuted = "A9B2BC",
                DisplayLatinFont = "Georgia",
                DisplayEaFont = "Microsoft JhengHei",
                DirectionForModel =
                    "Academic clarity: warm white ground (#FBFAF7), slate ink (#1F2937), teal-slate accent (#2F4858). " +
                    "Numbered sections, ruled header and footer, figure-and-caption discipline, tables with hairline rules, citations in small type. Structure over decoration."
            },
            _ => new DeckArtDirection
            {
                Id = "modern",
                DisplayName = "現代簡約",
                Family = "corporate",
                Background = "FAFAF8",
                Ink = "16181D",
                Muted = "6B7280",
                Accent = "1F5AE0",
                InverseBackground = "16181D",
                InverseInk = "FAFAF8",
                InverseMuted = "A6ABB4",
                DisplayLatinFont = "Segoe UI Semibold",
                DisplayEaFont = "Microsoft JhengHei",
                DirectionForModel =
                    "Modern corporate minimal: off-white ground (#FAFAF8), near-black ink (#16181D), confident blue accent (#1F5AE0). " +
                    "Strict grid, ruled header and footer with page numbers, large type hierarchy, generous whitespace, one idea per slide, oversized key numbers. Never a wall of bullets."
            }
        };

        /// <summary>給文件技能的完整藝術指導段落（含通則 + 這次選定的方向）。</summary>
        public string BuildModelBrief(string topic)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"【藝術方向：{DisplayName}】（依主題「{(topic ?? "").Trim()}」自動選定；若你判斷另一種語言更貼切，可以調整，但整份必須一致）");
            sb.AppendLine(DirectionForModel);

            // 配色變體換掉了強調色，但 DirectionForModel 裡寫的還是原本那個色碼。
            // 不覆寫的話，文件技能排出來的版本會跟內建排版器、預覽、PDF 三者不同色。
            if (Variant > 0)
            {
                sb.AppendLine(
                    $"Accent override for this deck: #{Accent} (use this instead of the accent colour named above); " +
                    $"inverse/quote panels use #{InverseBackground}. Everything else stays as described.");
            }

            return sb.ToString();
        }

        /// <summary>已知的藝術方向 ID（測試／設定用）。</summary>
        public static IReadOnlyList<string> AllIds { get; } =
            new[] { "modern", "editorial", "gallery", "nature", "finance", "tech", "academic" };
    }
}
