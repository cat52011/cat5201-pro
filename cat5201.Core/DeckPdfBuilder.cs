using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Cat5201;

/// <summary>Native PDF uses the same coordinates, line breaks and fitted images as the PPTX.</summary>
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
                            if (e.Kind == "rect") box.Background("#" + e.Color);
                            else if (e.Kind == "image") box.Image(e.Image!).FitArea();
                            else box.Text(t =>
                            {
                                var span = t.Span(e.Text).FontSize(e.Size).FontColor("#" + e.Color).LineHeight(1);
                                if (e.Bold) span.Bold();
                            });
                        }
                    });
                });
        }).GeneratePdf();
    }
}
