namespace Cat5201
{
    /// <summary>
    /// 使用者偏好的藝術類美術風格：「復古雜誌」（2026-11-06）。
    /// 藝術類的簡報與圖片都偏向這種美感；寫實類不受影響。哪些算藝術類，由第一層語意判斷決定
    /// （OutputIntent.ArtDirection == "poetic"），不用關鍵字。
    ///
    /// 使用者說得很清楚：要的是「美術風格」，不是一份照做的規格，也不要寫死。
    /// 所以這裡只描述美感與原則（質感、字體氣質、配色來源、節奏），不規定構圖、張數、文字內容或比例——
    /// 怎麼排由模型依內容決定。之前一版把海報規格逐條寫進提示（固定兩段英文詩、一個大寫字、ORIGINAL 縮圖），已經拿掉。
    /// </summary>
    public static class PoeticPrintStyle
    {
        /// <summary>這個風格在 DeckArtDirection 裡的 ID。</summary>
        public const string Id = "poetic";

        /// <summary>
        /// 單張藝術類圖片的美術方向（產圖、改圖）。只描述美感，構圖自由。
        /// 2026-11-06：原本寫「明顯的網點」和「暖白紙色」，清水寺那張整張蓋滿網點、還多一圈白紙邊，細節都糊了。
        /// 改成質感最多一點點、不能蓋掉細節、不加邊框。
        /// </summary>
        public const string ImageStyle =
            "Art direction (an aesthetic to lean toward, not a fixed layout): a vintage colour-magazine feel. " +
            "Real photographic material, sharp and true to life, with warm, emotionally tuned light and generous breathing room; " +
            "at most a very faint printed-paper character that never obscures detail — no heavy grain, no visible dot screen, no borders or frames; " +
            "colours drawn from the subject itself (petals, leaves, walls, water, fabric), never a default blue; calm, poetic restraint. " +
            "Choose whatever composition suits the subject.";

        /// <summary>
        /// 簡報裡的照片：乾淨、寫實、細節清楚，不加任何質感。雜誌感交給版面（紙色、字體、節奏），
        /// 不靠在照片上加網點——一頁四張照片時，只要有一張被加上網點或紙邊就會很突兀（2026-11-06 京都簡報）。
        /// </summary>
        public const string DeckPhotoStyle =
            "Clean, true-to-life photograph with crisp natural detail; warm, emotionally tuned light; colours drawn from the subject itself, " +
            "never a default blue; calm, uncluttered composition. No visible grain, no halftone dots, no film noise, " +
            "no paper border, frame or vignette — the magazine feel comes from the page layout, not from texture on the photo.";

        /// <summary>
        /// 交給簡報排版模型的版面語言（DeckArtDirection.DirectionForModel）。同樣只講美感與原則，不規定版型。
        /// </summary>
        public const string DeckDirection =
            "Vintage magazine (the user's preferred look for artistic subjects) — an aesthetic to interpret freely, not a template. " +
            "Warm white paper ground (#F3EEE4) and deep warm ink (#2A2420); confident high-contrast serif display type " +
            "(Georgia-like; Chinese headings in a Mincho/明體 serif) paired with small, quiet typewriter-style captions (e.g. Courier New); " +
            "photos stay clean and true to life (no added grain, dots or borders) and are given generous paper-white space — " +
            "the magazine feel comes from typography, paper colour and pacing. " +
            "Take accent colours from the photos on each slide rather than a fixed palette, and never default to blue; keep readable contrast. " +
            "Poetic, restrained, editorial pacing — vary layouts the way a good magazine does; avoid boxes, cards, drop shadows and header/footer bars.";
    }
}
