using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 介面語言（個人化 → 語言）。
    /// 測試會切換全域語言，所以每個測試結束都切回中文，避免影響其他斷言中文字串的測試。
    /// </summary>
    [Collection("UiLanguage")]
    public class UiLanguageTests : IDisposable
    {
        public void Dispose() => UiLanguage.Code = UiLanguage.Chinese;

        [Fact]
        public void P_PicksByCurrentLanguage()
        {
            UiLanguage.Code = UiLanguage.Chinese;
            Assert.Equal("確定", UiLanguage.P("確定", "OK"));

            UiLanguage.Code = UiLanguage.English;
            Assert.Equal("OK", UiLanguage.P("確定", "OK"));
        }

        [Fact]
        public void T_FallsBackToOriginal_WhenNoTranslation()
        {
            UiLanguage.Code = UiLanguage.English;
            Assert.Equal("沒有登記的字", UiLanguage.T("沒有登記的字"));
        }

        [Theory]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("en", "en")]
        [InlineData("en-US", "en")]
        [InlineData("zh-TW", "zh-TW")]
        [InlineData("zh-Hant", "zh-TW")]
        public void Normalize_EmptyMeansNotChosenYet(string? stored, string expected)
        {
            Assert.Equal(expected, UiLanguage.Normalize(stored));
        }

        [Fact]
        public void OldPrefsFile_HasNoLanguage_SoStartupAsks()
        {
            // 加語言設定之前的偏好檔：沒有 InterfaceLanguage → 空字串 → 啟動時會問一次。
            var prefs = JsonSerializer.Deserialize<MainWindow.UserPreferencesState>("{ \"DailyBudgetTwd\": 50 }");
            Assert.NotNull(prefs);
            Assert.Equal("", prefs!.InterfaceLanguage);
            Assert.Equal(50, prefs.DailyBudgetTwd);
        }

        [Fact]
        public void Prefs_RoundTripLanguage()
        {
            var original = new MainWindow.UserPreferencesState(InterfaceLanguage: UiLanguage.English);
            var restored = JsonSerializer.Deserialize<MainWindow.UserPreferencesState>(JsonSerializer.Serialize(original));
            Assert.Equal(UiLanguage.English, restored!.InterfaceLanguage);
        }

        /// <summary>
        /// XAML 裡每一個 {local:Tr '…'} 都要在 UiStrings 有英文。
        /// 改了 XAML 的中文卻忘了改對照表，英文介面就會冒出中文——這個測試把它擋下來。
        /// </summary>
        [Theory]
        [InlineData("MainWindow.xaml")]
        [InlineData("NodeControl.xaml")]
        public void EveryXamlTr_HasEnglish(string xamlFile)
        {
            string path = Path.Combine(FindRepoRoot(), xamlFile);
            string xaml = File.ReadAllText(path);

            var keys = Regex.Matches(xaml, @"\{local:Tr '((?:[^'\\]|\\.)*)'\}")
                .Select(m => WebUtility.HtmlDecode(Regex.Unescape(m.Groups[1].Value)))
                .Distinct()
                .ToList();
            Assert.NotEmpty(keys);

            // 介面層的對照表在第一次用到綁定時登記；這裡直接觸發。
            _ = LocalizationSource.Instance;
            UiLanguage.Code = UiLanguage.English;

            var missing = keys.Where(k => UiLanguage.T(k) == k && Regex.IsMatch(k, @"[一-鿿]")).ToList();
            Assert.True(missing.Count == 0, "缺英文：\n" + string.Join("\n", missing));
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cat5201.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("找不到 cat5201.csproj");
        }
    }
}

namespace Cat5201.Tests
{
    /// <summary>語言是全域狀態：這組測試單獨跑，不跟其他（斷言中文字串的）測試並行。</summary>
    [CollectionDefinition("UiLanguage", DisableParallelization = true)]
    public class UiLanguageCollection { }
}
