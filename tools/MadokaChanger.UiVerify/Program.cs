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
using CommunityToolkit.Mvvm.Input;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length<2){Console.WriteLine("MadokaChanger.UiVerify <test-data-root> <screenshots-folder>");return 1;}
        int exit=0;var app=new App{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.InitializeComponent();App.IsHeadlessVerification=true;
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
                await services.GetRequiredService<StateStore>().LoadAsync();
                var library=services.GetRequiredService<LibraryStore>();await library.LoadAsync();
                var originalIdentity=library.Manifest?.Packages.OrderBy(p=>p.Path,StringComparer.Ordinal).Select(p=>new{p.Path,p.Name,p.Author,p.Version,p.Selection,p.Preview}).ToList();
                var vm=services.GetRequiredService<MainViewModel>();await vm.InitializeAsync();
                if(originalIdentity is not null)
                {
                    if(library.Manifest is null || !PackageTransformer.IsCurrent(library.Manifest))throw new Exception("Startup did not complete the versioned library storage migration: "+vm.Message);
                    if(!originalIdentity.SequenceEqual(library.Manifest.Packages.OrderBy(p=>p.Path,StringComparer.Ordinal).Select(p=>new{p.Path,p.Name,p.Author,p.Version,p.Selection,p.Preview})))
                        throw new Exception("Storage migration changed frontend package identities or selection mappings.");
                    if(library.Manifest.Packages.Any(p=>!File.Exists(library.GetPackagePath(p))))throw new Exception("A renamed package could not be resolved through its manifest.");
                    Console.WriteLine("PASS startup storage migration, original display identities, selection mappings and renamed package resolution");
                }
                if(!vm.BackendReady || !vm.DependencyStatus.Contains("Built-in backend: ready"))throw new Exception("The complete built-in backend must be ready without configuring a folder.");
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
                void CheckVisible(string id,double minimumHeight=25,double minimumWidth=30)
                {
                    var element=FindControl(content,id)??throw new Exception("Missing control: "+id);
                    var bounds=element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                    if(bounds.Width<minimumWidth||bounds.Height<minimumHeight||bounds.Left<-.5||bounds.Top<-.5||bounds.Right>content.ActualWidth+.5||bounds.Bottom>content.ActualHeight+.5)
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
                    CheckVisible("ToggleCharacterTheme");
                    if(vm.IsDuo)
                    {
                        CheckVisible("DuoCreate");CheckVisible("DuoJoinCode");CheckVisible("DuoConnectionStatus",12,30);
                        var joinCode=FindControl(content,"DuoJoinCode") as System.Windows.Controls.TextBox??throw new Exception("Missing party code input.");
                        if(joinCode.MaxLength!=4 || System.Windows.Automation.AutomationProperties.GetName(joinCode)!="Four digit party code")throw new Exception("Duo join code must be a four character accessible input.");
                        foreach(var id in new[]{"DuoRelayHelp","DuoLocalChampion","DuoLocalDesired","DuoLocalPrepared","DuoPeerChampion","DuoPeerDesired","DuoPeerPrepared"})
                            if(FindControl(content,id) is null)throw new Exception("Missing Duo state control: "+id);
                        if(width>=1000){CheckVisible("DuoLocalChampion",16);CheckVisible("DuoPeerChampion",16);}
                    }
                    var mascot=FindControl(content,"BrandMascot") as System.Windows.Controls.Image??throw new Exception("Missing mascot image.");
                    var expectedSize=vm.CompactLayout?40:vm.ShortLayout?96:144;
                    if(mascot.Width!=expectedSize || mascot.Height!=expectedSize || mascot.Source is null)throw new Exception("The smaller mascot did not load at its responsive size.");
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
                async Task ToggleTheme()
                {
                    var button=FindControl(content,"ToggleCharacterTheme") as System.Windows.Controls.Button??throw new Exception("The mascot must be a keyboard-accessible button.");
                    if(!button.Focusable || !button.IsTabStop || button.Command is not IAsyncRelayCommand command)throw new Exception("The mascot toggle is not accessible.");
                    var previous=vm.HomuraTheme;var selections=string.Join('|',vm.Store.State.Selections.Values.OrderBy(s=>s.ChampionId).Select(s=>s.Key));
                    var favorites=string.Join('|',vm.Store.State.Favorites.Order());var page=vm.Page;var search=vm.Search;var skin=vm.SelectedSkin;var chroma=vm.SelectedChroma;
                    await command.ExecuteAsync(null);await Dispatcher.Yield(DispatcherPriority.DataBind);content.UpdateLayout();
                    if(vm.HomuraTheme==previous)throw new Exception("Clicking the mascot did not switch characters.");
                    var expectedAccent=(Color)ColorConverter.ConvertFromString(vm.HomuraTheme?"#B8A1E8":"#F2A7CA");
                    var expectedBackground=(Color)ColorConverter.ConvertFromString(vm.HomuraTheme?"#111216":"#0D0B14");
                    var update=FindControl(content,"UpdateLibrary") as System.Windows.Controls.Button;
                    if(update?.Background is not SolidColorBrush accent || accent.Color!=expectedAccent ||
                        content is not System.Windows.Controls.Grid grid || grid.Background is not SolidColorBrush background || background.Color!=expectedBackground)
                        throw new Exception("Existing controls did not update to the selected palette.");
                    var mascot=FindControl(content,"BrandMascot") as System.Windows.Controls.Image;
                    if(mascot?.Source?.ToString()?.EndsWith(vm.HomuraTheme?"/HomuraChibi.png":"/MadokaChibi.png",StringComparison.Ordinal)!=true ||
                        System.Windows.Automation.AutomationProperties.GetName(mascot)!=vm.MascotName)throw new Exception("The character artwork or accessible name did not switch.");
                    var saved=await JsonFile.ReadAsync<UserState>(Path.Combine(args[0],"settings.json"));
                    if(saved?.Settings.HomuraTheme!=vm.HomuraTheme)throw new Exception("The chosen theme was not saved.");
                    if(page!=vm.Page || search!=vm.Search || skin!=vm.SelectedSkin || chroma!=vm.SelectedChroma ||
                        selections!=string.Join('|',vm.Store.State.Selections.Values.OrderBy(s=>s.ChampionId).Select(s=>s.Key)) || favorites!=string.Join('|',vm.Store.State.Favorites.Order()))
                        throw new Exception("Changing the theme changed collection state.");
                }
                await Render("01-champions");
                vm.Page="Duo";await Render("18-duo-madoka",1360,870);await Render("19-duo-madoka-small",720,500,1.5);
                await ToggleTheme();await Render("20-duo-homura",1360,870);await Render("21-duo-homura-small",720,500,1.5);await ToggleTheme();vm.Page="Skins";
                await ToggleTheme();await Render("14-other-theme-browser");await ToggleTheme();
                vm.Search="aHrI";if(vm.ChampionRows.Sum(r=>r.Champions.Count)!=1)throw new Exception("Champion search must return exactly one Ahri, without Classic duplicates");
                var card=vm.ChampionRows.SelectMany(r=>r.Champions).Single(c=>c.Model.Id==103);vm.OpenChampionCommand.Execute(card);
                vm.SelectedSkin=vm.Skins.Single(s=>s.Model.Id==103004);await Render("02-skin-selection",1360,1080);
                vm.SelectedChroma=vm.Chromas.FirstOrDefault(c=>c.Model is not null);await Render("03-chroma-selection",1120,800);
                await ToggleTheme();await Render("15-other-theme-detail",1120,800);await Render("16-other-theme-compact",720,500,1.5);await ToggleTheme();
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
                await ToggleTheme();await Render("17-other-theme-settings",1360,1000);
                await vm.SaveSettingsCommand.ExecuteAsync(null);
                var savedTheme=await JsonFile.ReadAsync<UserState>(Path.Combine(args[0],"settings.json"));
                if(vm.HasError || savedTheme?.Settings.HomuraTheme!=vm.HomuraTheme)throw new Exception("Saving settings lost the chosen theme: "+vm.Message);
                await ToggleTheme();
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
                Console.WriteLine("PASS smaller mascot, both live palettes, character toggle, accessible button, theme persistence and unchanged collection state");
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
