using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Cat5201;

/// <summary>
/// 簡報配圖的裁切。
///
/// 為什麼需要：圖片模型回來的比例（1536×1024…）跟版面上的圖框幾乎不會一樣，
/// 等比置中就會在框裡留一條空白帶，看起來像「圖沒放好」而不是設計。
/// 這裡先把圖裁成圖框的比例，pptx / PDF / 預覽三邊拿到的就是同一張滿版圖，不必各自想辦法裁。
/// </summary>
internal static class DeckImage
{
    /// <summary>裁成指定長寬比的 PNG；來源太小、比例已相符或解碼失敗回 null（呼叫端退回等比置中）。</summary>
    internal static byte[]? CropToRatio(byte[] png, float ratio)
    {
        if (png == null || png.Length == 0 || ratio <= 0) return null;

        try
        {
            using var input = new MemoryStream(png, writable: false);
            using var source = new Bitmap(input);
            if (source.Width < 200 || source.Height < 200) return null;

            float actual = (float)source.Width / source.Height;
            if (Math.Abs(actual - ratio) < 0.01f) return null;

            int w, h;
            if (actual > ratio) { h = source.Height; w = (int)Math.Round(h * ratio); }
            else { w = source.Width; h = (int)Math.Round(w / ratio); }

            w = Math.Clamp(w, 1, source.Width);
            h = Math.Clamp(h, 1, source.Height);

            // 主體通常略高於正中（地平線、視線），所以直向多裁掉下面一點。
            int x = (source.Width - w) / 2;
            int y = (int)((source.Height - h) * 0.4f);

            using var cropped = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
            }

            using var output = new MemoryStream();
            cropped.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Deck", "配圖裁切失敗，改用等比置中", ex);
            return null;
        }
    }
}
