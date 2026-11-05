using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cat5201
{
    /// <summary>
    /// 簡報 / 報告「配圖」的視覺風格與提示詞組裝。
    ///
    /// 2026-09-25 修正兩個真實問題：
    ///  ① 圖片模型畫出來的手繪塗鴉感（扁平向量、卡通、草圖）放進簡報就是廉價，
    ///     所以風格一律改成「有美術指導的畫面」：材質靜物、光影研究、幾何構成，並明確排除手繪／卡通／clip art。
    ///  ② 真實而且查得到的東西（地圖、國旗、商標、真實地標輪廓、真人、圖表）不能用生成圖——
    ///     模型畫出來的塔斯馬尼亞地圖不是塔斯馬尼亞，放進簡報就是錯誤資訊。這類主題一律不配圖。
    /// 風格跟著 <see cref="DeckArtDirection"/> 走，圖才會像設計的一部分而不是貼上去的插圖。
    /// </summary>
    public static class IllustrationStyle
    {
        /// <summary>圖片模型實務上有效的硬性排除（亂碼文字、浮水印、廉價畫風）。</summary>
        private const string NoTextTail =
            "No text, no words, no letters, no numbers, no watermark, no logo. " +
            "Not hand-drawn, not a sketch, not a doodle, not cartoon, not clip art, not a flat vector icon illustration.";

        /// <summary>
        /// 預設風格：先看主體是什麼，再決定要拍它還是隱喻它。
        ///
        /// 2026-10-27 修正：原本一律是「材質靜物／棚拍抽象構成」，於是一份冰島極光簡報
        /// 生出了三張棚拍玻璃雕塑——畫質很好，但沒有一張是極光。
        /// 極光、冰川、食物、建築這些本來就拍得到的主體，讀者期待的是那個東西的照片；
        /// 抽象材質靜物只該用在「成長、流程、風險」這種拍不到的概念上。
        /// 排除廉價畫風（手繪、卡通、clip art）的規則不變，那條當初是對的。
        /// </summary>
        public const string StyleSuffix =
            "If the subject above is something real that could be photographed — a place, a natural phenomenon, " +
            "weather or sky, an animal, a plant, food, a building, an object or a material — then produce a high-end " +
            "photograph of that subject itself, in the manner of a magazine feature or fine-art print. " +
            "Only when the subject is an abstract idea with nothing to photograph should you instead build an " +
            "art-directed still life from real materials (paper, glass, metal, stone, fabric) or architectural geometry. " +
            "Either way: considered lighting, restrained palette, generous negative space, high production value, " +
            "editorial magazine quality. " + NoTextTail;

        /// <summary>給「圖片 brief 萃取」LLM 的風格指示（中文，讓 Claude 寫出符合此風格的具體畫面）。</summary>
        public const string BriefStyleGuidance =
            "先判斷這一頁的主體是不是「拍得到的東西」（地點、自然現象、天象、動植物、食物、建築、器物、材質）：" +
            "是的話就直接把那個主體寫成一張雜誌級攝影作品的畫面描述，不要繞成隱喻；" +
            "只有當主體是拍不到的抽象概念（成長、流程、風險、策略）時，才改用有美術指導的『材質靜物 / 光影 / 幾何構成』" +
            "（真實材質：紙、金屬、玻璃、石材、布料）來表達。" +
            "兩者都要：講究的光線、克制的色彩、大量留白、雜誌級質感；" +
            "禁止手繪、草圖、卡通、扁平向量圖示、clip art 這類廉價畫風，也不要資訊圖表式的符號拼貼；畫面中不要出現任何文字。";

        /// <summary>
        /// 這個主題不適合用生成圖：地圖、國旗、商標、真實地標／人物、圖表等「查得到正確答案」的東西，
        /// 模型畫出來一定是錯的，放進簡報等於給錯資訊。呼叫端遇到 true 就不配圖。
        /// </summary>
        public static bool IsFactualSubject(string? brief)
        {
            string text = (brief ?? "").ToLowerInvariant();
            if (text.Length == 0) return false;

            // 只擋「畫出來就會是錯的特定事物」。
            // 2026-09-25 修正：原本用 map / border / plot / diagram 這類單字去比對，
            // 結果「a thin border」「plated dish」這種正常的畫面描述也被當成地圖擋掉，
            // 觀光、美食這種最該配圖的主題反而整份沒有圖。所以改成整詞／片語比對。
            string[] english =
            {
                "a map", "the map", "map of", "world map", "cartographic", "atlas of",
                "flag of", "national flag", "coat of arms", "company logo", "brand logo", "trademark",
                "bar chart", "line chart", "pie chart", "flow chart", "flowchart", "infographic",
                "screenshot", "dashboard ui", "portrait of", "headshot of", "likeness of",
            };
            string[] chinese =
            {
                "地圖", "版圖", "疆域", "國旗", "商標", "企業標誌",
                "長條圖", "折線圖", "圓餅圖", "統計圖", "流程圖", "時間軸", "截圖", "肖像", "本人照片",
            };

            return english.Any(k => text.Contains(k, StringComparison.Ordinal))
                   || chinese.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>把一段「具體畫面 brief」組裝成最終圖片提示（brief + 統一風格後綴）。</summary>
        public static string Compose(string? brief)
        {
            string b = (brief ?? "").Trim();
            return b.Length == 0 ? StyleSuffix : b + " " + StyleSuffix;
        }

        /// <summary>
        /// 依簡報的藝術方向組裝圖片提示。isCover＝封面用（寬幅、左側留白給大標題壓字）。
        /// </summary>
        public static string Compose(string? brief, DeckArtDirection art, bool isCover = false)
        {
            string b = (brief ?? "").Trim();
            string style = StyleFor(art);

            string composition = isCover
                ? "Wide editorial image, one complete subject with breathing room on every side, calm left third. Title is placed outside the image, not over it. "
                : "Single complete subject, centered with generous margins on all sides. The image is shown uncropped beside text. ";

            return (b.Length == 0 ? "" : b + " ") + composition + style + " " + NoTextTail;
        }

        /// <summary>各藝術方向的圖像語言（色盤與質感都對齊簡報本身）。</summary>
        public static string StyleFor(DeckArtDirection art) => art?.Id switch
        {
            // 使用者偏好的藝術類美術風格（復古雜誌）：簡報照片保持乾淨寫實，雜誌感由版面負責（見 PoeticPrintStyle）。
            "poetic" => PoeticPrintStyle.DeckPhotoStyle,

            "gallery" =>
                "Exhibition-catalogue image: near-black ground (#0D0D0C), a single sculptural object lit by soft directional light, " +
                "warm brass highlights (#C9A227), deep shadows, museum-quality restraint, vast negative space, muted desaturated palette, fine grain.",

            "editorial" =>
                "Fashion-magazine still life: warm paper ground (#F2EEE6), real materials — folded paper, brushed metal, linen, ceramic — " +
                "arranged as a deliberate composition, one vermilion accent (#C2432C), raking studio light, crisp shadows, printed-matter texture, generous margins.",

            // 2026-09-25：原本只寫「自然的光影與質感研究」，太模糊——塔斯馬尼亞觀光那份生出了一座
            // 歐式石砌拱門廢墟，跟主題毫無關係。改成明確的大片幅風景攝影語言，並限制題材不要亂長建築。
            "nature" =>
                "Fine-art landscape photograph in the large-format nature photography tradition, depicting the subject above as a " +
                "type of place rather than a named landmark: layered mist, wet stone, moss, endemic vegetation, still water, " +
                "cold early light, deep tonal range, medium-format clarity and micro-detail, unhurried and empty of people. " +
                "No buildings, ruins, roads or structures unless the subject explicitly calls for them. " +
                "Sand and forest palette (#F4F2EB, #1B2A22, #2F6B4F).",

            "finance" =>
                "Restrained institutional image: white ground, deep navy forms (#0E2441), a single gold accent (#B08D3F), " +
                "architectural geometry or a precise material study, no invented charts or symbolic growth arrows, exact alignment, no clutter.",

            "tech" =>
                "Dark product-launch visual: near-black ground (#0A0E13), precise engineered geometry, electric blue glow (#4CC2FF), " +
                "subtle gradients, machined surfaces, technical clarity, high contrast.",

            "academic" =>
                "Calm explanatory still life: warm white ground (#FBFAF7), slate ink (#1F2937), teal-slate accent (#2F4858), " +
                "ordered physical objects arranged with precision, even soft light, no invented diagrams.",

            _ => StyleSuffix,
        };
    }
}
