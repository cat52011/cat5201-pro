using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace Cat5201
{
    /// <summary>
    /// XAML 用的翻譯：<c>Text="{local:Tr '新增節點'}"</c>。
    ///
    /// XAML 裡保留中文原文（讀版面時一眼看得懂），英文從 <see cref="UiStrings"/> 對照表查。
    /// 底下是一個綁定，所以在個人化切換語言時，畫面上的字會立刻跟著換，不必重開程式。
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class TrExtension : MarkupExtension
    {
        public TrExtension() { }
        public TrExtension(string text) { Text = text; }

        [ConstructorArgument("text")]
        public string Text { get; set; } = "";

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new Binding(nameof(LocalizationSource.Revision))
            {
                Source = LocalizationSource.Instance,
                Mode = BindingMode.OneWay,
                Converter = TrConverter.Instance,
                ConverterParameter = Text,
            };
            return binding.ProvideValue(serviceProvider);
        }
    }

    /// <summary>語言一換，Revision 就變一次，所有 Tr 綁定跟著重新取值。</summary>
    public sealed class LocalizationSource : INotifyPropertyChanged
    {
        public static LocalizationSource Instance { get; } = new();

        private LocalizationSource()
        {
            UiStrings.EnsureRegistered();
            UiLanguage.Changed += () =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        }

        public string Revision => UiLanguage.Code;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    internal sealed class TrConverter : IValueConverter
    {
        public static readonly TrConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => UiLanguage.T(parameter as string ?? "");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
