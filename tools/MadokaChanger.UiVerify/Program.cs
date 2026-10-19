using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MadokaChanger.App;
using MadokaChanger.App.ViewModels;
using MadokaChanger.Core;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length<2){Console.WriteLine("MadokaChanger.UiVerify <test-data-root> <screenshots-folder>");return 1;}
        int exit=0;var app=new App();app.InitializeComponent();App.IsHeadlessVerification=true;
        #pragma warning disable WPF0001 // Opt into the WPF theme API also selected explicitly in App.xaml.
        if(app.ThemeMode!=ThemeMode.Dark)throw new Exception("The application must force dark mode.");
        #pragma warning restore WPF0001
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var bindings=new BindingErrors();PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        _=Dispatcher.CurrentDispatcher.InvokeAsync(async()=>
        {
            try
            {
                using var services=App.CreateServices(Path.GetFullPath(args[0]));
                var vm=services.GetRequiredService<MainViewModel>();await vm.InitializeAsync();
                if(!vm.BackendReady || !vm.DependencyStatus.Contains("no setup needed"))throw new Exception("The complete built-in backend must be ready without configuring a folder.");
                if(vm.IsEmpty)throw new Exception("No champion metadata loaded: "+vm.Message);
                if(vm.CustomChampions.Any(c=>CatalogPolicy.IsClassicChampion(c.Model.Alias)))throw new Exception("Classic entries leaked into the champion list.");
                var window=services.GetRequiredService<MainWindow>();
                if(window.Title!="MadokaChanger")throw new Exception("Application title was not renamed.");
                var content=(FrameworkElement)window.Content;var folder=Path.GetFullPath(args[1]);Directory.CreateDirectory(folder);
                FrameworkElement? FindControl(DependencyObject node,string id)
                {
                    if(node is FrameworkElement element&&System.Windows.Automation.AutomationProperties.GetAutomationId(element)==id)return element;
                    for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)if(FindControl(VisualTreeHelper.GetChild(node,i),id) is { } match)return match;
                    return null;
                }
                void CheckVisible(string id)
                {
                    var element=FindControl(content,id)??throw new Exception("Missing control: "+id);
                    var bounds=element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                    if(bounds.Width<30||bounds.Height<25||bounds.Left<-.5||bounds.Top<-.5||bounds.Right>content.ActualWidth+.5||bounds.Bottom>content.ActualHeight+.5)
                        throw new Exception($"{id} is clipped at {content.ActualWidth}x{content.ActualHeight}: {bounds}");
                }
                IEnumerable<T> Descendants<T>(DependencyObject node) where T:DependencyObject
                {
                    for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
                    {
                        var child=VisualTreeHelper.GetChild(node,i);if(child is T value)yield return value;
                        foreach(var descendant in Descendants<T>(child))yield return descendant;
                    }
                }
                async Task Render(string name,double width=1360,double height=870,double scale=1)
                {
                    vm.SetViewport(width,height);content.Width=width;content.Height=height;content.Measure(new(width,height));content.Arrange(new(0,0,width,height));content.UpdateLayout();
                    await Task.Delay(2200);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);content.UpdateLayout();
                    CheckVisible("StartPatcher");CheckVisible("StopPatcher");
                    if(vm.IsDetail)
                    {
                        CheckVisible("ApplySelection");CheckVisible("ResetSelection");CheckVisible("SkinPreview");CheckVisible("SkinCarousel");
                        if(vm.HasChromas)
                        {
                            CheckVisible("ChromaSelector");var picker=FindControl(content,"ChromaSelector")!;
                            if(!Descendants<System.Windows.Controls.Border>(picker).Any(b=>b.Width==22&&b.Background is LinearGradientBrush))throw new Exception("Chroma color swatches must remain visible.");
                        }
                        if(vm.HasForms)CheckVisible("FormSelector");
                        var carousel=FindControl(content,"SkinCarousel")!;
                        if(Descendants<System.Windows.Controls.ListBoxItem>(carousel).Any(i=>i.ActualWidth>180))throw new Exception("Skin thumbnails expanded beyond their compact strip.");
                    }
                    var bitmap=new RenderTargetBitmap((int)(width*scale),(int)(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(content);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(folder,name+".png"));encoder.Save(stream);Console.WriteLine("Rendered "+name);
                }
                await Render("01-champions");
                vm.Search="aHrI";if(vm.ChampionRows.Sum(r=>r.Champions.Count)!=1)throw new Exception("Champion search must return exactly one Ahri, without Classic duplicates");
                var card=vm.ChampionRows.SelectMany(r=>r.Champions).Single(c=>c.Model.Id==103);vm.OpenChampionCommand.Execute(card);
                vm.SelectedSkin=vm.Skins.Single(s=>s.Model.Id==103004);await Render("02-skin-selection",1360,1080);
                vm.SelectedChroma=vm.Chromas.FirstOrDefault(c=>c.Model is not null);await Render("03-chroma-selection",1120,800);
                await Render("07-windowed-detail",960,640,1.25);
                await Render("08-small-detail",720,500,1.5);
                await Render("09-laptop-detail",854,480,1.5);
                vm.BackCommand.Execute(null);if(vm.Search!="aHrI")throw new Exception("Browser search was lost on return");
                vm.Page="Custom Skins";vm.CustomChampion=card;
                var sample=services.GetRequiredService<LibraryStore>().Manifest?.Packages.FirstOrDefault(p=>p.Selection==new Selection(103,103001));
                CustomSkin? imported=null;
                if(sample is not null)
                {
                    var path=services.GetRequiredService<LibraryStore>().GetPackagePath(sample);
                    await vm.ImportFilesAsync([path]);imported=services.GetRequiredService<StateStore>().State.CustomSkins.LastOrDefault();
                    vm.SelectedCustom=vm.CustomSkins.LastOrDefault();
                }
                await Render("04-custom-skins");
                await Render("10-windowed-custom",900,620,1.25);
                if(imported is not null)await services.GetRequiredService<CustomSkinService>().UninstallAsync(imported,CancellationToken.None);
                vm.Page="Settings";await Render("05-settings",1360,1000);
                await Render("11-windowed-settings",900,620,1.25);
                FindControl(content,"SaveSettings")!.BringIntoView();
                await Render("13-windowed-settings-bottom",900,620,1.25);CheckVisible("SaveSettings");
                bool HasBackendPicker(DependencyObject element)
                {
                    if(element is System.Windows.Controls.TextBox && System.Windows.Automation.AutomationProperties.GetName(element).Contains("backend",StringComparison.OrdinalIgnoreCase))return true;
                    for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)if(HasBackendPicker(VisualTreeHelper.GetChild(element,i)))return true;
                    return false;
                }
                if(HasBackendPicker(content))throw new Exception("A backend folder picker is still visible.");
                vm.Page="Skins";vm.Search="";await Render("06-minimum-size",720,500,1.5);
                await Render("12-wide-browser",1600,900);
                if(bindings.Errors.Count>0)throw new Exception("WPF binding errors:\n"+string.Join('\n',bindings.Errors));
                Console.WriteLine("PASS off-screen WPF layouts, search, skin/chroma selection, browser state and binding checks. No window was shown.");
            }
            catch(Exception e){Console.Error.WriteLine(e);exit=1;}
            finally {Environment.Exit(exit);}
        });
        Dispatcher.Run();Environment.Exit(exit);return exit;
    }
    private sealed class BindingErrors:TraceListener
    {
        public List<string> Errors {get;}=[];
        public override void Write(string? message){if(message is not null)Errors.Add(message);}
        public override void WriteLine(string? message)=>Write(message);
    }
}
