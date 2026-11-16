using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cat5201
{
    // MainWindow 部分類別 — 標題列配色（純外觀）。
    // 只在 Windows 11 生效；舊系統不支援這兩個屬性，呼叫會安靜失敗，標題列維持系統預設。
    public partial class MainWindow
    {
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 原始主題：b4a30e3 沒有標題列配色，維持系統預設。
            if (ThemeColors.IsOriginal)
                return;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int caption = ColorRef(0xED, 0xE6, 0xDA);   // 與側邊欄同一個燕麥色
                int text = ColorRef(0x4A, 0x3B, 0x30);      // 深棕標題文字
                DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
            }
            catch
            {
                // 外觀用途，失敗不影響任何功能。
            }
        }

        // COLORREF 是 0x00BBGGRR。
        private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
    }
}
