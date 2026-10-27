using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Pathman.Core.Localization;

public sealed class Text : INotifyPropertyChanged
{
    private static readonly ResourceManager resources=new("Pathman.Core.Localization.Strings",typeof(Text).Assembly) {IgnoreCase=true};
    private static readonly string systemLanguage=CultureInfo.CurrentUICulture.Name;
    private static volatile CultureInfo selectedCulture=CultureInfo.GetCultureInfo(ResolveLanguage("system"));
    public static Text Current {get;}=new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public string CultureName=>selectedCulture.Name;
    public static string ResolveLanguage(string? language,string? systemLanguage=null)
    {
        var requested=string.IsNullOrWhiteSpace(language) || language=="system" ? systemLanguage ?? Text.systemLanguage : language;
        return requested.Equals("zh",StringComparison.OrdinalIgnoreCase) || new[]{"zh-CN","zh-SG","zh-Hans"}.Any(code=>requested.StartsWith(code,StringComparison.OrdinalIgnoreCase)) ? "zh-CN" : "en-US";
    }
    public static bool SetLanguage(string language)
    {
        var culture=CultureInfo.GetCultureInfo(ResolveLanguage(language));var changed=Current.CultureName!=culture.Name;
        selectedCulture=culture;
        CultureInfo.DefaultThreadCurrentUICulture=culture;CultureInfo.CurrentUICulture=culture;
        if(changed) Current.PropertyChanged?.Invoke(Current,new(nameof(CultureName)));
        return changed;
    }
    public static string Get(string key)=>resources.GetString(key,selectedCulture) ?? key;
    public static string Format(string key,params object?[] arguments)=>string.Format(CultureInfo.CurrentCulture,Get(key),arguments);
}
