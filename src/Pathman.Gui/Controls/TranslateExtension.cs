using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using Pathman.Core.Localization;

namespace Pathman.Gui.Controls;

[MarkupExtensionReturnType(typeof(string))]
public sealed class TranslateExtension(string key) : MarkupExtension
{
    private sealed class Translator : IValueConverter
    {
        public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>Text.Get((string)parameter);
        public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
    }
    public override object ProvideValue(IServiceProvider serviceProvider)=>new Binding(nameof(Text.CultureName))
        {Source=Text.Current,Converter=new Translator(),ConverterParameter=key,Mode=BindingMode.OneWay}.ProvideValue(serviceProvider);
}
