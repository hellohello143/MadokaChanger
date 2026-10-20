using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using MadokaChanger.Core;

namespace MadokaChanger.App.Controls;
public sealed class AsyncImage : Grid
{
    private readonly Image image=new(){Stretch=Stretch.UniformToFill};
    private CancellationTokenSource? pending;
    public static readonly DependencyProperty UrlProperty=DependencyProperty.Register(nameof(Url),typeof(string),typeof(AsyncImage),new PropertyMetadata("",Changed));
    public string Url {get=>(string)GetValue(UrlProperty);set=>SetValue(UrlProperty,value);}
    public static readonly DependencyProperty ImageStretchProperty=DependencyProperty.Register(nameof(ImageStretch),typeof(Stretch),typeof(AsyncImage),new PropertyMetadata(Stretch.UniformToFill,(d,e)=>((AsyncImage)d).image.Stretch=(Stretch)e.NewValue));
    public Stretch ImageStretch {get=>(Stretch)GetValue(ImageStretchProperty);set=>SetValue(ImageStretchProperty,value);}
    public AsyncImage()
    {
        ClipToBounds=true;SetResourceReference(BackgroundProperty,"ImagePlaceholderBrush");
        var placeholder=new TextBlock{Text="◇",Opacity=.28,FontSize=32,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        placeholder.SetResourceReference(TextBlock.ForegroundProperty,"VioletBrush");Children.Add(placeholder);
        Children.Add(image);Loaded+=(_,_)=>Load();Unloaded+=(_,_)=>{pending?.Cancel();};
    }
    private static void Changed(DependencyObject d,DependencyPropertyChangedEventArgs e)=>((AsyncImage)d).Load();
    private async void Load()
    {
        if(!IsLoaded&&!App.IsHeadlessVerification)return;pending?.Cancel();pending?.Dispose();pending=new();var ct=pending.Token;image.Source=null;
        try
        {
            var file=await App.Services.GetRequiredService<ImageCache>().GetAsync(Url,ct);
            if(file is null||ct.IsCancellationRequested)return;
            var bitmap=await Task.Run(()=>
            {
                using var stream=File.OpenRead(file);var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.StreamSource=stream;b.EndInit();b.Freeze();return b;
            },ct);
            if(ct.IsCancellationRequested)return;image.Source=bitmap;
            if(!App.Services.GetRequiredService<StateStore>().State.Settings.ReducedMotion&&SystemParameters.ClientAreaAnimation)image.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(180)));
            else {image.BeginAnimation(OpacityProperty,null);image.Opacity=1;}
        }
        catch(Exception e) when(e is OperationCanceledException or IOException or NotSupportedException or ArgumentException or System.Net.Http.HttpRequestException) { }
    }
}
