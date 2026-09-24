namespace Cat5201
{
    /// <summary>
    /// 影片導演的「原廠預設風格」。這是注入給 Claude 導演 + Veo 的視覺風格基底。
    ///
    /// 個人化頁面會完整顯示這段預設 prompt，讓使用者照同樣格式寫自己的風格；
    /// 使用者未自訂時一律用這個預設。
    ///
    /// 設計重點（2026-09-17 改版：拿掉舊膠片感，改走當代大畫幅史詩電影攝影，參考 Nolan《The Odyssey》2026 的攝影手法）：
    ///   - 核心定義：任何主題都以「IMAX 大畫幅史詩電影」的語言呈現——視覺語言固定，內容跟使用者需求走；
    ///   - 影像質感：清晰、細節與材質真實、深邃黑位、寬廣動態範圍，帶「淡淡」細顆粒（使用者 09-17 重測後要求加回）；
    ///     不要粗顆粒/刮痕/片門晃動/泛黃/遮住細節的濃霧柔焦；
    ///   - 光線：自然、有來源的實光（日光、陰天、火光、月光），有雕塑感的明暗；遠景一層薄霧增加空氣感，主體保持清楚；
    ///   - 色調：樸實大地色（石、沙、赭、青銅、深海藍），色彩來自世界本身而非調色；
    ///   - 構圖：壯闊遠景（渺小的主體對巨大的世界）與貼近特寫的尺度對比；運鏡沉穩有份量；
    ///   - 刻意避免：CG 塑膠感、遊戲/奇幻插畫感、AI 泛類超現實、商業廣告光澤、青橙調色。
    ///
    /// 片名與人名只寫在這段註解：送給模型的文字一律只寫具體視覺描述——
    /// 寫片名會讓每支影片都長出希臘船與青銅盔甲（內容污染），也避免模仿特定作品。
    /// </summary>
    public static class VideoStyle
    {
        /// <summary>原廠預設風格（英文，影片/影像模型對英文表現最好）。</summary>
        public const string DefaultCinematicPrompt =
@"Visual aesthetic: modern epic cinema made for the largest screens. Whatever the subject — grand or ordinary — it is filmed with monumental scale, clarity and gravity. Grounded, tactile and real rather than stylized: it should feel photographed in the physical world, not generated.

Image (essential — this is what separates the look from generic AI): immense resolving detail and natural texture — skin, stone, water, cloth, wood and weathered metal all read as real materials. A clear, crisp image carrying a subtle, fine organic grain — present, but never distracting. Deep, rich, inky blacks and full dynamic range; highlights roll off gently and naturally. Shallow, organic depth of field in close-ups; deep focus in wide shots. The picture always fills the entire frame.

Light and atmosphere: naturalistic and motivated — real sunlight, overcast sky, firelight, torchlight, moonlight. Hard sun carving strong shadows; storm light over dark water; a face lit by a single practical source against darkness. A soft veil of atmospheric haze and light mist gives depth to distances and catches the light, while the subject stays sharp. Bold, sculpted contrast, but never glossy or artificially polished.

Color: restrained, earthy, natural palette — slate and deep sea blue, sun-bleached stone, sand and ochre, bronze and weathered iron, warm firelight, the true black of night. Colour comes from the world itself, not from a heavy grade.

Camera and composition: a deliberate contrast of scale. Monumental wide vistas where the subject is small against sea, sky, cliffs, desert or city, alternating with intimate close-ups of faces, eyes and hands that carry the emotion. The camera moves with purpose — slow, weighty push-ins and sweeping tracking or aerial moves for scale; restrained handheld for close, visceral moments. Strong classical framing, one clear subject per shot.

Emotion: gravity, awe, solitude, endurance, longing — a small human presence facing something vast. Serious and immersive, never flashy.

NOT: old or damaged film, heavy or coarse grain, scratches, dust, gate weave, sepia or yellowed tones, dense fog or dreamy soft-focus diffusion that hides detail, washed-out low contrast; black bars, borders or on-screen text; CGI-plastic surfaces, video game or fantasy-artwork look, generic AI surrealism (floating islands, golden clouds, glowing cloaked figures), glossy commercial or music-video style, teal-and-orange grading, oversaturation, frantic cuts.";

        /// <summary>個人化頁面顯示用的一句話說明。</summary>
        public const string DefaultStyleSummary =
            "原廠預設：當代史詩電影攝影——清晰畫面帶淡淡細顆粒、遠處一層薄霧、深邃黑位、自然實光與雕塑感明暗、樸實大地色調、壯闊遠景與貼近特寫的尺度對比、沉穩有份量的運鏡。任何主題都以此語言呈現。";

        /// <summary>
        /// 給影片模型（Veo）用的「精簡視覺標籤」。
        ///
        /// 為什麼要跟 DefaultCinematicPrompt 分開：完整版那段是寫給「導演（Claude）」看懂美學用的，
        /// 充滿抽象藝術評論與 NOT 清單。但影片模型 prompt 服從度很短，只認得具體視覺名詞——
        /// 把整段藝評＋否定清單原封塞給 Veo 只會稀釋訊號、甚至讓 NOT 裡的詞被當正面 token 渲染出來
        /// （例如寫 no film grain 反而冒出顆粒）。
        /// 因此送進 Veo 的固定是這段「短、具體、純正面描述」的標籤（無抽象情緒、無 NOT 清單）；
        /// 也刻意不寫「一定有人物」，避免主題是物件/風景時被硬塞人進去。
        ///
        /// 2026-09-17 實測教訓：不寫片幅/攝影機規格（IMAX、70mm、large-format、widescreen）。
        /// 影片模型把這些當成「要出現在畫面上的字」，搭配黑邊就長出「70X」「IMAM」這種假浮水印。
        /// </summary>
        public const string DefaultVeoRenderTags =
            "Epic cinematic photography with monumental scale. Clear, crisp image with rich natural detail and real material texture, a subtle fine organic grain, deep inky blacks and full dynamic range. Naturalistic motivated lighting from real sunlight, overcast sky or practical sources, bold sculpted contrast, a soft veil of atmospheric haze and light mist adding depth to the distance. Restrained earthy natural palette of weathered stone, sand, ochre, bronze and deep slate blue. Vast sweeping wide shots and intimate close-ups with shallow depth of field. Slow, weighty, purposeful camera movement. Grounded, tactile, real-world realism, the picture filling the whole frame edge to edge.";

        // 影片模型會把「片幅/畫幅規格」畫成畫面上的字或補黑邊，以及把否定清單裡的名詞當成要畫的東西
        // （Veo 3.1 的 Gemini API 沒有 negativePrompt 欄位，只能靠正面描述）。導演偶爾照舊習慣寫進去，這裡在送出前剝掉。
        private static readonly System.Text.RegularExpressions.Regex FormatJargon = new(
            @"\b(?:shot\s+on\s+)?(?:(?:65|70)\s?mm\s+)?IMAX(?:\s+(?:cameras?|film|format))?\b|\b(?:65|70)\s?mm(?:\s+film)?\b|\blarge[-\s]format\b|\bletterbox(?:ed|ing)?\b|\bwidescreen\b|\bcinemascope\b|\banamorphic\b|\b2\.39\s?:\s?1\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex NegatedOverlay = new(
            @"\bno\s+(?:on-screen\s+)?(?:text|captions?|subtitles?|titles?|watermarks?|logos?|letters|words)\b\s*[,.;]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// 送進影片/英雄圖模型前的防呆：剝掉片幅規格字與「no text / no logo」這類否定清單，並收拾殘留標點空白。
        /// 只用在導演（AI）產出的鏡頭內容；使用者自訂風格原樣送出（個人化優先）。
        /// </summary>
        public static string SanitizeForVideoModel(string? text)
        {
            string s = text ?? "";
            s = FormatJargon.Replace(s, "");
            s = NegatedOverlay.Replace(s, "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]{2,}", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+([,.;])", "$1");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"([,;])\s*(?=[,.;])", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(^|\n)\s*[,.;]\s*", "$1");
            return s.Trim();
        }

        /// <summary>
        /// 解析「實際生效」的風格：使用者自訂（去空白後非空且不等於預設）則用自訂，否則用原廠預設。
        /// </summary>
        public static string Resolve(string? userOverride)
        {
            string s = (userOverride ?? "").Trim();
            return s.Length == 0 ? DefaultCinematicPrompt : s;
        }

        /// <summary>
        /// 取「實際送進影片模型（Veo）」的風格標籤。
        /// 生效風格＝原廠預設時 → 用精簡標籤 DefaultVeoRenderTags（不把整段藝評丟給 Veo）；
        /// 使用者自訂時 → 直接用使用者那段（他自己決定要寫什麼給模型）。
        /// </summary>
        public static string RenderTagsFor(string? effectiveStyle)
        {
            string s = (effectiveStyle ?? "").Trim();
            if (s.Length == 0 ||
                string.Equals(s, DefaultCinematicPrompt.Trim(), System.StringComparison.Ordinal))
            {
                return DefaultVeoRenderTags;
            }
            return s;
        }
    }
}
