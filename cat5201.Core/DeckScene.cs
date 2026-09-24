using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Cat5201;

// Coordinates are points on a 960 x 540 canvas. All native exports consume these exact elements.
internal sealed record DeckElement(string Kind, float X, float Y, float W, float H,
    string Color = "", string Text = "", float Size = 0, bool Bold = false, byte[]? Image = null);
internal sealed record DeckPage(int SourceOrder, IReadOnlyList<DeckElement> Elements);

internal static class DeckScene
{
    internal const float Width = 960, Height = 540;
    internal const string Font = "Microsoft JhengHei";

    internal static IReadOnlyList<DeckPage> Build(PresentationOutlinePayload outline, byte[]? cover)
    {
        var art = DeckArtDirection.ForTopic(outline.Title + " " + outline.Topic);
        var pages = new List<DeckPage>();
        var slides = outline.Slides.OrderBy(s => s.Order).ToList();
        if (slides.Count == 0)
            slides.Add(new() { Order = 1, Kind = "cover", Heading = outline.Title });
        foreach (var slide in slides)
        {
            var lines = slide.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)).SelectMany(SplitLong).ToArray();
            bool isCover = string.Equals(slide.Kind, "cover", StringComparison.OrdinalIgnoreCase);
            bool sources = string.Equals(slide.Kind, "sources", StringComparison.OrdinalIgnoreCase);
            var image = isCover ? cover : slide.ImageBytes;
            if (image != null && PptxBuilder.TryReadPngSize(image) == null) image = null;
            // Numeric evidence must not be replaced by a metaphorical illustration.
            bool metrics = lines.All(b => b.Length <= 100) && lines.Count(b => !string.IsNullOrEmpty(PptxBuilder.FindKeyFigure(b))) >= 2;
            if (metrics || sources) image = null;
            var chunks = Pack(lines, sources, metrics, image != null).ToList();
            if (isCover) chunks.Insert(0, Array.Empty<string>());
            if (chunks.Count == 0) chunks.Add(Array.Empty<string>());
            for (int chunk = 0; chunk < chunks.Count; chunk++)
            {
                var e = new List<DeckElement>();
                void Rect(float x, float y, float w, float h, string color) => e.Add(new("rect", x, y, w, h, color));
                void Text(float x, float y, float w, float h, string text, float size, string color, bool bold = false)
                    => AddText(e, x, y, w, h, text, size, color, bold);
                void Picture(float x, float y, float w, float h, byte[] bytes)
                {
                    var dimensions = PptxBuilder.TryReadPngSize(bytes);
                    if (dimensions == null) return;
                    float scale = Math.Min(w / dimensions.Value.Width, h / dimensions.Value.Height);
                    float iw = dimensions.Value.Width * scale, ih = dimensions.Value.Height * scale;
                    e.Add(new("image", x + (w - iw) / 2, y + (h - ih) / 2, iw, ih, Image: bytes));
                }

                bool coverPage = isCover && chunk == 0;
                bool statement = !coverPage && !sources && image == null && chunks[chunk].Length == 1 && chunks[chunk][0].Length <= 70;
                bool inverse = (coverPage || statement) && !art.DarkCover;
                string ink = inverse ? art.InverseInk : art.Ink;
                string muted = inverse ? art.InverseInk : art.Muted;
                Rect(0, 0, Width, Height, inverse ? art.InverseBackground : art.Background);
                string title = slide.Heading + (chunk > 0 ? "（續）" : "");
                if (coverPage)
                {
                    // Image and title have separate areas: no guessing a safe place to overlay text.
                    float textWidth = image != null ? 356 : 780;
                    Rect(52, 64, 48, 4, art.Accent);
                    int colon = title.IndexOf('：');
                    string coverTitle = colon > 0 && colon < 16 ? title.Insert(colon + 1, "\n") : title;
                    Text(52, 132, textWidth, 214, coverTitle, image != null ? 38 : 50, ink, true);
                    if (!string.IsNullOrWhiteSpace(outline.Topic))
                        Text(52, 380, textWidth, 100, outline.Topic, 18, muted);
                    if (image != null)
                    {
                        Picture(452, 38, 470, 424, image);
                        Text(452, 480, 470, 18, "概念示意", 11, muted);
                    }
                }
                else
                {
                    Rect(52, 42, 38, 3, art.Accent);
                    Text(52, 64, 844, 98, title, 32, ink, true);
                    var items = chunks[chunk];
                    if (metrics)
                    {
                        // A quiet 2 x 2 grid keeps units and explanations readable.
                        for (int j = 0; j < items.Length; j++)
                        {
                            float x = 52 + (j % 2) * 446, y = 188 + (j / 2) * 146;
                            var (value, caption) = PptxBuilder.SplitStat(items[j]);
                            if (value.Length == 0)
                            {
                                Text(x, y + 16, 394, 114, caption, 24, ink);
                                continue;
                            }
                            Text(x, y, 394, 55, value, 38, art.Accent, true);
                            Rect(x, y + 56, 394, .6f, art.Muted);
                            Text(x, y + 66, 394, items.Length <= 2 ? 204 : 64, caption, 18, ink);
                        }
                    }
                    else if (image != null)
                    {
                        Picture(528, 180, 380, 296, image);
                        Text(528, 480, 380, 18, "概念示意", 11, muted);
                        float row = 296f / Math.Max(1, items.Length);
                        for (int j = 0; j < items.Length; j++)
                            Text(52, 184 + j * row, 426, row - 16, items[j], 21, ink);
                    }
                    else if (items.Length == 0 || (items.Length == 1 && items[0].Length <= 70))
                    {
                        Text(52, 220, 820, 204, items.FirstOrDefault() ?? title, 36, ink);
                    }
                    else
                    {
                        bool columns = !sources && items.Length is >= 2 and <= 4 && items.All(b => b.Length <= 70);
                        for (int j = 0; j < items.Length; j++)
                        {
                            float x = columns ? 52 + j % 2 * 446 : 52;
                            float row = columns ? 146 : 300f / items.Length;
                            float y = 184 + (columns ? j / 2 : j) * row;
                            Rect(x, y + 4, 3, Math.Min(42, row - 12), art.Accent);
                            Text(x + 18, y, columns ? 388 : 824, row - 16, items[j], sources ? 16 : 22, ink);
                        }
                    }
                    Text(854, 504, 54, 18, $"{pages.Count + 1:00}", 11, muted);
                }
                pages.Add(new(slide.Order, e));
            }
        }
        return pages;
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
        bool columns = !sources && items.Length is >= 2 and <= 4 && items.All(b => b.Length <= 70);
        float width = metrics ? 394 : hasImage ? 426 : columns ? 388 : 824;
        float height = metrics ? (items.Length <= 2 ? 204 : 64) : hasImage ? 296f / items.Length - 16 : (columns ? 146 : 300f / items.Length) - 16;
        float size = sources ? 16 : 18;
        return items.All(item =>
        {
            var stat = PptxBuilder.SplitStat(item);
            float available = metrics && stat.Value.Length == 0 ? 114 : height;
            return Wrap(metrics ? stat.Caption : item, width, size).Count * size * 1.35f <= available;
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
        string text, float size, string color, bool bold)
    {
        List<string> lines;
        float minimum = Math.Min(size, 16);
        do
        {
            lines = Wrap(text, w, size);
            if (lines.Count * size * 1.35f <= h) break;
            size -= 1;
        } while (size >= minimum);
        if (size < minimum)
            throw new InvalidOperationException("投影片文字超出版面，請精簡標題或增加頁數；已保留原內容，沒有截斷。");
        for (int i = 0; i < lines.Count; i++)
            elements.Add(new("text", x, y + i * size * 1.35f, w, size * 1.35f, color, lines[i], size, bold));
    }

    internal static List<string> Wrap(string text, float width, float size)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = new StringBuilder();
            float used = 0;
            float Measure(string token) => token.Sum(c => size * (c >= 0x2E80 ? 1.02f : char.IsWhiteSpace(c) ? .32f : char.IsUpper(c) ? .72f : .60f));
            foreach (Match match in Regex.Matches(paragraph, @"[+−-]?\d+(?:,\d{3})*(?:\.\d+)?(?:[bmkBMK])?(?:\s*(?:[%％]|億澳元|億美元|億元|萬平方公里|萬人次|萬人|萬夜|澳元|美元|億|萬|元|人次|人|夜))?|[A-Za-z]+|[\s\S]"))
            {
                string token = match.Value;
                float advance = Measure(token);
                // Keep words, signed values and units together. Long URLs still wrap safely.
                if (used + advance > width && line.Length > 0 && !"，。；：！？、,.!?;:）)」』".Contains(token))
                {
                    result.Add(line.ToString()); line.Clear(); used = 0;
                }
                if (advance > width)
                {
                    foreach (char c in token)
                    {
                        float step = Measure(c.ToString());
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
