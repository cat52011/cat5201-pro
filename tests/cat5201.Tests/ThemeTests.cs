using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 介面風格（奶油／侘寂）。
    ///  1. 偏好 InterfaceTheme：存讀往返、缺欄位＝奶油。
    ///  2. C# 版 grade()（ThemeColors.GradeWabi）逐色等於 tools\theme\wabi_grade.py 的輸出（TestData\wabi_grade_table.csv）。
    ///  3. 兩個主題字典：鍵一致、奶油的值＝鍵名的色碼（取自 git HEAD）、侘寂的值＝grade(奶油值)。
    ///  4. XAML 用到的每個 StaticResource 鍵兩套主題都有（缺鍵會讓 XAML 載入時丟例外）。
    /// </summary>
    [CollectionDefinition("Theme", DisableParallelization = true)]
    public class ThemeCollection { }

    [Collection("Theme")]
    public class ThemeTests
    {
        // ------------------------------------------------------------ 偏好

        [Fact]
        public void Preferences_DefaultInterfaceTheme_IsCream()
        {
            var prefs = new MainWindow.UserPreferencesState();
            Assert.Equal("Cream", prefs.InterfaceTheme);
        }

        [Fact]
        public void Preferences_MissingInterfaceTheme_LoadsAsCream()
        {
            // 加介面風格之前的偏好檔：沒有 InterfaceTheme 欄位。
            var prefs = JsonSerializer.Deserialize<MainWindow.UserPreferencesState>("{ \"DailyBudgetTwd\": 50 }");
            Assert.NotNull(prefs);
            Assert.Equal("Cream", prefs!.InterfaceTheme);
            Assert.Equal(InterfaceTheme.Cream, InterfaceThemes.Parse(prefs.InterfaceTheme));
            Assert.Equal(InterfaceTheme.Cream, InterfaceThemes.ParseFromPreferencesJson("{ \"DailyBudgetTwd\": 50 }"));
        }

        [Fact]
        public void Preferences_InterfaceTheme_RoundTrips()
        {
            foreach (var theme in new[] { InterfaceTheme.Cream, InterfaceTheme.Wabi, InterfaceTheme.Original })
            {
                var original = new MainWindow.UserPreferencesState(InterfaceTheme: InterfaceThemes.ToStorageValue(theme));
                string json = JsonSerializer.Serialize(original);
                var restored = JsonSerializer.Deserialize<MainWindow.UserPreferencesState>(json);

                Assert.Equal(original, restored);
                Assert.Equal(theme, InterfaceThemes.Parse(restored!.InterfaceTheme));
                // 啟動時 App 只用這個函式讀偏好檔（MainWindow 還沒載入）：要和上面的存法讀出同一個結果。
                Assert.Equal(theme, InterfaceThemes.ParseFromPreferencesJson(json));
            }
        }

        [Theory]
        [InlineData(null, "Cream")]
        [InlineData("", "Cream")]
        [InlineData("Cream", "Cream")]
        [InlineData("Wabi", "Wabi")]
        [InlineData("wabi", "Wabi")]
        [InlineData(" Wabi ", "Wabi")]
        [InlineData("Original", "Original")]
        [InlineData("original", "Original")]
        [InlineData("Original2", "Cream")]   // 不認得的值當成奶油，不讓程式打不開
        [InlineData("garbage", "Cream")]
        public void Parse_KnownNames_AndUnknownFallsBackToCream(string? value, string expected)
        {
            Assert.Equal(expected, InterfaceThemes.Parse(value).ToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json")]
        [InlineData("[1,2]")]
        [InlineData("{ \"InterfaceTheme\": 5 }")]
        [InlineData("{ \"InterfaceTheme\": null }")]
        public void ParseFromPreferencesJson_BadInput_IsCream(string json)
        {
            Assert.Equal(InterfaceTheme.Cream, InterfaceThemes.ParseFromPreferencesJson(json));
        }

        [Fact]
        public void Default_IsCream_AndThemeColorsPassThrough()
        {
            // 測試程序不會呼叫 App.OnStartup，所以目前主題是預設的奶油：呼叫點的奶油原值要原樣出來。
            Assert.Equal(InterfaceTheme.Cream, InterfaceThemes.Current);
            Assert.Equal(System.Windows.Media.Color.FromRgb(0x2A, 0x20, 0x19), ThemeColors.Rgb(0x2A, 0x20, 0x19));
            Assert.Equal(System.Windows.Media.Color.FromArgb(0x80, 1, 2, 3), ThemeColors.Argb(0x80, 1, 2, 3));
            Assert.Equal("#F8F4ED", ThemeColors.Hex("#F8F4ED"));
            Assert.Equal("White", ThemeColors.Hex("White"));
            Assert.Equal(new System.Windows.CornerRadius(2), ThemeColors.DialogCornerRadius);
        }

        // ------------------------------------------------------------ C# grade() 對照 Python

        public static IEnumerable<object[]> GradeRows()
        {
            foreach (var line in File.ReadLines(RepoFile("tests", "cat5201.Tests", "TestData", "wabi_grade_table.csv")))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                yield return new object[] { p[0], p[1], p[2], p[3], p[4], p[5] };
            }
        }

        [Fact]
        public void GradeWabi_MatchesPythonGrade_ForEveryRowInTable()
        {
            int rows = 0;
            var wrong = new List<string>();
            foreach (var row in GradeRows())
            {
                rows++;
                byte r = (byte)(int)row[0], g = (byte)(int)row[1], b = (byte)(int)row[2];
                var got = ThemeColors.GradeWabi(r, g, b);
                var want = ((byte)(int)row[3], (byte)(int)row[4], (byte)(int)row[5]);
                if (got != want)
                    wrong.Add($"({r},{g},{b}) -> C# {got} / Python {want}");
            }
            Assert.True(rows >= 4000, "對照表列數太少：" + rows);   // 表裡有整個 RGB 立方體的取樣、專案實際用到的每個顏色與固定亂數
            Assert.True(wrong.Count == 0, $"{wrong.Count}/{rows} 色不同，前幾個：" + string.Join("; ", wrong.Take(8)));
        }

        [Fact]
        public void GradeHex_KeepsAlphaAndCase()
        {
            // 白色 → 紙色；alpha 原樣保留；沒有大寫字母的原字串輸出小寫（和 Python 版一致）。
            var (r, g, b) = ThemeColors.GradeWabi(255, 255, 255);
            string rgb = $"{r:X2}{g:X2}{b:X2}";
            Assert.Equal("#" + rgb, ThemeColors.GradeHex("#FFFFFF"));
            Assert.Equal("#80" + rgb, ThemeColors.GradeHex("#80FFFFFF"));
            Assert.Equal("#ffffff".Length, ThemeColors.GradeHex("#ffffff").Length);
            Assert.Equal("#" + rgb.ToLowerInvariant(), ThemeColors.GradeHex("#ffffff"));
            // 不是色碼的字串原樣回傳
            Assert.Equal("White", ThemeColors.GradeHex("White"));
            Assert.Equal("", ThemeColors.GradeHex(""));
            Assert.Equal("#12345", ThemeColors.GradeHex("#12345"));
            Assert.Equal("#GGGGGG", ThemeColors.GradeHex("#GGGGGG"));
        }

        // ------------------------------------------------------------ 主題字典

        private static readonly XNamespace Px = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xx = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>讀 Themes\名稱.xaml 裡的 Br_ / Cl_ 資源：鍵 → 色碼字串（大寫）。</summary>
        private static Dictionary<string, string> LoadPalette(string theme)
        {
            var doc = XDocument.Load(RepoFile("Themes", theme + ".xaml"));
            var map = new Dictionary<string, string>();
            foreach (var e in doc.Root!.Elements())
            {
                string? key = (string?)e.Attribute(Xx + "Key");
                if (key == null || !(key.StartsWith("Br_") || key.StartsWith("Cl_"))) continue;
                string value = e.Name == Px + "Color" ? e.Value.Trim() : (string?)e.Attribute("Color") ?? "";
                map[key] = value;
            }
            return map;
        }

        [Fact]
        public void ThemeDictionaries_SameKeys_CreamIsHead_WabiIsGradeOfCream()
        {
            var cream = LoadPalette("Cream");
            var wabi = LoadPalette("Wabi");

            Assert.True(cream.Count > 100, "色票數量不對：" + cream.Count);
            Assert.Equal(cream.Keys.OrderBy(k => k), wabi.Keys.OrderBy(k => k));

            var hex = new Regex("^(?:Br|Cl)_([0-9A-F]{6}|[0-9A-F]{8})(?:__\\w+)?$");
            var wrong = new List<string>();
            foreach (var (key, creamValue) in cream)
            {
                if (key == "Br_PaperWhite")
                {
                    // 形狀差異：奶油的 Background="White" → 侘寂的紙色（tools\theme\wabi_shape.py）。
                    Assert.Equal("White", creamValue);
                    Assert.Equal("#F4EFE6", wabi[key]);
                    continue;
                }
                if (key.StartsWith("Br_Lit_"))
                {
                    // 奶油版本來就是字面值（Transparent 等），侘寂不改它。
                    Assert.Equal(creamValue, wabi[key]);
                    continue;
                }

                var m = hex.Match(key);
                Assert.True(m.Success, "鍵名不是色碼：" + key);
                string digits = m.Groups[1].Value;
                if (key.Contains("__"))
                {
                    string baseKey = key.Substring(0, key.IndexOf("__", StringComparison.Ordinal));
                    if (!string.Equals(creamValue, cream[baseKey], StringComparison.OrdinalIgnoreCase))
                        wrong.Add($"{key}: 變體鍵的奶油值 {creamValue} 與基本鍵 {baseKey} 的 {cream[baseKey]} 不同");
                    if (!string.Equals(wabi[key], wabi[baseKey], StringComparison.OrdinalIgnoreCase))
                        wrong.Add($"{key}: 變體鍵的侘寂值與基本鍵不同");
                    continue;
                }

                // 奶油：值就是鍵名的色碼（鍵名取自 git HEAD 的字面值）
                if (!string.Equals(creamValue, "#" + digits, StringComparison.OrdinalIgnoreCase))
                    wrong.Add($"{key}: 奶油值 {creamValue}");

                // 侘寂：值＝grade(奶油值)
                string expected = ThemeColors.GradeHex("#" + digits);
                if (!string.Equals(wabi[key], expected, StringComparison.OrdinalIgnoreCase))
                    wrong.Add($"{key}: 侘寂值 {wabi[key]}，應為 {expected}");
            }
            Assert.True(wrong.Count == 0, string.Join("; ", wrong.Take(8)));
        }

        [Fact]
        public void ThemeDictionaries_PaletteGradeAlsoMatchesPythonTable()
        {
            // 把字典裡每個奶油→侘寂的色碼組，再對一次 Python 對照表（表裡有專案用到的每個顏色）。
            var table = new Dictionary<(int, int, int), (int, int, int)>();
            foreach (var row in GradeRows())
                table[((int)row[0], (int)row[1], (int)row[2])] = ((int)row[3], (int)row[4], (int)row[5]);

            var cream = LoadPalette("Cream");
            var wabi = LoadPalette("Wabi");
            int checkedCount = 0;
            foreach (var (key, creamValue) in cream)
            {
                if (key == "Br_PaperWhite" || key.Contains("__") || key.StartsWith("Br_Lit_")) continue;
                string d = creamValue.TrimStart('#');
                string rgb = d.Length == 8 ? d.Substring(2) : d;
                var c = (Convert.ToInt32(rgb.Substring(0, 2), 16), Convert.ToInt32(rgb.Substring(2, 2), 16), Convert.ToInt32(rgb.Substring(4, 2), 16));
                Assert.True(table.TryGetValue(c, out var want), $"對照表缺 {creamValue}（{key}）");

                string w = wabi[key].TrimStart('#');
                string wrgb = w.Length == 8 ? w.Substring(2) : w;
                var got = (Convert.ToInt32(wrgb.Substring(0, 2), 16), Convert.ToInt32(wrgb.Substring(2, 2), 16), Convert.ToInt32(wrgb.Substring(4, 2), 16));
                Assert.Equal(want, got);
                if (d.Length == 8)
                    Assert.Equal(d.Substring(0, 2), w.Substring(0, 2), ignoreCase: true);   // alpha 不變
                checkedCount++;
            }
            Assert.True(checkedCount > 100);
        }

        [Fact]
        public void ThemeDictionaries_DeclareTheirName_AndSharedShapeKeys()
        {
            foreach (var theme in new[] { "Cream", "Wabi" })
            {
                var doc = XDocument.Load(RepoFile("Themes", theme + ".xaml"));
                var keys = doc.Root!.Elements().Select(e => (string?)e.Attribute(Xx + "Key")).Where(k => k != null).ToHashSet();
                Assert.Contains("ThemeName", keys);
                Assert.Contains("NodeBorderStyle", keys);
                Assert.Contains("DecisionPanelCornerRadius", keys);
                var name = doc.Root.Elements().First(e => (string?)e.Attribute(Xx + "Key") == "ThemeName").Value;
                Assert.Equal(theme, name);
            }
        }

        private static HashSet<string> AllKeys(string theme)
        {
            var doc = XDocument.Load(RepoFile("Themes", theme + ".xaml"));
            return doc.Root!.Elements().Select(e => (string?)e.Attribute(Xx + "Key")).Where(k => k != null).Select(k => k!).ToHashSet();
        }

        [Fact]
        public void EveryStaticResourceKey_ExistsInAllThreeThemes()
        {
            // MainWindow.xaml／NodeControl.xaml 用到、又不是自己檔案裡定義的資源鍵，三套主題字典都要有；缺鍵會讓 XAML 載入時丟例外。
            var used = new HashSet<string>();
            var local = new HashSet<string>();
            foreach (var file in new[] { "MainWindow.xaml", "NodeControl.xaml" })
            {
                string text = File.ReadAllText(RepoFile(file));
                foreach (Match m in Regex.Matches(text, @"\{StaticResource (\w+)\}")) used.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(text, @"x:Key=""(\w+)""")) local.Add(m.Groups[1].Value);
            }
            used.ExceptWith(local);
            Assert.True(used.Count > 150, "用到的主題鍵數量不對：" + used.Count);

            foreach (var theme in new[] { "Cream", "Wabi", "Original" })
            {
                var keys = AllKeys(theme);
                var missing = used.Where(k => !keys.Contains(k)).ToList();
                Assert.True(missing.Count == 0, $"{theme} 缺少：" + string.Join(", ", missing));
            }
        }

        [Theory]
        [InlineData("Cream")]
        [InlineData("Wabi")]
        [InlineData("Original")]
        public void EveryUsedKey_ResolvesToARealValue_WhenTheDictionaryIsLoaded(string theme)
        {
            // StaticResource 取到 null（x:Null）或 UnsetValue 會被當成「找不到」而丟例外，所以每個用到的鍵都必須有實際的值。
            var used = new HashSet<string>();
            var local = new HashSet<string>();
            foreach (var file in new[] { "MainWindow.xaml", "NodeControl.xaml" })
            {
                string text = File.ReadAllText(RepoFile(file));
                foreach (Match m in Regex.Matches(text, @"\{StaticResource (\w+)\}")) used.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(text, @"x:Key=""(\w+)""")) local.Add(m.Groups[1].Value);
            }
            used.ExceptWith(local);

            var bad = OnSta(() =>
            {
                var dict = new ResourceDictionary { Source = new Uri("/cat5201;component/Themes/" + theme + ".xaml", UriKind.Relative) };
                return used.Where(k => !dict.Contains(k) || dict[k] == null || dict[k] == DependencyProperty.UnsetValue).ToList();
            });
            Assert.True(bad.Count == 0, theme + " 的值是空的：" + string.Join(", ", bad));
        }

        [Fact]
        public void Original_HasAllPaletteAndShapeKeysOfTheOtherThemes()
        {
            // 奶油的鍵（除了奶油獨有的全域樣式：單選、勾選、提示框、按鈕、輸入框、滑桿用到的 SliderLineButton／SliderRoundThumb）原始都要有。
            var cream = AllKeys("Cream");
            var original = AllKeys("Original");
            var onlyCream = cream.Except(original).OrderBy(k => k).ToList();
            Assert.Equal(new[] { "SliderLineButton", "SliderRoundThumb" }, onlyCream);
            Assert.Empty(original.Except(cream));
            Assert.Equal(cream.OrderBy(k => k), AllKeys("Wabi").OrderBy(k => k));
        }

        [Fact]
        public void Original_DictionaryHasNoGlobalImplicitStylesExceptScrollBar()
        {
            // b4a30e3 的 App.xaml 只有捲軸樣式：原始主題不能有全域的按鈕、輸入框、單選、勾選、提示框、滑桿隱含樣式。
            var doc = XDocument.Load(RepoFile("Themes", "Original.xaml"));
            var implicitTargets = doc.Root!.Elements(Px + "Style")
                .Where(e => e.Attribute(Xx + "Key") == null)
                .Select(e => (string?)e.Attribute("TargetType")).ToList();
            Assert.Equal(new[] { "{x:Type ScrollBar}" }, implicitTargets);
        }

        [Fact]
        public void Original_ValuesAreFromB4a30e3_SpotChecks()
        {
            // 取自 git show b4a30e3:MainWindow.xaml／NodeControl.xaml 的幾個位置。
            var cream = LoadPalette("Cream");
            var orig = LoadPalette("Original");
            Assert.Equal("#FFFFFF", orig["Cl_FFFDF9"].ToUpperInvariant());      // FileMenuBg 的顏色
            Assert.Equal("#D6D6D6", orig["Cl_D9CFBF"].ToUpperInvariant());
            Assert.Equal("#222222", orig["Cl_26231E"].ToUpperInvariant());
            Assert.NotEqual(cream["Br_2A2019"], orig["Br_2A2019"]);
        }

        private static T OnSta<T>(Func<T> body)
        {
            T result = default!;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { result = body(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 執行緒逾時");
            if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
            return result;
        }

        [Theory]
        [InlineData("Cream")]
        [InlineData("Wabi")]
        [InlineData("Original")]
        public void NodeControl_LoadsUnderEveryTheme_WithTheThemesNodeCard(string themeName)
        {
            var theme = InterfaceThemes.Parse(themeName);
            // 在沒有 Application 的測試環境，NodeControl 會把目前主題的字典放進自己的資源（EnsureResourcesWithoutApplication）。
            // 能建出來＝NodeControl.xaml 每個 StaticResource 都找得到、型別也對。
            try
            {
                InterfaceThemes.SetCurrentForTests(theme);
                var (radius, thickness, hasShadow) = OnSta(() =>
                {
                    var node = new NodeControl();
                    return (node.NodeBorder.CornerRadius, node.NodeBorder.BorderThickness, node.NodeBorder.Effect != null);
                });
                switch (theme)
                {
                    case InterfaceTheme.Cream:
                        Assert.Equal(new CornerRadius(3), radius); Assert.Equal(new Thickness(1.5), thickness); Assert.False(hasShadow); break;
                    case InterfaceTheme.Wabi:
                        Assert.Equal(new CornerRadius(9, 3, 10, 4), radius); Assert.Equal(new Thickness(1.2), thickness); Assert.True(hasShadow); break;
                    default:
                        Assert.Equal(new CornerRadius(8), radius); Assert.Equal(new Thickness(3), thickness); Assert.False(hasShadow); break;
                }
            }
            finally
            {
                InterfaceThemes.SetCurrentForTests(InterfaceTheme.Cream);
            }
        }

        [Fact]
        public void ThemeColors_OriginalBranch_UsesTheOriginalValues_OtherThemesIgnoreThem()
        {
            try
            {
                InterfaceThemes.SetCurrentForTests(InterfaceTheme.Original);
                Assert.True(ThemeColors.IsOriginal);
                Assert.Equal(Color.FromRgb(0x00, 0x00, 0x00), ThemeColors.Rgb(0x2A, 0x20, 0x19, 0x000000));
                Assert.Equal(Color.FromRgb(0x22, 0x22, 0x22), ThemeColors.Rgb(0x2A, 0x20, 0x19, 0x222222));   // 同一個奶油色，不同位置，不同的原始色
                Assert.Equal("#F2F2F2", ThemeColors.Hex("#F1EEE9", "#F2F2F2"));
                Assert.Equal("White", ThemeColors.Hex("#FFFDF9", "White"));
                Assert.Equal(10, ThemeColors.Pick(3, 10));
                Assert.Equal(new CornerRadius(12), ThemeColors.DialogCornerRadius);
                // 沒帶原始值的呼叫：原始主題回傳奶油值（不做侘寂調色）
                Assert.Equal(Color.FromRgb(0x2A, 0x20, 0x19), ThemeColors.Rgb(0x2A, 0x20, 0x19));
                Assert.Equal("#F8F4ED", ThemeColors.Hex("#F8F4ED"));

                InterfaceThemes.SetCurrentForTests(InterfaceTheme.Wabi);
                Assert.False(ThemeColors.IsOriginal);
                var g = ThemeColors.GradeWabi(0x2A, 0x20, 0x19);
                Assert.Equal(Color.FromRgb(g.R, g.G, g.B), ThemeColors.Rgb(0x2A, 0x20, 0x19, 0x000000));   // 侘寂：從奶油值算，不看原始值
                Assert.Equal(ThemeColors.GradeHex("#F1EEE9"), ThemeColors.Hex("#F1EEE9", "#F2F2F2"));
                Assert.Equal(3, ThemeColors.Pick(3, 10));

                InterfaceThemes.SetCurrentForTests(InterfaceTheme.Cream);
                Assert.Equal(Color.FromRgb(0x2A, 0x20, 0x19), ThemeColors.Rgb(0x2A, 0x20, 0x19, 0x000000));
                Assert.Equal("#F1EEE9", ThemeColors.Hex("#F1EEE9", "#F2F2F2"));
                Assert.Equal(3, ThemeColors.Pick(3, 10));
                Assert.Equal(new CornerRadius(2), ThemeColors.DialogCornerRadius);
            }
            finally
            {
                InterfaceThemes.SetCurrentForTests(InterfaceTheme.Cream);
            }
        }

        [Fact]
        public void XamlFiles_HaveNoHardcodedHexColors()
        {
            // 顏色一律走主題資源。直接寫 #RRGGBB 的話，侘寂主題下就不會換色。
            // （Themes\ 字典本身例外，它們就是定義顏色的地方。）
            var hex = new Regex(@"=""#[0-9A-Fa-f]{6,8}""");
            foreach (var file in new[] { "App.xaml", "MainWindow.xaml", "NodeControl.xaml" })
                Assert.False(hex.IsMatch(File.ReadAllText(RepoFile(file))), file + " 還有寫死的色碼");
        }

        // ------------------------------------------------------------ 找專案檔案

        private static string RepoFile(params string[] parts)
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "cat5201.csproj")))
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            Assert.True(dir != null, "找不到專案根目錄（cat5201.csproj）");
            return Path.Combine(new[] { dir! }.Concat(parts).ToArray());
        }
    }
}
