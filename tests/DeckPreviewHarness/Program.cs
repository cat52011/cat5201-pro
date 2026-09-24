using Cat5201;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

// Offline visual regression tool. Reuses the contents of an old native deck without changing it.
if (args.Length != 2) throw new ArgumentException("Usage: input.pptx output-directory");
var slides = new List<PresentationSlidePayload>();
byte[]? cover = null;
string subtitle = "";
using (var original = PresentationDocument.Open(args[0], false))
{
    var presentation = original.PresentationPart!;
    foreach (var id in presentation.Presentation.SlideIdList!.Elements<P.SlideId>())
    {
        var part = (SlidePart)presentation.GetPartById(id.RelationshipId!);
        var shapes = part.Slide.Descendants<P.Shape>().ToList();
        string Name(P.Shape shape) => shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value ?? "";
        string Text(P.Shape shape) => string.Concat(shape.Descendants<A.Text>().Select(t => t.Text));
        var title = shapes.FirstOrDefault(s => Name(s) is "CoverTitle" or "Title");
        if (title == null) throw new InvalidOperationException("This tool expects the prior native deck format.");
        if (slides.Count == 0)
        {
            subtitle = shapes.Where(s => Name(s) == "CoverSub").Select(Text).FirstOrDefault() ?? "";
            var imagePart = part.ImageParts.FirstOrDefault();
            if (imagePart != null)
            {
                using var stream = imagePart.GetStream(); using var buffer = new MemoryStream();
                stream.CopyTo(buffer); cover = buffer.ToArray();
            }
        }
        var bullets = shapes.Where(s => Name(s) == "Body" || Name(s).StartsWith("ColText") || Name(s).StartsWith("Sources"))
            .SelectMany(s => s.Descendants<A.Paragraph>().Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text))))
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        slides.Add(new() { Order = slides.Count + 1, Kind = slides.Count == 0 ? "cover" : Text(title).Contains("資料來源") ? "sources" : "content", Heading = Text(title), Bullets = bullets });
        Console.WriteLine($"{slides.Count}: {Text(title)} ({bullets.Length} items)");
    }
}
var outline = new PresentationOutlinePayload { Title = slides[0].Heading, Topic = subtitle, Slides = slides, SlideCount = slides.Count };
Directory.CreateDirectory(args[1]);
string stem = Path.Combine(args[1], "塔斯馬尼亞_排版驗證");
File.WriteAllBytes(stem + ".pptx", PptxBuilder.Build(outline, cover));
File.WriteAllBytes(stem + ".pdf", DeckPdfBuilder.Build(outline, cover));
File.WriteAllText(stem + ".html", DeckHtmlRenderer.Build(outline, new[] { cover }));
using var result = PresentationDocument.Open(stem + ".pptx", false);
var errors = new OpenXmlValidator().Validate(result).ToList();
foreach (var error in errors.Take(10)) Console.WriteLine(error.Description + " " + error.Path?.XPath);
if (errors.Count > 0) throw new InvalidOperationException($"{errors.Count} invalid OpenXML elements");
Console.WriteLine($"Valid PPTX: {result.PresentationPart!.SlideParts.Count()} pages. Saved to {stem}");
