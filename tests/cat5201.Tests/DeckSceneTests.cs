using Cat5201;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Xunit;

namespace Cat5201.Tests;

public class DeckSceneTests
{
    private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=");
    private static PresentationOutlinePayload Outline(params PresentationSlidePayload[] slides) => new()
    { Title = "自然旅遊", Topic = "塔斯馬尼亞", Slides = slides, SlideCount = slides.Length };

    [Theory]
    [InlineData("成長率 -3.9%", "-3.9%")]
    [InlineData("變動 −12.5％", "−12.5％")]
    [InlineData("訪客 +2025 人", "+2025人")]
    [InlineData("住宿 1,291 萬夜", "1,291萬夜")]
    public void KeyFiguresKeepSignsAndCompleteUnits(string text, string expected)
        => Assert.Equal(expected, PptxBuilder.FindKeyFigure(text));

    [Fact]
    public void NumericEvidenceTakesPriorityOverDecorativeImage()
    {
        var pages = DeckScene.Build(Outline(new PresentationSlidePayload() { Order = 1, Heading = "市場", ImageBytes = Pixel,
            Bullets = new[] { "營收 7.586 億澳元", "成長 -3.9%", "市場說明" } }), null);
        Assert.Single(pages);
        Assert.DoesNotContain(pages[0].Elements, e => e.Kind == "image");
        Assert.Contains(pages[0].Elements, e => e.Text == "-3.9%" && e.Size >= 30);
        Assert.Contains(pages[0].Elements, e => e.Text == "市場說明");
    }

    [Fact]
    public void LongBodyIsPaginatedWithoutLosingContentOrShrinkingToTinyType()
    {
        var input = Enumerable.Range(0, 8).Select(i => $"第{i}段" + new string('文', 165)).ToArray();
        var outline = Outline(new PresentationSlidePayload() { Order = 7, Heading = "長內容", Bullets = input, ImageBytes = Pixel });
        var pages = DeckScene.Build(outline, null);
        Assert.True(pages.Count > 1);
        var body = pages.SelectMany(p => p.Elements).Where(e => e.Kind == "text" && e.Text.Contains('文')).ToList();
        Assert.Equal(string.Concat(input), string.Concat(body.Select(e => e.Text)));
        Assert.All(body, e => Assert.InRange(e.Size, 14, 36));
        Assert.All(pages, p => Assert.Equal(7, p.SourceOrder));
        Assert.All(pages.SelectMany(p => p.Elements), e => { Assert.True(e.X + e.W <= 960.1); Assert.True(e.Y + e.H <= 540.1); });
    }

    [Fact]
    public void ImageUsesItsAspectRatioAndNeverOverlapsText()
    {
        var page = Assert.Single(DeckScene.Build(Outline(new PresentationSlidePayload() { Order = 1, Heading = "作品", ImageBytes = Pixel,
            Bullets = new[] { "作品細節與材質" } }), null));
        var image = Assert.Single(page.Elements, e => e.Kind == "image");
        Assert.Equal(image.W, image.H);
        Assert.All(page.Elements.Where(e => e.Kind == "text"), e =>
            Assert.True(e.X >= image.X + image.W || e.X + e.W <= image.X || e.Y >= image.Y + image.H || e.Y + e.H <= image.Y));
    }

    [Fact]
    public void NativeRoundTripRetainsAllImagesAndValidOpenXml()
    {
        var outline = Outline(new PresentationSlidePayload() { Order = 1, Kind = "cover", Heading = "封面" },
            new() { Order = 2, Heading = "作品", Bullets = new[] { "保留圖片" }, ImageBytes = Pixel });
        byte[] bytes = PptxBuilder.Build(outline, Pixel);
        using var doc = PresentationDocument.Open(new MemoryStream(bytes), false);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pptx");
        try
        {
            File.WriteAllBytes(path, bytes);
            var data = Assert.IsType<NativeDeckStorage.Data>(NativeDeckStorage.Read(path));
            Assert.Equal(Pixel, data.Cover);
            Assert.Equal(Pixel, data.Outline.Slides[1].ImageBytes);
            Assert.Equal(2, NativeDeckStorage.SourceOrderForPage(data, 2));
            Assert.Null(NativeDeckStorage.SourceOrderForPage(data, 3));
            using (var edited = PresentationDocument.Open(path, true))
            {
                edited.PresentationPart!.SlideParts.First().Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>().First().Text = "PowerPoint 手動修改";
            }
            Assert.Null(NativeDeckStorage.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CoverBulletContentIsRetainedAndNumericTokensStayTogether()
    {
        var pages = DeckScene.Build(Outline(new PresentationSlidePayload() { Order = 1, Kind = "cover", Heading = "封面", Bullets = new[] { "必要內容" } }), null);
        Assert.Contains(pages.SelectMany(p => p.Elements), e => e.Text == "必要內容");
        var lines = DeckScene.Wrap("停留夜數年增1.6%，展現深度旅遊實力", 120, 22);
        Assert.Contains(lines, line => line.Contains("1.6%"));
    }

    [Fact]
    public void ContinuationPageNeitherRepeatsTheImageNorLosesItsEditor()
    {
        // 一張投影片放不下時會拆成續頁。續頁若沿用同一張圖，同一張照片會在相鄰兩頁各出現一次；
        // 續頁若沒有自己的編輯區，那一頁就完全動不了（2026-09-25 使用者實測到的兩個問題）。
        // 不要用「第1個」這種寫法：帶單位的數字會讓這頁被判定成數據頁，數據頁本來就不配圖。
        var bullets = new[] { "甲", "乙", "丙", "丁" }
            .Select(tag => $"重點{tag}：" + new string('內', 60))
            .ToArray();
        var outline = Outline(
            new PresentationSlidePayload { Order = 1, Kind = "cover", Heading = "封面" },
            new PresentationSlidePayload { Order = 2, Heading = "核心景點", Bullets = bullets, ImageBytes = Pixel });

        var pages = DeckScene.Build(outline, null);
        var forSlide2 = pages.Where(p => p.SourceOrder == 2).ToList();
        Assert.True(forSlide2.Count > 1, "這組內容應該會拆成續頁");

        Assert.Single(forSlide2[0].Elements, e => e.Kind == "image");
        Assert.All(forSlide2.Skip(1), p => Assert.DoesNotContain(p.Elements, e => e.Kind == "image"));

        string html = DeckHtmlRenderer.Build(outline, allowEdit: true);
        Assert.Equal(pages.Count, System.Text.RegularExpressions.Regex.Matches(html, "<details").Count);
        Assert.Contains("（接續）", html);
    }

    [Fact]
    public void HangingPunctuationNeverOverflowsItsBoxSoRenderersCannotClipCharacters()
    {
        // 避頭點會讓標點掛在行尾、該行比文字框寬；框沒放寬的話 PDF 會再折一次行並裁掉，字就這樣消失。
        var bullets = new[]
        {
            "通路端反映：貨架上辨識度不足，消費者記得商品卻記不得品牌。",
            "既有識別停留在 2019 年的扁平化語言，與現在的產品線脫節。",
        };
        var outline = new PresentationOutlinePayload
        {
            Title = "品牌視覺設計提案",
            Topic = "時尚與工藝的再編輯",
            SlideCount = 1,
            Slides = new[] { new PresentationSlidePayload { Order = 1, Heading = "為什麼現在重做視覺", Bullets = bullets } },
        };

        var page = Assert.Single(DeckScene.Build(outline, null));
        var texts = page.Elements.Where(e => e.Kind == "text").ToList();

        string rendered = string.Concat(texts.Select(e => e.Text));
        foreach (var bullet in bullets)
            Assert.Contains(bullet, rendered);

        Assert.All(texts, e => Assert.True(
            DeckScene.Measure(e.Text, e.Size) <= e.W + 0.5f,
            $"「{e.Text}」寬 {DeckScene.Measure(e.Text, e.Size):F1} 超出框寬 {e.W:F1}，渲染端會重折並裁字"));
    }

    [Fact]
    public void PreviewEscapesEditableTextAndKeepsControlsOutsideTheSlide()
    {
        var outline = Outline(new PresentationSlidePayload() { Order = 1, Heading = "<script>bad</script>", Bullets = new[] { "A & B" } });
        string html = DeckHtmlRenderer.Build(outline, allowEdit: true);
        Assert.DoesNotContain("<script>bad</script>", html);
        Assert.Contains("&lt;script&gt;bad&lt;/script&gt;", html);
        Assert.Contains("</svg><details", html);      // 編輯區在投影片外面，不會混進版面
        Assert.Contains("data-order='1'", html);

        // 重新生成是「單頁」的，而且每頁自己帶修改要求輸入框。
        Assert.Contains("class='instruction'", html);
        Assert.Contains("只重新生成這一頁", html);
        Assert.Contains("d.querySelector('.instruction').value", html);

        // 單頁也能換／加／移除配圖，圖片描述有自己的輸入框。
        Assert.Contains("class='image-brief'", html);
        Assert.Contains("data-action='image'", html);
        Assert.Contains("生成配圖", html);
    }
}
