using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using MadokaChanger.App.ViewModels;

namespace MadokaChanger.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private bool closed;
    public MainWindow(MainViewModel vm)
    {
        this.vm=vm;InitializeComponent();DataContext=vm;
        var area=SystemParameters.WorkArea;
        MinWidth=Math.Min(720,area.Width);MinHeight=Math.Min(500,area.Height);
        Width=Math.Min(1180,Math.Max(MinWidth,area.Width*.9));Height=Math.Min(820,Math.Max(MinHeight,area.Height*.88));
        vm.SetViewport(Width,Height);
        SourceInitialized+=(_,_)=>{var enabled=1;DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,20,ref enabled,4);};
    }
    private async void WindowLoaded(object sender,RoutedEventArgs e)=>await vm.InitializeAsync();
    private void ViewportSizeChanged(object sender,SizeChangedEventArgs e)=>vm.SetViewport(e.NewSize.Width,e.NewSize.Height);
    private void ContentSizeChanged(object sender,SizeChangedEventArgs e)=>vm.SetContentViewport(e.NewSize.Width,e.NewSize.Height);
    private void SkinSelectionChanged(object sender,SelectionChangedEventArgs e) {if(SkinCarousel.SelectedItem is { } item)SkinCarousel.ScrollIntoView(item);}
    private async void ImportClick(object sender,RoutedEventArgs e)=>await ShowImportAsync();
    private async Task ShowImportAsync()
    {
        var dialog=new OpenFileDialog{Title="Import custom skin",Filter="Skin packages|*.modpkg;*.fantome;*.zip",Multiselect=true};
        if(dialog.ShowDialog(this)==true)await vm.ImportFilesAsync(dialog.FileNames);
    }
    private void BrowseGame(object sender,RoutedEventArgs e)=>ChooseFolder("League Game folder",p=>vm.GameFolder=p);
    private void BrowseLibrary(object sender,RoutedEventArgs e)=>ChooseFolder("Skin library location",p=>vm.LibraryFolder=p);
    private void ChooseFolder(string title,Action<string> selected) {var dialog=new OpenFolderDialog{Title=title};if(dialog.ShowDialog(this)==true)selected(dialog.FolderName);}
    private void WindowDragOver(object sender,DragEventArgs e) {e.Effects=vm.CanImport&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private async void WindowDrop(object sender,DragEventArgs e) {if(e.Data.GetData(DataFormats.FileDrop) is string[] files){vm.Page="Custom Skins";await vm.ImportFilesAsync(files.Where(File.Exists));}}
    private async void WindowClosing(object? sender,CancelEventArgs e)
    {
        if(App.IsHeadlessVerification){closed=true;return;}
        if(closed)return;e.Cancel=true;IsEnabled=false;
        try{await vm.ShutdownAsync();}finally{closed=true;Close();}
    }
    protected override async void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if(e.Key==System.Windows.Input.Key.I&&System.Windows.Input.Keyboard.Modifiers==System.Windows.Input.ModifierKeys.Control){if(vm.CanImport)await ShowImportAsync();e.Handled=true;}
        base.OnPreviewKeyDown(e);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h,int attribute,ref int value,int size);
}
