using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Cat5201
{
    /// <summary>介面風格。存在偏好檔的 InterfaceTheme 欄位，值就是這裡的名稱。</summary>
    internal enum InterfaceTheme
    {
        /// <summary>奶油（預設）：2026-11 改版的樣子。</summary>
        Cream,
        /// <summary>侘寂・陶器：色票換成偏灰的陶土色，節點卡與彈窗圓角不對稱、節點有柔和陰影。</summary>
        Wabi,
        /// <summary>原始：git commit b4a30e3（6.11.26，1.9.4）時的介面，連彈窗與標題列都還原。</summary>
        Original,
    }

    /// <summary>
    /// 介面風格的載入與選擇。
    ///
    /// 時機：主題字典必須在 MainWindow 的 InitializeComponent 之前合併進 Application.Resources
    /// （XAML 用 StaticResource，找不到鍵會直接丟例外），所以由 <c>App.OnStartup</c> 最前面自己讀偏好檔，
    /// 不等 MainWindow 載入偏好。換風格因此要重新啟動才生效。
    ///
    /// 每套主題是一個完整的 ResourceDictionary（Themes\名稱.xaml）：色票、形狀資源、全域樣式都在裡面。
    /// 之後要加「原始」這種版面不同的主題，新增一個列舉值、一個字典檔、在 <see cref="Parse"/> 與
    /// <see cref="DictionaryName"/> 各加一行即可。
    /// </summary>
    internal static class InterfaceThemes
    {
        /// <summary>這次執行實際套用的風格（啟動後不再改變）。</summary>
        internal static InterfaceTheme Current { get; private set; } = InterfaceTheme.Cream;

        /// <summary>只給測試用：直接指定目前風格（不載入字典）。測試要還原成奶油，並且不能平行執行會讀 Current 的其他測試。</summary>
        internal static void SetCurrentForTests(InterfaceTheme theme) => Current = theme;

        internal const string PreferenceKey = "InterfaceTheme";

        /// <summary>存進偏好檔的字串。</summary>
        internal static string ToStorageValue(InterfaceTheme theme) => theme switch
        {
            InterfaceTheme.Wabi => "Wabi",
            InterfaceTheme.Original => "Original",
            _ => "Cream",
        };

        /// <summary>讀偏好檔的字串。空、沒有這個欄位、不認得的值一律當成奶油。</summary>
        internal static InterfaceTheme Parse(string? value)
        {
            string? v = value?.Trim();
            if (string.Equals(v, "Wabi", StringComparison.OrdinalIgnoreCase))
                return InterfaceTheme.Wabi;
            if (string.Equals(v, "Original", StringComparison.OrdinalIgnoreCase))
                return InterfaceTheme.Original;
            return InterfaceTheme.Cream;
        }

        /// <summary>主題字典在組件裡的檔名（Themes\xxx.xaml）。</summary>
        internal static string DictionaryName(InterfaceTheme theme) => theme switch
        {
            InterfaceTheme.Wabi => "Wabi",
            InterfaceTheme.Original => "Original",
            _ => "Cream",
        };

        /// <summary>
        /// 從偏好檔（JSON 文字）取出 InterfaceTheme。與 <c>MainWindow.UserPreferencesState</c> 的讀法一致，
        /// 但只看這一個欄位，所以偏好檔其他欄位壞了也不影響主題。
        /// </summary>
        internal static InterfaceTheme ParseFromPreferencesJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return InterfaceTheme.Cream;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(PreferenceKey, out var v)
                    && v.ValueKind == JsonValueKind.String)
                    return Parse(v.GetString());
            }
            catch (JsonException) { }
            return InterfaceTheme.Cream;
        }

        /// <summary>啟動時用：讀資料根目錄的偏好檔。讀不到就是奶油。</summary>
        internal static InterfaceTheme ReadSavedTheme()
        {
            try
            {
                string path = Path.Combine(DataRoot.Resolve(), "_config", "_preferences.json");
                if (File.Exists(path))
                    return ParseFromPreferencesJson(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                AppLog.Warn("Theme", "讀取介面風格偏好失敗，使用奶油", ex);
            }
            return InterfaceTheme.Cream;
        }

        /// <summary>App.OnStartup 最前面呼叫：決定風格並把字典合併進 Application.Resources。</summary>
        internal static void ApplyFromPreferences(Application app)
        {
            Apply(app, ReadSavedTheme());
        }

        internal static void Apply(Application app, InterfaceTheme theme)
        {
            try
            {
                Merge(app, theme);
                Current = theme;
            }
            catch (Exception ex) when (theme != InterfaceTheme.Cream)
            {
                // 字典載入失敗不能讓程式打不開：退回奶油。
                AppLog.Error("Theme", "載入介面風格失敗，改用奶油：" + theme, ex);
                Merge(app, InterfaceTheme.Cream);
                Current = InterfaceTheme.Cream;
            }
        }

        /// <summary>
        /// 沒有 Application 的環境（單元測試、設計工具）建立控制項時，把目前主題的字典放進該控制項自己的資源，
        /// 讓它的 StaticResource 找得到鍵。正常執行時（有 Application）什麼都不做。
        /// </summary>
        internal static void EnsureResourcesWithoutApplication(FrameworkElement element)
        {
            if (Application.Current != null)
                return;
            element.Resources.MergedDictionaries.Add(CreateDictionary(Current));
        }

        private static ResourceDictionary CreateDictionary(InterfaceTheme theme)
        {
            string asm = typeof(InterfaceThemes).Assembly.GetName().Name ?? "cat5201";
            return new ResourceDictionary
            {
                Source = new Uri($"/{asm};component/Themes/{DictionaryName(theme)}.xaml", UriKind.Relative)
            };
        }

        private static void Merge(Application app, InterfaceTheme theme)
        {
            app.Resources.MergedDictionaries.Add(CreateDictionary(theme));
        }
    }
}
