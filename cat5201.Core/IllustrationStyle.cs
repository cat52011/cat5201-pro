namespace Cat5201
{
    /// <summary>
    /// 簡報 / 報告「配圖」的視覺風格與提示詞組裝。
    ///
    /// 2026-09-24：圖片改成跟著 <see cref="DeckArtDirection"/> 走——展覽風的簡報配展覽感的圖、
    /// 雜誌風配雜誌感的圖，色盤也對齊同一組色，圖才會像「設計的一部分」而不是貼上去的插圖。
    /// 沒有指定藝術方向時（報告章節配圖等）沿用原本的乾淨現代資訊示意風格。
    /// </summary>
    public static class IllustrationStyle
    {
        /// <summary>圖片模型實務上有效的硬性排除（避免亂碼文字、浮水印）。</summary>
        private const string NoTextTail =
            "No text, no words, no letters, no numbers, no watermark, no logo.";

        /// <summary>預設風格（無藝術方向時）：乾淨現代扁平資訊示意。</summary>
        public const string StyleSuffix =
            "Clean modern flat infographic illustration, minimalist vector style, simple clear geometric shapes and symbols, " +
            "bright airy light background, fresh friendly modern multi-color palette, generous whitespace, soft subtle shadows, " +
            "polished business presentation aesthetic, one clear positive easy-to-read concept. " + NoTextTail;

        /// <summary>給「圖片 brief 萃取」LLM 的風格指示（中文，讓 Claude 寫出符合此風格的具體畫面）。</summary>
        public const string BriefStyleGuidance =
            "風格固定為：乾淨、現代、扁平的『資訊示意圖 / 向量插畫』，用簡單清楚的幾何元素與象徵符號表達單一核心概念，" +
            "配色明亮清新專業、淺色背景、留白充足；畫面要正向、直覺、好理解，避免黑暗、威脅、詭異或過度隱喻的視覺；" +
            "不是寫實照片、不是繁複場景、畫面中不要出現任何文字。";

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
            "gallery" =>
                "Art-exhibition catalogue image: near-black ground (#0E0E0E), a single sculptural subject lit by soft directional light, " +
                "warm brass highlights (#C8A96A), deep shadows, museum-quality restraint, vast negative space, muted desaturated palette.",

            "editorial" =>
                "Magazine editorial illustration: warm paper ground (#F4F1EA), confident near-black shapes and line work, " +
                "one vermilion accent (#B3462F), bold simplified forms, collage-like composition, generous margins, printed-matter texture.",

            "nature" =>
                "Clearly illustrative landscape study, not a documentary photograph of a real location: sand and forest palette (#F6F4EF, #1C2B23, #2F6B4F), soft natural daylight, " +
                "wide horizon, atmospheric depth, unposed and calm, subtle film-like grain.",

            "finance" =>
                "Restrained institutional graphic: white ground, deep navy forms (#10233F), a single gold accent (#C8A23C), " +
                "depict only the specified concrete subject, no invented charts or symbolic growth arrows, precise alignment, no clutter.",

            "tech" =>
                "Dark product-launch visual: near-black ground (#0B0F14), precise geometric forms, electric blue glow (#4CC2FF), " +
                "subtle gradients, technical clarity, high contrast.",

            "academic" =>
                "Clean explanatory schematic: warm white ground (#FBFAF7), slate ink (#1F2937), teal-slate accent (#2F4858), " +
                "structured diagrammatic shapes, calm and precise.",

            _ => StyleSuffix,
        };
    }
}
