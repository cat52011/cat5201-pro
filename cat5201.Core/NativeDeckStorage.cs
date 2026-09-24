using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using System.Text;
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;

namespace Cat5201;

/// <summary>Only decks with our lossless editing payload may be rebuilt by the native editor.</summary>
public static class NativeDeckStorage
{
    private const string Namespace = "urn:cat5201:native-deck:v2";
    public sealed record Data(PresentationOutlinePayload Outline, byte[]? Cover, Dictionary<int, byte[]> Images, string ContentHash);

    internal static void Write(PresentationPart part, PresentationOutlinePayload outline, byte[]? cover)
    {
        var images = outline.Slides.Where(s => s.ImageBytes is { Length: > 0 })
            .GroupBy(s => s.Order).ToDictionary(g => g.Key, g => g.First().ImageBytes!);
        var xmlPart = part.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using var stream = xmlPart.GetStream(FileMode.Create, FileAccess.Write);
        new XDocument(new XElement(XName.Get("deck", Namespace), JsonSerializer.Serialize(new Data(outline, cover, images, Fingerprint(part))))).Save(stream);
    }

    public static Data? Read(string path)
    {
        try
        {
            using var doc = PresentationDocument.Open(path, false);
            foreach (var part in doc.PresentationPart!.CustomXmlParts)
            {
                using var stream = part.GetStream();
                var root = XDocument.Load(stream).Root;
                if (root?.Name != XName.Get("deck", Namespace)) continue;
                var data = JsonSerializer.Deserialize<Data>(root.Value);
                if (data?.Outline?.Slides == null) return null;
                // PowerPoint edits can make the embedded outline stale. Never overwrite those edits.
                if (data.ContentHash != Fingerprint(doc.PresentationPart)) return null;
                foreach (var slide in data.Outline.Slides)
                    if (data.Images?.TryGetValue(slide.Order, out var image) == true) slide.ImageBytes = image;
                return data;
            }
        }
        catch (Exception ex) { AppLog.Warn("Deck", "讀取原生簡報編輯資料失敗", ex); }
        return null;
    }

    private static string Fingerprint(PresentationPart part)
    {
        var xml = new StringBuilder(part.Presentation.OuterXml);
        foreach (var id in part.Presentation.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>())
            xml.Append(((SlidePart)part.GetPartById(id.RelationshipId!)).Slide.OuterXml);
        foreach (var master in part.SlideMasterParts)
            xml.Append(master.SlideMaster.OuterXml).Append(master.ThemePart?.Theme.OuterXml);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml.ToString())));
    }

    public static int? SourceOrderForPage(Data data, int page)
    {
        var pages = DeckScene.Build(data.Outline, data.Cover);
        return page > 0 && page <= pages.Count ? pages[page - 1].SourceOrder : null;
    }
}
