using System.Windows;

namespace Cat5201
{
    // MainWindow 部分類別 — 介面風格（個人化 → 介面風格）
    // 風格本身在啟動時由 App.OnStartup 套用（見 Themes\InterfaceThemes.cs）；這裡只管「選哪一個、存進偏好」。
    public partial class MainWindow
    {
        /// <summary>使用者選的風格（存進偏好檔，下次啟動生效）。可能和這次實際套用的 <see cref="InterfaceThemes.Current"/> 不同。</summary>
        private InterfaceTheme _selectedInterfaceTheme = InterfaceThemes.Current;
        private bool _suppressThemeRadioEvents;

        private void SyncInterfaceThemeRadios()
        {
            _suppressThemeRadioEvents = true;
            try
            {
                if (ThemeCream != null) ThemeCream.IsChecked = _selectedInterfaceTheme == InterfaceTheme.Cream;
                if (ThemeWabi != null) ThemeWabi.IsChecked = _selectedInterfaceTheme == InterfaceTheme.Wabi;
                if (ThemeOriginal != null) ThemeOriginal.IsChecked = _selectedInterfaceTheme == InterfaceTheme.Original;
                if (InterfaceThemeRestartHint != null)
                    InterfaceThemeRestartHint.Visibility = _selectedInterfaceTheme != InterfaceThemes.Current
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            finally
            {
                _suppressThemeRadioEvents = false;
            }
        }

        private void InterfaceTheme_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressThemeRadioEvents)
                return;

            _selectedInterfaceTheme = ReferenceEquals(sender, ThemeWabi) ? InterfaceTheme.Wabi
                : ReferenceEquals(sender, ThemeOriginal) ? InterfaceTheme.Original
                : InterfaceTheme.Cream;
            SyncInterfaceThemeRadios();
            SavePreferences();
            AppLog.Info("Theme", "介面風格改選為 " + _selectedInterfaceTheme + "（重新啟動後生效）");
        }
    }
}
