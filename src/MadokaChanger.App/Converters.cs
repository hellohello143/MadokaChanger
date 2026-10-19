using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MadokaChanger.App;
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is true?Visibility.Visible:Visibility.Collapsed;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
public sealed class SwatchConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
    {
        var colors=(value as IEnumerable<string>)?.ToList()??["#AC96E8"];
        var brush=new LinearGradientBrush {StartPoint=new(0,0),EndPoint=new(1,1)};
        for(int i=0;i<colors.Count;i++)
        {
            Color color;try{color=(Color)ColorConverter.ConvertFromString(colors[i]);}catch(FormatException){color=Color.FromRgb(172,150,232);}
            brush.GradientStops.Add(new(color,(double)i/colors.Count));brush.GradientStops.Add(new(color,(double)(i+1)/colors.Count));
        }
        brush.Freeze();return brush;
    }
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
