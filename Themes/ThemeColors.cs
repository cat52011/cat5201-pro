using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace Cat5201
{
    /// <summary>
    /// 程式碼裡寫死的顏色／形狀的主題入口。
    ///
    /// 呼叫點寫「奶油」的原值，侘寂靠 <see cref="GradeWabi"/> 由奶油值算出來；
    /// 原始主題（git commit b4a30e3）沒有公式可算——同一個奶油色在不同位置對應不同的原始色——
    /// 所以每個呼叫點明確帶上原始值：
    ///   <c>ThemeColors.Rgb(0x2A, 0x20, 0x19, 0x000000)</c>（第四個參數＝0xRRGGBB）、
    ///   <c>ThemeColors.Hex("#F8F4ED", "#FFFFFF")</c>（第二個參數＝原始色碼）。
    /// 沒帶原始值的呼叫（例如 DialogChrome，原始主題不用它）在原始主題下回傳奶油值。
    /// 形狀等非顏色的差異用 <see cref="Pick{T}"/>。
    ///
    /// XAML 的顏色走資源字典（Themes\Cream.xaml、Themes\Wabi.xaml），不經過這裡。
    /// 目前主題由 <see cref="InterfaceThemes.Apply"/> 在啟動最前面決定，之後不再改變（換主題要重新啟動）。
    /// </summary>
    internal static class ThemeColors
    {
        private static bool IsWabi => InterfaceThemes.Current == InterfaceTheme.Wabi;

        /// <summary>目前是不是原始主題（b4a30e3 的外觀）。程式碼裡結構不同的地方（彈窗、標題列）用它分支。</summary>
        internal static bool IsOriginal => InterfaceThemes.Current == InterfaceTheme.Original;

        /// <summary>奶油與侘寂用 <paramref name="cream"/>，原始主題用 <paramref name="original"/>。</summary>
        internal static T Pick<T>(T cream, T original) => IsOriginal ? original : cream;

        /// <summary>取代 <c>Color.FromRgb</c>，並帶上原始主題的 0xRRGGBB。</summary>
        internal static Color Rgb(byte r, byte g, byte b, uint original)
        {
            if (IsOriginal)
                return Color.FromRgb((byte)(original >> 16), (byte)(original >> 8), (byte)original);
            return Rgb(r, g, b);
        }

        /// <summary>取代寫死的色碼字串，並帶上原始主題的色碼（或顏色名稱）。</summary>
        internal static string Hex(string hex, string original) => IsOriginal ? original : Hex(hex);

        /// <summary>取代 <c>Color.FromRgb</c>。</summary>
        internal static Color Rgb(byte r, byte g, byte b)
        {
            if (IsWabi)
                (r, g, b) = GradeWabi(r, g, b);
            return Color.FromRgb(r, g, b);
        }

        /// <summary>取代 <c>Color.FromArgb</c>（透明度不變）。</summary>
        internal static Color Argb(byte a, byte r, byte g, byte b)
        {
            if (IsWabi)
                (r, g, b) = GradeWabi(r, g, b);
            return Color.FromArgb(a, r, g, b);
        }

        /// <summary>
        /// 取代寫死的 <c>"#RRGGBB"</c> 或 <c>"#AARRGGBB"</c> 字串。
        /// 其他格式（顏色名稱、空字串）原樣回傳。
        /// </summary>
        internal static string Hex(string hex)
        {
            if (!IsWabi)
                return hex;
            return GradeHex(hex);
        }

        /// <summary>彈窗外框的圓角（DialogChrome）。</summary>
        internal static CornerRadius DialogCornerRadius =>
            IsWabi ? new CornerRadius(8, 3, 9, 4) : IsOriginal ? new CornerRadius(12) : new CornerRadius(2);

        // ---------------------------------------------------------------- 侘寂調色

        private static readonly ConcurrentDictionary<string, string> _hexCache = new(StringComparer.Ordinal);

        /// <summary>把奶油的色碼字串轉成侘寂的色碼字串（與 <see cref="Hex"/> 在侘寂主題下的結果相同，但不看目前主題）。</summary>
        internal static string GradeHex(string hex)
        {
            if (!IsHexColor(hex))
                return hex;
            return _hexCache.GetOrAdd(hex, static h =>
            {
                string digits = h.Substring(1);
                string alpha = digits.Length == 8 ? digits.Substring(0, 2) : "";
                string rgb = digits.Length == 8 ? digits.Substring(2) : digits;
                byte r = Convert.ToByte(rgb.Substring(0, 2), 16);
                byte g = Convert.ToByte(rgb.Substring(2, 2), 16);
                byte b = Convert.ToByte(rgb.Substring(4, 2), 16);
                var (nr, ng, nb) = GradeWabi(r, g, b);
                string o = nr.ToString("X2") + ng.ToString("X2") + nb.ToString("X2");
                // 原字串沒有大寫字母就輸出小寫（tools\theme\wabi_grade.py 的規則）
                if (digits == digits.ToLowerInvariant())
                    o = o.ToLowerInvariant();
                return "#" + alpha + o;
            });
        }

        private static bool IsHexColor(string? s)
        {
            if (s == null || (s.Length != 7 && s.Length != 9) || s[0] != '#')
                return false;
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// 侘寂・陶器調色：奶油的一個顏色 → 侘寂的顏色。
        /// 逐行移植自 tools\theme\wabi_grade.py 的 grade()，浮點運算順序一樣，輸出必須與 Python 版逐色相同
        /// （tests\cat5201.Tests\ThemeTests.cs 用 TestData\wabi_grade_table.csv 比對）。
        /// 改這裡一定要同步改 Python 版與資料表。
        /// </summary>
        internal static (byte R, byte G, byte B) GradeWabi(byte r, byte g, byte b)
        {
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            var (h, l, _) = RgbToHls(rf, gf, bf);
            double c = Math.Max(rf, Math.Max(gf, bf)) - Math.Min(rf, Math.Min(gf, bf));
            double hd = h * 360;
            bool neutral = c < 0.02;

            if (l > 0.85)                                   // 紙
            {
                double l2 = l - 0.045 - (l - 0.85) * 0.1;
                double c2 = c * 0.9 + 0.03;
                double h2 = (neutral || (hd >= 15 && hd <= 60)) ? 40 : hd;
                if (!neutral && !(hd >= 15 && hd <= 60))    // 淡綠、淡紅、淡藍底
                {
                    c2 = c * 0.6 + 0.02;
                    if (hd > 60 && hd < 180) h2 = 80 + (hd - 80) * 0.3;
                    else if (hd >= 330 || hd < 15) h2 = 12;
                    else h2 = 205 + (hd - 205) * 0.4;
                }
                return FromHlc(h2 / 360, l2, c2);
            }

            if (neutral)                                    // 灰：帶一點暖
                return FromHlc(32.0 / 360, l, Math.Max(c, 0.025));
            if (hd >= 15 && hd <= 60)                       // 暖色：墨、駝、赭
            {
                if (l < 0.3)
                    return FromHlc((hd + 3) / 360, l, c * 0.45);
                if (c > 0.35)                               // 鮮橘、鮮黃 → 赭
                    return FromHlc((hd - 4) / 360, l * 0.9, c * 0.6);
                return FromHlc((hd - 6) / 360, l * 0.99, c * 0.92);
            }
            if (hd > 60 && hd < 180)                        // 綠 → 苔
                return FromHlc((80 + (hd - 80) * 0.3) / 360, l * 0.95, c * 0.55);
            if (hd >= 330 || hd < 15)                       // 紅 → 鐵鏽
                return FromHlc(12.0 / 360, l * 0.92, c * 0.75);
            return FromHlc((205 + (hd - 205) * 0.4) / 360, l, c * 0.45);   // 藍紫 → 灰藍
        }

        private static double Clamp01(double x) => Math.Max(0.0, Math.Min(1.0, x));

        /// <summary>Python 的 <c>x % 1.0</c>（結果恆在 [0,1)）。</summary>
        private static double Mod1(double x)
        {
            double m = x % 1.0;
            if (m < 0) m += 1.0;
            return m;
        }

        private static (byte, byte, byte) FromHlc(double h, double l, double c)
        {
            double denom = 1 - Math.Abs(2 * l - 1);
            double s = denom <= 1e-6 ? 0.0 : Clamp01(c / denom);
            var (r, g, b) = HlsToRgb(Mod1(h), Clamp01(l), s);
            return (ToByte(r), ToByte(g), ToByte(b));
        }

        private static byte ToByte(double v) => (byte)Math.Round(Clamp01(v) * 255, MidpointRounding.ToEven);

        // 以下兩個是 Python colorsys 的逐行移植（3.13 版）。
        private static (double H, double L, double S) RgbToHls(double r, double g, double b)
        {
            double maxc = Math.Max(r, Math.Max(g, b));
            double minc = Math.Min(r, Math.Min(g, b));
            double sumc = maxc + minc;
            double rangec = maxc - minc;
            double l = sumc / 2.0;
            if (minc == maxc)
                return (0.0, l, 0.0);
            double s = l <= 0.5 ? rangec / sumc : rangec / (2.0 - maxc - minc);
            double rc = (maxc - r) / rangec;
            double gc = (maxc - g) / rangec;
            double bc = (maxc - b) / rangec;
            double h;
            if (r == maxc) h = bc - gc;
            else if (g == maxc) h = 2.0 + rc - bc;
            else h = 4.0 + gc - rc;
            h = Mod1(h / 6.0);
            return (h, l, s);
        }

        private const double OneThird = 1.0 / 3.0;
        private const double OneSixth = 1.0 / 6.0;
        private const double TwoThird = 2.0 / 3.0;

        private static (double, double, double) HlsToRgb(double h, double l, double s)
        {
            if (s == 0.0)
                return (l, l, l);
            double m2 = l <= 0.5 ? l * (1.0 + s) : l + s - (l * s);
            double m1 = 2.0 * l - m2;
            return (HlsV(m1, m2, h + OneThird), HlsV(m1, m2, h), HlsV(m1, m2, h - OneThird));
        }

        private static double HlsV(double m1, double m2, double hue)
        {
            hue = Mod1(hue);
            if (hue < OneSixth)
                return m1 + (m2 - m1) * hue * 6.0;
            if (hue < 0.5)
                return m2;
            if (hue < TwoThird)
                return m1 + (m2 - m1) * (TwoThird - hue) * 6.0;
            return m1;
        }
    }
}
