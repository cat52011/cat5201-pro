using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 簡報的「藝術方向」：依主題自動選一套視覺語言（色盤、字體、版面個性），
    /// 內建排版器與 Claude 文件技能都吃同一份設定，所以兩條路徑的風格判斷一致。
    ///
    /// 為什麼需要：舊版所有主題都長一樣——深藍標題列＋條列文字，
    /// 塔斯馬尼亞旅遊簡報和財報簡報視覺上毫無差別，看起來就是「文字簡報」。
    /// </summary>
    public sealed class DeckArtDirection
    {
        public string Id { get; private init; } = "modern";
        /// <summary>中文名稱（決策窗／說明用）。</summary>
        public string DisplayName { get; private init; } = "現代簡約";
        /// <summary>給 Claude 文件技能的藝術指導（英文，技能的設計準則用英文最準）。</summary>
        public string DirectionForModel { get; private init; } = "";

        public string Background { get; private init; } = "FFFFFF";
        /// <summary>主要文字色。</summary>
        public string Ink { get; private init; } = "111111";
        /// <summary>次要／說明文字色。</summary>
        public string Muted { get; private init; } = "6B7280";
        public string Accent { get; private init; } = "1F5AE0";
        /// <summary>反白頁（章節／引述）的底色。</summary>
        public string InverseBackground { get; private init; } = "111111";
        public string InverseInk { get; private init; } = "FFFFFF";

        /// <summary>西文標題字體（CJK 一律用 EastAsianFont）。</summary>
        public string HeadingLatinFont { get; private init; } = "Segoe UI Semibold";
        public string BodyLatinFont { get; private init; } = "Segoe UI";
        /// <summary>中文字體：Windows 內建，PowerPoint 開啟時才不會掉字。</summary>
        public string EastAsianFont { get; private init; } = "Microsoft JhengHei";

        /// <summary>標題是否用字距放大的全大寫小標（雜誌／展覽風）。</summary>
        public bool UsesKicker { get; private init; } = true;
        /// <summary>封面是否走深色滿版（展覽／科技風）。</summary>
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
                "design", "brand", "typography", "editorial", "fashion", "architecture", "aesthetic", "creative"
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

        /// <summary>依主題文字挑選藝術方向；都沒命中時用現代簡約。</summary>
        public static DeckArtDirection ForTopic(string? topic)
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
                if (choice.Score > 0) return ById(choice.Id);
            }

            return ById("modern");
        }

        public static DeckArtDirection ById(string id) => id switch
        {
            "gallery" => new DeckArtDirection
            {
                Id = "gallery",
                DisplayName = "藝術展覽",
                Background = "0E0E0E",
                Ink = "F3F1EC",
                Muted = "8E877C",
                Accent = "C8A96A",
                InverseBackground = "F3F1EC",
                InverseInk = "0E0E0E",
                HeadingLatinFont = "Georgia",
                BodyLatinFont = "Segoe UI",
                DarkCover = true,
                DirectionForModel =
                    "Art-exhibition catalogue: near-black canvas (#0E0E0E), warm off-white type (#F3F1EC), muted brass accent (#C8A96A). " +
                    "Gallery-wall spacing — enormous margins, one idea per slide, small letterspaced uppercase labels above serif display headings. " +
                    "Images full-bleed or hung like framed works with generous surrounding space; captions small, quiet and precise. No boxes, no shadows, no bullet clutter."
            },
            "editorial" => new DeckArtDirection
            {
                Id = "editorial",
                DisplayName = "雜誌編輯",
                Background = "F4F1EA",
                Ink = "14110E",
                Muted = "6B655C",
                Accent = "B3462F",
                InverseBackground = "14110E",
                InverseInk = "F4F1EA",
                HeadingLatinFont = "Georgia",
                BodyLatinFont = "Segoe UI",
                DirectionForModel =
                    "Magazine editorial: warm paper ground (#F4F1EA), near-black ink (#14110E), vermilion accent (#B3462F). " +
                    "Strong typographic hierarchy — oversized serif display headlines, small letterspaced uppercase kickers, hairline rules, generous columns and whitespace. " +
                    "Asymmetric layouts, pull quotes set large, drop-cap or oversized numerals for lists. Editorial restraint: no icon clutter, no drop shadows."
            },
            "nature" => new DeckArtDirection
            {
                Id = "nature",
                DisplayName = "自然旅遊",
                Background = "F6F4EF",
                Ink = "1C2B23",
                Muted = "6E7B71",
                Accent = "2F6B4F",
                InverseBackground = "1C2B23",
                InverseInk = "F6F4EF",
                HeadingLatinFont = "Georgia",
                BodyLatinFont = "Segoe UI",
                DirectionForModel =
                    "Travel/nature editorial: sand-paper ground (#F6F4EF), deep forest ink (#1C2B23), moss accent (#2F6B4F) with a warm clay secondary (#C2703D). " +
                    "Full-bleed landscape imagery with dark scrims for legibility, wide horizon-like rules, airy type, place-name kickers. Calm, spacious, documentary feel."
            },
            "finance" => new DeckArtDirection
            {
                Id = "finance",
                DisplayName = "財經數據",
                Background = "FFFFFF",
                Ink = "10233F",
                Muted = "5A6B82",
                Accent = "C8A23C",
                InverseBackground = "10233F",
                InverseInk = "FFFFFF",
                HeadingLatinFont = "Segoe UI Semibold",
                BodyLatinFont = "Segoe UI",
                DirectionForModel =
                    "Institutional finance: white ground, deep navy ink (#10233F), restrained gold accent (#C8A23C). " +
                    "Data-forward — oversized key figures with small captions, clean tables with hairline rules, charts with direct labels instead of legends. " +
                    "Every number carries its unit, period and source. Sober, precise, no decoration."
            },
            "tech" => new DeckArtDirection
            {
                Id = "tech",
                DisplayName = "科技產品",
                Background = "0B0F14",
                Ink = "E8EDF2",
                Muted = "7C8794",
                Accent = "4CC2FF",
                InverseBackground = "E8EDF2",
                InverseInk = "0B0F14",
                HeadingLatinFont = "Segoe UI Semibold",
                BodyLatinFont = "Segoe UI",
                DarkCover = true,
                DirectionForModel =
                    "Product-launch dark theme: near-black ground (#0B0F14), cool light type (#E8EDF2), electric blue accent (#4CC2FF). " +
                    "Big statement headlines, monospaced micro-labels, diagram-first slides (flows, architectures) drawn with clean geometry. Tight grid, high contrast, minimal text."
            },
            "academic" => new DeckArtDirection
            {
                Id = "academic",
                DisplayName = "學術研究",
                Background = "FBFAF7",
                Ink = "1F2937",
                Muted = "6B7280",
                Accent = "2F4858",
                InverseBackground = "1F2937",
                InverseInk = "FBFAF7",
                HeadingLatinFont = "Georgia",
                BodyLatinFont = "Segoe UI",
                DirectionForModel =
                    "Academic clarity: warm white ground (#FBFAF7), slate ink (#1F2937), teal-slate accent (#2F4858). " +
                    "Numbered sections, serif headings, figure-and-caption discipline, tables with hairline rules, citations in small type. Structure over decoration."
            },
            _ => new DeckArtDirection
            {
                Id = "modern",
                DisplayName = "現代簡約",
                Background = "FAFAF8",
                Ink = "16181D",
                Muted = "6B7280",
                Accent = "1F5AE0",
                InverseBackground = "16181D",
                InverseInk = "FAFAF8",
                HeadingLatinFont = "Segoe UI Semibold",
                BodyLatinFont = "Segoe UI",
                DirectionForModel =
                    "Modern minimal: off-white ground (#FAFAF8), near-black ink (#16181D), confident blue accent (#1F5AE0). " +
                    "Large type hierarchy, generous whitespace, one idea per slide, oversized key numbers, hairline rules, asymmetric layouts. Never a wall of bullets."
            }
        };

        /// <summary>給文件技能的完整藝術指導段落（含通則 + 這次選定的方向）。</summary>
        public string BuildModelBrief(string topic)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"【藝術方向：{DisplayName}】（依主題「{(topic ?? "").Trim()}」自動選定；若你判斷另一種語言更貼切，可以調整，但整份必須一致）");
            sb.AppendLine(DirectionForModel);
            return sb.ToString();
        }

        /// <summary>已知的藝術方向 ID（測試／設定用）。</summary>
        public static IReadOnlyList<string> AllIds { get; } =
            new[] { "modern", "editorial", "gallery", "nature", "finance", "tech", "academic" };
    }
}
