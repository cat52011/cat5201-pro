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

    /// <summary>這份 pptx 跟我們的編輯資料是什麼關係。</summary>
    public enum State
    {
        /// <summary>裡面根本沒有我們的編輯資料（外部來源的簡報）。</summary>
        Foreign,

        /// <summary>是我們產的，而且沒有被外部程式改過——可以單頁編輯。</summary>
        Native,

        /// <summary>是我們產的，但有人在 PowerPoint 裡動過版面——內容讀得出來，但不可覆寫。</summary>
        ManuallyEdited,
    }

    public sealed record ReadResult(Data? Data, State State);

    /// <summary>
    /// 讀取時連「為什麼不能編輯」一起回報。
    ///
    /// 只回 null 的話，呼叫端分不出「這是別人給的簡報」跟「這是我們產的、只是被手動改過」，
    /// 於是兩種情況都只能顯示同一句「請開啟原始簡報查看設計」——
    /// 後者其實內容完全讀得出來，使用者在預覽裡看到一片空白是沒道理的。
    /// </summary>
    public static ReadResult ReadDetailed(string path)
    {
        try
        {
            using var file = ReadOnlyOfficeFile.Pptx(path);
            var doc = file.Document;
            foreach (var part in doc.PresentationPart!.CustomXmlParts)
            {
                using var stream = part.GetStream();
                var root = XDocument.Load(stream).Root;
                if (root?.Name != XName.Get("deck", Namespace)) continue;
                var data = JsonSerializer.Deserialize<Data>(root.Value);
                if (data?.Outline?.Slides == null) return new ReadResult(null, State.Foreign);

                foreach (var slide in data.Outline.Slides)
                    if (data.Images?.TryGetValue(slide.Order, out var image) == true) slide.ImageBytes = image;

                // PowerPoint edits can make the embedded outline stale. Never overwrite those edits.
                //
                // 兩種指紋都接受：新版比對文字＋圖形幾何，舊版只比對文字。
                // 只認新版的話，換演算法之前產出的簡報會全部被判定成「已手改」而失去單頁編輯——
                // 那些檔案其實沒被動過，只是當初存的是另一套雜湊。
                bool intact = data.ContentHash == Fingerprint(doc.PresentationPart) ||
                              data.ContentHash == LegacyTextFingerprint(doc.PresentationPart);

                return new ReadResult(data, intact ? State.Native : State.ManuallyEdited);
            }
        }
        catch (Exception ex) { AppLog.Warn("Deck", "讀取原生簡報編輯資料失敗", ex); }
        return new ReadResult(null, State.Foreign);
    }

    /// <summary>可安全覆寫的編輯資料；被手動改過或非我們產出時回 null。</summary>
    public static Data? Read(string path)
    {
        var result = ReadDetailed(path);
        return result.State == State.Native ? result.Data : null;
    }

    /// <summary>
    /// 「這份簡報還是我們自己產的那一份嗎？」的指紋。
    ///
    /// 不能比對整包 XML：PowerPoint 只要另存就會重寫 XML，那樣每份簡報都會被當成已手改。
    /// 也不能只比對文字：使用者在 PowerPoint 裡搬動圖片、改大小、換顏色而沒改字，
    /// 我們就會以為沒被動過，重建時把他的調整全部洗掉。
    ///
    /// 所以比對「我們自己排出來的那些值」：每頁的形狀數量、每個形狀的名稱、位置、大小、文字，
    /// 以及圖片數量。這些值在 PowerPoint 單純另存時不會變，使用者真的動了才會變。
    /// </summary>
    private static string Fingerprint(PresentationPart part)
    {
        var sb = new StringBuilder();

        foreach (var id in part.Presentation.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>())
        {
            var slide = ((SlidePart)part.GetPartById(id.RelationshipId!)).Slide;
            sb.Append('\u0001');

            foreach (var shape in slide.Descendants<DocumentFormat.OpenXml.Presentation.Shape>())
            {
                sb.Append(shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value ?? "")
                  .Append('\u0003');
                AppendGeometry(sb, shape.ShapeProperties?.Transform2D);

                foreach (var run in shape.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
                    sb.Append(run.Text).Append('\u0002');

                sb.Append('\u0004');
            }

            foreach (var picture in slide.Descendants<DocumentFormat.OpenXml.Presentation.Picture>())
            {
                sb.Append("pic").Append('\u0003');
                AppendGeometry(sb, picture.ShapeProperties?.Transform2D);
                sb.Append('\u0004');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void AppendGeometry(StringBuilder sb, DocumentFormat.OpenXml.Drawing.Transform2D? transform)
    {
        var offset = transform?.Offset;
        var extents = transform?.Extents;
        sb.Append(offset?.X?.Value ?? 0).Append(',')
          .Append(offset?.Y?.Value ?? 0).Append(',')
          .Append(extents?.Cx?.Value ?? 0).Append(',')
          .Append(extents?.Cy?.Value ?? 0).Append('\u0003');
    }

    /// <summary>
    /// 舊版指紋（只比對文字）。換成含幾何的版本之前產出的簡報都是用這套存的，
    /// 讀取時仍要認得，否則那些檔案會無法單頁編輯。新檔一律用新版寫入，不再產生這種雜湊。
    /// </summary>
    private static string LegacyTextFingerprint(PresentationPart part)
    {
        var text = new StringBuilder();
        foreach (var id in part.Presentation.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>())
        {
            text.Append('\u0001');
            foreach (var run in ((SlidePart)part.GetPartById(id.RelationshipId!)).Slide
                         .Descendants<DocumentFormat.OpenXml.Drawing.Text>())
                text.Append(run.Text).Append('\u0002');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static int? SourceOrderForPage(Data data, int page)
    {
        var pages = DeckScene.Build(data.Outline, data.Cover);
        return page > 0 && page <= pages.Count ? pages[page - 1].SourceOrder : null;
    }
}
