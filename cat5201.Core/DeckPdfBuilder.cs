using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Cat5201;

/// <summary>Native PDF uses the same coordinates, fonts, line breaks and fitted images as the PPTX.</summary>
public static class DeckPdfBuilder
{
    static DeckPdfBuilder() => QuestPDF.Settings.License = LicenseType.Community;
    public static byte[] Build(PresentationOutlinePayload outline, byte[]? coverImagePng)
    {
        var pages = DeckScene.Build(outline, coverImagePng);
        return Document.Create(document =>
        {
            foreach (var scene in pages)
                document.Page(page =>
                {
                    page.Size(new PageSize(DeckScene.Width, DeckScene.Height));
                    page.Margin(0);
                    page.DefaultTextStyle(t => t.FontFamily(DeckScene.Font));
                    page.Content().Layers(layers =>
                    {
                        layers.PrimaryLayer().Width(DeckScene.Width).Height(DeckScene.Height);
                        foreach (var e in scene.Elements)
                        {
                            var box = layers.Layer().OffsetX(e.X).OffsetY(e.Y).Width(e.W).Height(e.H);
                            if (e.Kind == "rect")
                                box.Background(e.Alpha >= 100 ? "#" + e.Color : $"#{(int)(e.Alpha * 2.55f):X2}{e.Color}");
                            else if (e.Kind == "image") box.Image(e.Image!).FitArea();
                            else box.Text(t =>
                            {
                                if (e.Align == "r") t.AlignRight();
                                else if (e.Align == "c") t.AlignCenter();

                                var span = t.Span(e.Text).FontSize(e.Size).FontColor("#" + e.Color).LineHeight(1);
                                // 西文字體在前、中文字體墊後：襯線大標的英文用 Georgia，中文自動退到明體，不會變豆腐。
                                if (!string.IsNullOrEmpty(e.Font) || !string.IsNullOrEmpty(e.EaFont))
                                    span.FontFamily(
                                        string.IsNullOrEmpty(e.Font) ? DeckScene.Font : e.Font,
                                        string.IsNullOrEmpty(e.EaFont) ? DeckScene.Font : e.EaFont,
                                        DeckScene.Font);
                                if (e.Track > 0 && e.Size > 0) span.LetterSpacing(e.Track / e.Size);
                                if (e.Bold) span.Bold();
                            });
                        }
                    });
                });
        }).GeneratePdf();
    }
}
