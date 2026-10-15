using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Cat5201;

// Coordinates are points on a 960 x 540 canvas. All native exports (pptx / pdf / preview) consume these exact elements.
internal sealed record DeckElement(string Kind, float X, float Y, float W, float H,
    string Color = "", string Text = "", float Size = 0, bool Bold = false, byte[]? Image = null,
    string Font = "", string EaFont = "", string Align = "l", float Track = 0, int Alpha = 100);

internal sealed record DeckPage(int SourceOrder, IReadOnlyList<DeckElement> Elements);

/// <summary>
/// 簡報的版面引擎：一份幾何場景同時餵給 pptx / PDF / 預覽，所以三者長得一模一樣。
///
/// 2026-09-24 美術重做（使用者要的兩種風格）：
///   Editorial 家族（藝術／時尚雜誌）：襯線大標、字距拉開的小標、髮絲線、超大留白、頁碼當設計元素、編號用大字。
///   Corporate 家族（商業科技／銀行正式）：嚴謹格線、頁首頁尾分隔線、半粗黑標題、列與列之間細線、數字對齊、克制的強調色。
/// 版面參數集中在 Layout，兩家族只差參數與少數分支，避免兩套程式各自腐爛。
/// </summary>
internal static class DeckScene
{
    internal const float Width = 960, Height = 540;
    internal const string Font = "Microsoft JhengHei";

    /// <summary>有配圖時，圖從這個 x 一路出血到頁面右緣與上下緣；文字守在左邊的欄裡。</summary>
    private const float ImageColumnX = 552;

    private sealed class Layout
    {
        public float Left, Right, Top, Bottom;
        public float KickerSize, TitleSize, BodySize, StatSize, FolioSize;
        public float LineHeight;
        public float RuleWeight;
        public bool EditorialFolio;      // 雜誌：左下大頁碼；商業：右上頁碼 + 頁尾線
        public string DisplayLatin = "", DisplayEa = "", BodyLatin = "", BodyEa = "";
        public float KickerTrack;
    }

    private static Layout LayoutFor(DeckArtDirection art) => art.Family == "editorial"
        ? new Layout
        {
            Left = 64, Right = 64, Top = 52, Bottom = 50,
            KickerSize = 10.5f, TitleSize = 37, BodySize = 17.5f, StatSize = 54, FolioSize = 26,
            LineHeight = 1.62f, RuleWeight = 0.7f, EditorialFolio = true, KickerTrack = 3.4f,
            DisplayLatin = art.DisplayLatinFont, DisplayEa = art.DisplayEaFont,
            BodyLatin = art.BodyLatinFont, BodyEa = art.BodyEaFont,
        }
        : new Layout
        {
            Left = 60, Right = 60, Top = 44, Bottom = 46,
            KickerSize = 10f, TitleSize = 30, BodySize = 17f, StatSize = 44, FolioSize = 10,
            LineHeight = 1.5f, RuleWeight = 0.6f, EditorialFolio = false, KickerTrack = 2.2f,
            DisplayLatin = art.DisplayLatinFont, DisplayEa = art.DisplayEaFont,
            BodyLatin = art.BodyLatinFont, BodyEa = art.BodyEaFont,
        };

    /// <summary>實際會出現在成品上的配圖張數（數據頁／來源頁的圖會被版面捨棄，不能只數手上有幾個 byte[]）。</summary>
    internal static int CountRenderedImages(PresentationOutlinePayload outline, byte[]? cover)
        => Build(outline, cover).Sum(p => p.Elements.Count(e => e.Kind == "image"));

    /// <summary>
    /// 這一次允許「擠一點」：文字縮到最小可讀字級仍塞不下時，照樣排出來，不要丟例外。
    ///
    /// 為什麼需要這個開關：AddText 原本的設計是「縮不下去就丟例外，讓分頁去處理」，
    /// 但分頁那層沒有接住它——例外一路往上拋到 AgentRuntime，被 catch 成一行警告，
    /// 於是整份簡報憑空消失。2026-10-15 使用者實測三份簡報，兩份就是這樣沒有產出檔案，
    /// 畫面上只說「主答案不受影響」，完全看不出簡報去哪了。
    ///
    /// 一頁字稍微擠，跟整份簡報做不出來，不是同一個等級的問題。
    /// </summary>
    [ThreadStatic] private static bool s_relaxedFit;

    internal static IReadOnlyList<DeckPage> Build(PresentationOutlinePayload outline, byte[]? cover)
    {
        try
        {
            return BuildCore(outline, cover);
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Warn("DeckScene",
                "有一頁的文字縮到最小字級仍塞不下，改用最小字級重排（寧可擠一點，也不要整份簡報做不出來）", ex);

            s_relaxedFit = true;
            try { return BuildCore(outline, cover); }
            finally { s_relaxedFit = false; }
        }
    }

    private static IReadOnlyList<DeckPage> BuildCore(PresentationOutlinePayload outline, byte[]? cover)
    {
        var art = DeckArtDirection.ForOutline(outline);
        var L = LayoutFor(art);
        float contentW = Width - L.Left - L.Right;

        var pages = new List<DeckPage>();
        var slides = outline.Slides.OrderBy(s => s.Order).ToList();
        if (slides.Count == 0)
            slides.Add(new() { Order = 1, Kind = "cover", Heading = outline.Title });

        // 頁尾的「X / Y」要用實際會印出來的頁數：內容太長會拆成續頁，
        // 用大綱張數會印出「07 / 05」這種對不上的頁碼。先跑一次切塊算總數。
        int totalPages = CountPages(slides, cover);

        foreach (var slide in slides)
        {
            var lines = slide.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)).SelectMany(SplitLong).ToArray();
            bool isCover = string.Equals(slide.Kind, "cover", StringComparison.OrdinalIgnoreCase);
            bool sources = string.Equals(slide.Kind, "sources", StringComparison.OrdinalIgnoreCase);

            var image = isCover ? cover : slide.ImageBytes;
            if (image != null && PptxBuilder.TryReadPngSize(image) == null) image = null;

            // 有兩個以上帶單位的數字＝這頁的主角是數據，不用隱喻插圖蓋掉。
            bool metrics = lines.Length > 0 && lines.All(b => b.Length <= 100)
                           && lines.Count(b => !string.IsNullOrEmpty(PptxBuilder.FindKeyFigure(b))) >= 2;
            if (metrics || sources) image = null;

            var chunks = SplitIntoChunks(slide, cover);

            for (int chunk = 0; chunk < chunks.Count; chunk++)
            {
                var items = chunks[chunk];
                var e = new List<DeckElement>();

                void Rect(float x, float y, float w, float h, string color, int alpha = 100)
                    => e.Add(new("rect", x, y, w, h, color, Alpha: alpha));

                void Text(float x, float y, float w, float h, string text, float size, string color,
                    bool bold = false, bool display = false, string align = "l", float track = 0)
                    => AddText(e, x, y, w, h, text, size, color, bold,
                        display ? L.DisplayLatin : L.BodyLatin, display ? L.DisplayEa : L.BodyEa, align, track);

                void Picture(float x, float y, float w, float h, byte[] bytes)
                {
                    var dim = PptxBuilder.TryReadPngSize(bytes);
                    if (dim == null) return;

                    // 先裁成圖框比例＝滿版，框裡不會留空白帶；裁不動（圖太小）才退回等比置中。
                    var filled = DeckImage.CropToRatio(bytes, w / h);
                    if (filled != null) { e.Add(new("image", x, y, w, h, Image: filled)); return; }

                    float scale = Math.Min(w / dim.Value.Width, h / dim.Value.Height);
                    float iw = dim.Value.Width * scale, ih = dim.Value.Height * scale;
                    e.Add(new("image", x + (w - iw) / 2, y + (h - ih) / 2, iw, ih, Image: bytes));
                }

                bool coverPage = isCover && chunk == 0;

                // 一張投影片的內容放不下時會拆成續頁；圖只放在第一頁。
                // 續頁沿用同一張圖的話，同一張照片會在相鄰兩頁各出現一次，看起來就是做壞了。
                byte[]? pageImage = chunk == 0 ? image : null;

                bool statement = !coverPage && !sources && pageImage == null && !metrics
                                 && items.Length == 1 && items[0].Length <= 70;
                bool inverse = coverPage || statement;

                string bg = inverse ? art.InverseBackground : art.Background;
                string ink = inverse ? art.InverseInk : art.Ink;
                string muted = inverse ? art.InverseMuted : art.Muted;

                Rect(0, 0, Width, Height, bg);

                string title = slide.Heading + (chunk > 0 ? "（續）" : "");
                int pageNumber = pages.Count + 1;

                // 有滿版配圖時，頁首／頁尾／標題都縮到左邊的文字欄，圖才能真的出血到右緣與上下緣。
                bool bleedImage = !statement && !metrics && !sources && pageImage != null;
                float bandW = bleedImage ? ImageColumnX - L.Left - 44 : contentW;

                if (coverPage)
                {
                    RenderCover(art, L, Rect, Text, Picture, outline, title, pageImage, ink, muted,
                        cover != null || outline.Slides.Any(s => s.ImageBytes is { Length: > 0 }));
                    pages.Add(new(slide.Order, e));
                    continue;
                }

                // ── 頁首：小標 + 髮絲線 + 大標題 ──
                float y = L.Top;
                string kicker = string.IsNullOrWhiteSpace(outline.Title) ? "" : outline.Title.Trim();

                if (!statement && !string.IsNullOrWhiteSpace(kicker))
                {
                    Text(L.Left, y, bandW - 120, L.KickerSize * 1.4f,
                        OneLine(kicker, bandW - 120, L.KickerSize, L.KickerTrack), L.KickerSize,
                        muted, track: L.KickerTrack);
                    if (!L.EditorialFolio)
                        Text(L.Left + bandW - 110, y, 110, L.KickerSize * 1.4f,
                            $"{pageNumber:00}", L.KickerSize, muted, align: "r", track: 1.6f);
                    y += L.KickerSize * 1.4f + 10;
                    Rect(L.Left, y, bandW, L.RuleWeight, art.Muted, inverse ? 40 : 30);
                    y += 22;
                }
                else if (!statement)
                {
                    Rect(L.Left, y + 6, 44, 2.5f, art.Accent);
                    y += 34;
                }

                if (!statement)
                {
                    float titleH = L.TitleSize * L.LineHeight * 2;
                    Text(L.Left, y, bandW - (bleedImage ? 0 : L.EditorialFolio ? 150 : 90), titleH, title, L.TitleSize, ink,
                        bold: art.Family != "editorial", display: true);
                    y += titleH + (L.EditorialFolio ? 14 : 10);
                    if (art.Family != "editorial")
                    {
                        Rect(L.Left, y, 52, 2.5f, art.Accent);
                        y += 22;
                    }
                }

                float bodyTop = Math.Max(y, 186);
                float bodyBottom = Height - L.Bottom - (L.EditorialFolio ? 30 : 34);
                float bodyH = bodyBottom - bodyTop;

                if (statement)
                {
                    RenderStatement(art, L, Rect, Text, title, items.FirstOrDefault() ?? title, ink, muted, contentW);
                }
                else if (metrics)
                {
                    RenderStats(art, L, Rect, Text, items, ink, muted, contentW, bodyTop, bodyH);
                }
                else if (pageImage != null)
                {
                    RenderImageAndText(art, L, Rect, Text, Picture, items, pageImage, ink, bodyTop, bodyH);
                }
                else if (items.Length == 0)
                {
                    Text(L.Left, bodyTop + 20, contentW - 80, bodyH - 40, title, L.TitleSize + 6, ink, display: true);
                }
                else
                {
                    RenderPoints(art, L, Rect, Text, items, sources, ink, muted, contentW, bodyTop, bodyH);
                }

                // ── 頁尾：雜誌＝左下大頁碼；商業＝細線 + 主題 + 頁碼 ──
                if (!statement)
                {
                    if (L.EditorialFolio)
                    {
                        Text(L.Left, Height - L.Bottom - L.FolioSize * 1.2f, 90, L.FolioSize * 1.4f,
                            $"{pageNumber:00}", L.FolioSize, art.Accent, display: true);
                        if (!bleedImage)
                            Text(Width - L.Right - 300, Height - L.Bottom - 14, 300, 16,
                                OneLine(kicker, 300, 9.5f, 1.4f), 9.5f, muted, align: "r", track: 1.4f);
                    }
                    else
                    {
                        float fy = Height - L.Bottom - 6;
                        Rect(L.Left, fy, bandW, L.RuleWeight, art.Muted, 26);
                        if (!bleedImage)
                            Text(L.Left, fy + 9, bandW - 120, 14,
                                OneLine(kicker, bandW - 120, 9.5f, 1.2f), 9.5f, muted, track: 1.2f);
                        Text(L.Left + bandW - 110, fy + 9, 110, 14, $"{pageNumber:00} / {totalPages:00}",
                            9.5f, muted, align: "r", track: 1.2f);
                    }
                }

                pages.Add(new(slide.Order, e));
            }
        }

        return pages;
    }

    // ===== 各版面 =====

    private static void RenderCover(
        DeckArtDirection art, Layout L,
        Action<float, float, float, float, string, int> Rect,
        Action<float, float, float, float, string, float, string, bool, bool, string, float> Text,
        Action<float, float, float, float, byte[]> Picture,
        PresentationOutlinePayload outline, string title, byte[]? image,
        string ink, string muted, bool hasAnyImage)
    {
        float contentW = Width - L.Left - L.Right;
        float textW = image != null ? ImageColumnX - L.Left - 44 : contentW - 120;

        // 上緣：短強調線 + 主題小標（字距拉開）
        Rect(L.Left, L.Top, 44, 3, art.Accent, 100);
        if (!string.IsNullOrWhiteSpace(outline.Topic))
            Text(L.Left, L.Top + 18, textW, L.KickerSize * 1.4f,
                OneLine(outline.Topic, textW, L.KickerSize, L.KickerTrack), L.KickerSize, muted, false, false, "l", L.KickerTrack);

        // 標題：冒號後換行，讓「主標／副標」在視覺上分層（雜誌大標的常見處理）。
        int colon = title.IndexOf('：');
        string coverTitle = colon > 0 && colon < 18 ? title.Insert(colon + 1, "\n") : title;
        float titleSize = art.Family == "editorial" ? (image != null ? 38 : 58) : (image != null ? 32 : 46);

        Text(L.Left, image != null ? 146 : 196, textW, 230, coverTitle, titleSize, ink,
            art.Family != "editorial", true, "l", 0);

        // 下緣：整段髮絲線 + 副標，收住版面
        float ruleY = Height - L.Bottom - 46;
        float subtitleW = image != null ? textW : contentW - 160;
        Rect(L.Left, ruleY, image != null ? textW : contentW, L.RuleWeight, art.InverseInk, 34);
        if (!string.IsNullOrWhiteSpace(outline.Topic))
            Text(L.Left, ruleY + 14, subtitleW, 22,
                OneLine(outline.Topic, subtitleW, 14f), 14f, muted, false, false, "l", 0);

        if (image != null)
            // 圖滿版壓到頁面右緣與上下緣（雜誌封面的作法），不留浮在半空中的小圖框。
            Picture(ImageColumnX, 0, Width - ImageColumnX, Height, image);

        // 生成圖是示意不是照片，這件事要講；整份講一次就夠，不必每頁都掛一行小字。
        if (hasAnyImage)
            Text(L.Left, Height - 30, textW, 14, "插圖為 AI 生成的示意畫面", 9f, muted, false, false, "l", 0.8f);
    }

    private static void RenderStatement(
        DeckArtDirection art, Layout L,
        Action<float, float, float, float, string, int> Rect,
        Action<float, float, float, float, string, float, string, bool, bool, string, float> Text,
        string title, string body, string ink, string muted, float contentW)
    {
        Rect(L.Left, 150, 56, 3, art.Accent, 100);
        if (!string.IsNullOrWhiteSpace(title))
            Text(L.Left, 170, contentW - 120, L.KickerSize * 1.4f,
                OneLine(title, contentW - 120, L.KickerSize, L.KickerTrack), L.KickerSize, muted, false, false, "l", L.KickerTrack);

        float size = art.Family == "editorial" ? 40 : 34;
        Text(L.Left, 212, contentW - 100, 200, body, size, ink, art.Family != "editorial", true, "l", 0);
    }

    private static void RenderStats(
        DeckArtDirection art, Layout L,
        Action<float, float, float, float, string, int> Rect,
        Action<float, float, float, float, string, float, string, bool, bool, string, float> Text,
        string[] items, string ink, string muted, float contentW, float top, float bodyH)
    {
        int columns = Math.Max(1, Math.Min(items.Length, 4));
        float gap = 30;
        float colW = (contentW - gap * (columns - 1)) / columns;

        // 欄數越多，數字越小——四個 54pt 的大數字擠在一起就不是設計，是塞。
        float valueSize = L.StatSize * (columns >= 4 ? 0.60f : columns == 3 ? 0.76f : 1f);
        float valueH = valueSize * 1.34f, captionSize = L.BodySize - 1.5f;

        // 分隔線只包住內容，拖到底會像一張空表格。
        float blockH = valueH + 26 + items.Max(item => TextHeight(PptxBuilder.SplitStat(item).Caption, colW, captionSize));
        blockH = Math.Min(blockH, bodyH);
        top += (bodyH - blockH) * 0.42f;

        for (int j = 0; j < items.Length; j++)
        {
            float x = L.Left + j * (colW + gap);
            var (value, caption) = PptxBuilder.SplitStat(items[j]);

            // 欄與欄之間的髮絲線：銀行／年報式的秩序感。
            if (j > 0)
                Rect(x - gap / 2, top - 6, L.RuleWeight, blockH + 12, art.Muted, 28);

            if (value.Length == 0)
            {
                Text(x, top + 10, colW, blockH - 10, caption, L.BodySize + 2, ink, false, false, "l", 0);
                continue;
            }

            Text(x, top, colW, valueH, value, valueSize, art.Family == "editorial" ? ink : art.Accent,
                art.Family != "editorial", true, "l", 0);
            Rect(x, top + valueH + 8, Math.Min(colW, 40), 2, art.Accent, 100);
            Text(x, top + valueH + 26, colW, blockH - valueH - 26, caption, captionSize,
                muted, false, false, "l", 0);
        }
    }

    private static void RenderImageAndText(
        DeckArtDirection art, Layout L,
        Action<float, float, float, float, string, int> Rect,
        Action<float, float, float, float, string, float, string, bool, bool, string, float> Text,
        Action<float, float, float, float, byte[]> Picture,
        string[] items, byte[] image, string ink, float top, float bodyH)
    {
        // 圖直接出血到頁面右緣與上下緣（雜誌的作法）；文字守在左欄，兩邊互不干擾。
        // 圖已先裁成這個框的比例，所以框裡不會有空白帶。
        Picture(ImageColumnX, 0, Width - ImageColumnX, Height, image);

        float textW = ImageColumnX - L.Left - 44;

        float size = L.BodySize + 1.5f;
        // 圖是滿版的，文字欄太小就會被壓過去；內容少時放大字級撐起這一欄。
        while (size < L.BodySize * 1.55f
               && (items.Max(item => TextHeight(item, textW, size + 1)) + 30) * items.Length <= bodyH * 0.8f)
            size += 1;

        float natural = items.Max(item => TextHeight(item, textW, size)) + 30;
        float row = bodyH / Math.Max(1, items.Length);
        float y0 = top;
        if (natural * items.Length <= bodyH) { y0 += (bodyH - natural * items.Length) / 2; row = natural; }

        for (int j = 0; j < items.Length; j++)
        {
            float y = y0 + j * row;
            if (j > 0)
                Rect(L.Left, y - 12, textW, L.RuleWeight, art.Muted, 26);
            Text(L.Left, y, textW, row - 20, items[j], size, ink, false, false, "l", 0);
        }
    }

    private static void RenderPoints(
        DeckArtDirection art, Layout L,
        Action<float, float, float, float, string, int> Rect,
        Action<float, float, float, float, string, float, string, bool, bool, string, float> Text,
        string[] items, bool sources, string ink, string muted, float contentW, float top, float bodyH)
    {
        bool columns = !sources && items.Length is >= 2 and <= 4 && items.All(b => b.Length <= 70);
        float size = sources ? L.BodySize - 3 : L.BodySize;
        bool editorial = art.Family == "editorial";

        if (columns)
        {
            int perRow = items.Length <= 2 ? items.Length : 2;
            int rows = (int)Math.Ceiling(items.Length / (float)perRow);
            float gap = 34;
            float colW = (contentW - gap * (perRow - 1)) / perRow;

            // 內容不足以填滿版心時：先放大字級，再把整塊往中間放——
            // 否則兩句話配一整頁空白，看起來像沒做完，而不是留白。
            float head = editorial ? 40 : 38, pad = 30;
            float Natural(float s) => head + items.Max(item => TextHeight(item, colW, s)) + pad;
            while (size < L.BodySize * 1.45f && Natural(size + 1) * rows <= bodyH * 0.78f) size += 1;

            float natural = Natural(size);
            float rowH = bodyH / rows;
            if (natural * rows <= bodyH) { top += (bodyH - natural * rows) / 2; rowH = natural; }

            // 雜誌的欄間細線：兩欄之間有一道分欄線，版面才像排過而不是並排的兩塊字。
            if (editorial && perRow > 1)
                for (int c = 1; c < perRow; c++)
                    Rect(L.Left + c * (colW + gap) - gap / 2, top, L.RuleWeight,
                        Math.Min(rowH * rows - pad + 10, bodyH), art.Muted, 24);

            for (int j = 0; j < items.Length; j++)
            {
                float x = L.Left + (j % perRow) * (colW + gap);
                float y = top + (j / perRow) * rowH;

                // 編號：雜誌用大襯線數字當視覺錨點；商業用小字 + 短線，保持秩序。
                if (editorial)
                    Text(x, y, colW, 32, $"{j + 1:00}", 24, art.Accent, false, true, "l", 0);
                else
                {
                    Rect(x, y + 4, 22, 2, art.Accent, 100);
                    Text(x, y + 16, colW, 15, $"{j + 1:00}", 9.5f, art.Accent, true, false, "l", 1.8f);
                }
                Text(x, y + head, colW, rowH - head - pad + 12, items[j], size, ink, false, false, "l", 0);
            }
            return;
        }

        float indent = sources ? 0 : editorial ? 56 : 42;
        float rowPad = sources ? 30 : 26;
        if (!sources)
            while (size < L.BodySize * 1.5f
                   && (items.Max(item => TextHeight(item, contentW - indent, size + 1)) + rowPad) * items.Length <= bodyH * 0.72f)
                size += 1;

        float lineNatural = items.Max(item => TextHeight(item, contentW - indent, size)) + rowPad;
        float lineH = bodyH / items.Length;
        if (lineNatural * items.Length <= bodyH) { top += (bodyH - lineNatural * items.Length) / 2; lineH = lineNatural; }

        for (int j = 0; j < items.Length; j++)
        {
            float y = top + j * lineH;
            if (j > 0)
                Rect(L.Left, y - rowPad / 2.4f, contentW, L.RuleWeight, art.Muted, sources ? 22 : 30);

            if (sources)
            {
                Text(L.Left, y, contentW, lineH - rowPad + 10, items[j], size, muted, false, false, "l", 0);
                continue;
            }

            if (editorial)
                Text(L.Left, y - 2, 46, 28, $"{j + 1:00}", 19, art.Accent, false, true, "l", 0);
            else
                Text(L.Left, y + 3, 30, 15, $"{j + 1:00}", 9.5f, art.Accent, true, false, "l", 1.8f);

            Text(L.Left + indent, y, contentW - indent, lineH - rowPad + 10, items[j], size, ink, false, false, "l", 0);
        }
    }

    // ===== 內容切塊（維持 GPT 版的分頁保底：字不會被吃掉）=====

    /// <summary>
    /// 實際會印出幾頁。跟 Build 用同一套判斷（哪些頁不配圖、內容怎麼切塊），
    /// 兩邊分岔的話頁碼就會對不上，所以規則集中在 SplitIntoChunks。
    /// </summary>
    private static int CountPages(IReadOnlyList<PresentationSlidePayload> slides, byte[]? cover)
        => Math.Max(1, slides.Sum(slide => SplitIntoChunks(slide, cover).Count));

    /// <summary>把一張投影片的內容切成「實際頁面」的塊（封面多一塊；內容太長會有續頁）。</summary>
    private static List<string[]> SplitIntoChunks(PresentationSlidePayload slide, byte[]? cover)
    {
        var lines = slide.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)).SelectMany(SplitLong).ToArray();
        bool isCover = string.Equals(slide.Kind, "cover", StringComparison.OrdinalIgnoreCase);
        bool sources = string.Equals(slide.Kind, "sources", StringComparison.OrdinalIgnoreCase);

        var image = isCover ? cover : slide.ImageBytes;
        if (image != null && PptxBuilder.TryReadPngSize(image) == null) image = null;

        bool metrics = lines.Length > 0 && lines.All(b => b.Length <= 100)
                       && lines.Count(b => !string.IsNullOrEmpty(PptxBuilder.FindKeyFigure(b))) >= 2;
        if (metrics || sources) image = null;

        var chunks = Pack(lines, sources, metrics, image != null).ToList();
        if (isCover) chunks.Insert(0, Array.Empty<string>());
        if (chunks.Count == 0) chunks.Add(Array.Empty<string>());
        return chunks;
    }

    /// <summary>這段文字在指定寬度下實際要佔的高度（版面置中用）。</summary>
    private static float TextHeight(string text, float width, float size) => Wrap(text, width, size).Count * size * 1.4f;

    /// <summary>小標／頁尾之類的單行標籤：量到寬度就截斷，永遠不會換行撐破固定高度的框。</summary>
    private static string OneLine(string? text, float width, float size, float track = 0)
    {
        string t = (text ?? "").Trim().Replace("\n", " ");
        float used = 0;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            used += size * (c >= 0x2E80 ? 1.02f : char.IsWhiteSpace(c) ? .32f : char.IsUpper(c) ? .72f : .60f) + track;
            if (used > width - size) return i > 1 ? t[..(i - 1)] + "…" : "…";
        }
        return t;
    }

    private static IEnumerable<string[]> Pack(string[] lines, bool sources, bool metrics, bool hasImage)
    {
        int at = 0, maximum = sources ? 8 : metrics ? 4 : hasImage ? 3 : 5;
        while (at < lines.Length)
        {
            int count = Math.Min(maximum, lines.Length - at);
            while (count > 1 && !Fits(lines.Skip(at).Take(count).ToArray(), sources, metrics, hasImage)) count--;
            yield return lines.Skip(at).Take(count).ToArray();
            at += count;
        }
    }

    private static bool Fits(string[] items, bool sources, bool metrics, bool hasImage)
    {
        // 與實際排版同一組尺寸（版心約 250pt 高、832pt 寬），估太寬鬆就會排到溢出。
        bool columns = !sources && items.Length is >= 2 and <= 4 && items.All(b => b.Length <= 70);
        int rows = columns ? (int)Math.Ceiling(items.Length / (items.Length <= 2 ? (float)items.Length : 2f)) : 1;
        float width = metrics ? 176 : hasImage ? 420 : columns ? 390 : 770;
        float height = metrics ? 120
            : hasImage ? 250f / items.Length - 24
            : columns ? 250f / rows - 58
            : 250f / items.Length - 16;
        float size = sources ? 14.5f : 17.5f;
        return items.All(item =>
        {
            var stat = PptxBuilder.SplitStat(item);
            return Wrap(metrics ? stat.Caption : item, width, size).Count * size * 1.45f <= height;
        });
    }

    private static IEnumerable<string> SplitLong(string text)
    {
        // Retain every character; overflow becomes a continuation page instead of disappearing.
        text = text.Trim();
        while (text.Length > 170)
        {
            int end = 170;
            for (int i = 169; i >= 90; i--)
                if ("。；，、. ;,".Contains(text[i])) { end = i + 1; break; }
            yield return text[..end];
            text = text[end..];
        }
        if (text.Length > 0) yield return text;
    }

    private static void AddText(List<DeckElement> elements, float x, float y, float w, float h,
        string text, float size, string color, bool bold,
        string latinFont, string eaFont, string align, float track)
    {
        List<string> lines;
        // 內文縮到 12pt 在投影時後排就看不清楚了；縮不下去時寧可分頁（Pack 會處理），
        // 不要用「反正塞得下」換掉可讀性。小字標籤（頁碼、圖說）本來就小，不受此限。
        float minimum = Math.Min(size, size >= 13f ? DeckQuality.MinBodyFontSize : 8f);
        do
        {
            lines = Wrap(text, w, size);
            if (lines.Count * size * 1.4f <= h) break;
            size -= 1;
        } while (size >= minimum);

        if (size < minimum)
        {
            // 預設仍然丟出去，讓上層有機會重排；但重排過一輪之後（s_relaxedFit）
            // 就必須交得出東西——丟掉整份簡報從來不是比較好的選項。
            if (!s_relaxedFit)
                throw new InvalidOperationException("投影片文字超出版面，請精簡標題或增加頁數；已保留原內容，沒有截斷。");

            size = minimum;
            lines = Wrap(text, w, size);
        }

        // 大標的收尾字：「…下半年展／望」這種孤字是排版事故，收窄行寬重排把它拉回來。
        for (int attempt = 0; attempt < 3 && lines.Count > 1 && size >= 20; attempt++)
        {
            if (lines[^1].Length > 2 || lines[^1].Trim().Length == 0) break;
            var balanced = Wrap(text, w * (1 - 0.06f * (attempt + 1)), size);
            if (balanced.Count * size * 1.4f > h) break;
            lines = balanced;
        }

        // 中文的避頭點會讓標點掛在行尾，該行就比框寬；框不放寬的話 PDF/pptx 會自己再折一次行，
        // 折出去的字被裁掉就等於內容消失（曾經掉過「足，」兩個字）。框只往右放寬，位置不變。
        float widest = lines.Max(line => Measure(line, size) + Math.Max(0, line.Length - 1) * track);
        float boxW = align == "l" ? Math.Max(w, widest + size * 0.3f) : w;

        for (int i = 0; i < lines.Count; i++)
            elements.Add(new("text", x, y + i * size * 1.4f, boxW, size * 1.4f, color, lines[i], size, bold,
                Font: latinFont, EaFont: eaFont, Align: align, Track: track));
    }

    internal static float Measure(string text, float size) =>
        text.Sum(c => size * (c >= 0x2E80 ? 1.02f : char.IsWhiteSpace(c) ? .32f : char.IsUpper(c) ? .72f : .60f));

    internal static List<string> Wrap(string text, float width, float size)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = new StringBuilder();
            float used = 0;
            foreach (Match match in Regex.Matches(paragraph, @"[+−-]?\d+(?:,\d{3})*(?:\.\d+)?(?:[bmkBMK])?(?:\s*(?:[%％]|億澳元|億美元|億元|萬平方公里|萬人次|萬人|萬夜|澳元|美元|億|萬|元|人次|人|夜))?|[A-Za-z]+|[\s\S]"))
            {
                string token = match.Value;
                float advance = Measure(token, size);
                // Keep words, signed values and units together. Long URLs still wrap safely.
                if (used + advance > width && line.Length > 0 && !"，。；：！？、,.!?;:）)」』".Contains(token))
                {
                    result.Add(line.ToString()); line.Clear(); used = 0;
                }
                if (advance > width)
                {
                    foreach (char c in token)
                    {
                        float step = Measure(c.ToString(), size);
                        if (used + step > width && line.Length > 0) { result.Add(line.ToString()); line.Clear(); used = 0; }
                        line.Append(c); used += step;
                    }
                }
                else { line.Append(token); used += advance; }
            }
            result.Add(line.ToString());
        }
        return result;
    }
}
