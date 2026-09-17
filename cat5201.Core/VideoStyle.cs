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
    ///   - 影像質感：清晰乾淨、細節與材質真實、深邃黑位、寬廣動態範圍；不要顆粒/刮痕/片門晃動/霧化柔焦；
    ///   - 光線：自然、有來源的實光（日光、陰天、火光、月光），有雕塑感的明暗；
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
@"Visual aesthetic: modern large-format epic cinema. Whatever the subject — grand or ordinary — it is filmed with the scale, clarity and gravity of a film shot on 70mm IMAX cameras. Grounded, tactile and real rather than stylized: it should feel photographed in the physical world, not generated.

Image and format (essential — this is what separates the look from generic AI): pristine large-format clarity with immense resolving detail and natural texture — skin, stone, water, cloth, wood and weathered metal all read as real materials. A clean, crisp image with deep, rich, inky blacks and full dynamic range; highlights roll off gently and naturally. Shallow, organic depth of field in close-ups; deep focus in wide shots.

Light: naturalistic and motivated — real sunlight, overcast sky, firelight, torchlight, moonlight. Hard sun carving strong shadows; storm light over dark water; a face lit by a single practical source against darkness. Bold, sculpted contrast, but never glossy or artificially polished.

Color: restrained, earthy, natural palette — slate and deep sea blue, sun-bleached stone, sand and ochre, bronze and weathered iron, warm firelight, the true black of night. Colour comes from the world itself, not from a heavy grade.

Camera and composition: a deliberate contrast of scale. Monumental wide vistas where the subject is small against sea, sky, cliffs, desert or city, alternating with intimate close-ups of faces, eyes and hands that carry the emotion. The camera moves with purpose — slow, weighty push-ins and sweeping tracking or aerial moves for scale; restrained handheld for close, visceral moments. Strong classical framing, one clear subject per shot.

Emotion: gravity, awe, solitude, endurance, longing — a small human presence facing something vast. Serious and immersive, never flashy.

NOT: old or damaged film, visible film grain, scratches, dust, gate weave, sepia or yellowed tones, dreamy haze, soft-focus diffusion, washed-out low contrast; CGI-plastic surfaces, video game or fantasy-artwork look, generic AI surrealism (floating islands, golden clouds, glowing cloaked figures), glossy commercial or music-video style, teal-and-orange grading, oversaturation, frantic cuts.";

        /// <summary>個人化頁面顯示用的一句話說明。</summary>
        public const string DefaultStyleSummary =
            "原廠預設：當代大畫幅史詩電影攝影——IMAX 級清晰乾淨畫面、深邃黑位、自然實光與雕塑感明暗、樸實大地色調、壯闊遠景與貼近特寫的尺度對比、沉穩有份量的運鏡。任何主題都以此語言呈現。";

        /// <summary>
        /// 給影片模型（Veo）用的「精簡視覺標籤」。
        ///
        /// 為什麼要跟 DefaultCinematicPrompt 分開：完整版那段是寫給「導演（Claude）」看懂美學用的，
        /// 充滿抽象藝術評論與 NOT 清單。但影片模型 prompt 服從度很短，只認得具體視覺名詞——
        /// 把整段藝評＋否定清單原封塞給 Veo 只會稀釋訊號、甚至讓 NOT 裡的詞被當正面 token 渲染出來
        /// （例如寫 no film grain 反而冒出顆粒）。
        /// 因此送進 Veo 的固定是這段「短、具體、純正面描述」的標籤（無抽象情緒、無 NOT 清單）；
        /// 也刻意不寫「一定有人物」，避免主題是物件/風景時被硬塞人進去。
        /// </summary>
        public const string DefaultVeoRenderTags =
            "Epic large-format cinematography shot on 70mm IMAX cameras. Pristine, crisp, clean image with rich natural detail and real material texture, deep inky blacks and full dynamic range. Naturalistic motivated lighting from real sunlight, overcast sky or practical sources, bold sculpted contrast. Restrained earthy natural palette of weathered stone, sand, ochre, bronze and deep slate blue. Monumental scale in wide shots, intimate detail in close-ups with shallow depth of field. Slow, weighty, purposeful camera movement. Grounded, tactile, real-world realism.";

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
