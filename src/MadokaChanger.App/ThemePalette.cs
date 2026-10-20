using System.Windows;

namespace MadokaChanger.App;

public static class ThemePalette
{
    public static void Apply(bool homura)
    {
        var name=homura?"Homura":"Madoka";
        var dictionaries=Application.Current.Resources.MergedDictionaries;
        for(var i=0;i<dictionaries.Count;i++)
        {
            if(!dictionaries[i].Contains("AccentBrush"))continue;
            if(dictionaries[i].Source?.OriginalString.EndsWith($"Palettes/{name}.xaml",StringComparison.Ordinal)==true)return;
            // Only replace colors. Existing styles/templates and the user's current page remain in place.
            dictionaries[i]=new ResourceDictionary{Source=new Uri($"/MadokaChanger;component/Themes/Palettes/{name}.xaml",UriKind.Relative)};
            return;
        }
        throw new InvalidOperationException("The application palette is missing.");
    }
}
