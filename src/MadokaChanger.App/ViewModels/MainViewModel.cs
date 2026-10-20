using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MadokaChanger.Core;

namespace MadokaChanger.App.ViewModels;

public sealed partial class ChampionCard(Champion model,MainViewModel owner) : ObservableObject
{
    public Champion Model { get; }=model;
    public string Name=>Model.Name;
    public string Portrait=>Model.Portrait;
    public MainViewModel Owner=>owner;
    public bool IsFavorite=>owner.Store.State.Favorites.Contains(Model.Id);
    public bool HasSelection=>owner.Store.State.Selections.ContainsKey(Model.Id);
    public string FavoriteLabel=>IsFavorite?"★":"☆";
    public string SelectionLabel=>owner.Store.State.Selections.TryGetValue(Model.Id,out var selection)?owner.IsSelectionAvailable(selection)?"◆ Saved":"◆ Missing":"";
    public void Refresh() {OnPropertyChanged(nameof(IsFavorite));OnPropertyChanged(nameof(FavoriteLabel));OnPropertyChanged(nameof(HasSelection));OnPropertyChanged(nameof(SelectionLabel));}
}
public sealed record ChampionRow(List<ChampionCard> Champions);
public sealed record SkinCard(Skin Model,bool Available)
{
    public string Name=>Model.Name;
    public string Thumbnail=>Model.Tile;
    public string Availability=>Model.IsBase?"Default":Available?"Installed":"Preview only";
}
public sealed record ChromaChoice(Chroma? Model)
{
    public string Name=>Model?.Name??"Original colors";
    public List<string> Colors=>Model?.Colors??["#F2A7CA","#AC96E8"];
    public string Preview=>Model?.Preview??"";
}
public sealed record FormChoice(string? Id,string Name,string? Preview);
public sealed record CustomRow(CustomSkin Model,string Status)
{
    public string Name=>Model.Name;
    public string Author=>Model.Author;
    public string Version=>Model.Version;
    public bool Enabled=>Model.Enabled;
}

public sealed partial class MainViewModel : ObservableObject
{
    public StateStore Store { get; }
    private readonly MetadataProvider metadata;
    private readonly LibraryStore library;
    private readonly LibraryInstaller installer;
    private readonly CustomSkinService custom;
    private readonly PatcherController patcher;
    private readonly NativeBridge native;
    private readonly AppPaths paths;
    private readonly UpdateService updates;
    private readonly LeagueClientApi leagueClient;
    private readonly ILogger<MainViewModel> log;
    public DuoViewModel Duo { get; }
    private Catalog? catalog;
    private UpdateReport? updateReport;
    private CancellationTokenSource? operation;
    private List<ChampionCard> allChampions=[];
    private int columns=6;
    private double contentWidth=1000,contentHeight=680;
    private bool changing;
    private Func<CancellationToken,Task>? retry;
    [ObservableProperty] private string page="Skins";
    [ObservableProperty] private string search="";
    [ObservableProperty] private bool favoritesOnly;
    [ObservableProperty] private bool savedOnly;
    [ObservableProperty] private string customSearch="";
    [ObservableProperty] private ChampionCard? currentChampion;
    [ObservableProperty] private ChampionCard? customChampion;
    [ObservableProperty] private SkinCard? selectedSkin;
    [ObservableProperty] private ChromaChoice? selectedChroma;
    [ObservableProperty] private FormChoice? selectedForm;
    [ObservableProperty] private CustomRow? selectedCustom;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth),nameof(SidebarMargin),nameof(PageMargin),nameof(SkinsNavigation),nameof(CustomNavigation),nameof(DuoNavigation),nameof(SettingsNavigation))]
    private bool compactLayout;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageMargin),nameof(SkinCarouselHeight),nameof(SkinThumbnailHeight),nameof(SkinThumbnailWidth))]
    private bool shortLayout;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomChampionWidth))]
    private bool narrowLayout;
    [ObservableProperty] private double championCardWidth=142;
    [ObservableProperty] private double championPortraitHeight=126;
    [ObservableProperty] private double customTableHeight=260;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string message="Loading your collection…";
    [ObservableProperty] private string activity="Ready";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string gameFolder="";
    [ObservableProperty] private string libraryFolder="";
    [ObservableProperty] private string language="default";
    [ObservableProperty] private bool reducedMotion;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MascotSource),nameof(MascotName),nameof(MascotToggleLabel),nameof(AtelierLabel))]
    private bool homuraTheme;
    [ObservableProperty] private bool enforceSkinhackScan;
    [ObservableProperty] private bool elevatePatcher;
    [ObservableProperty] private ConflictChoice conflictChoice;
    public ObservableCollection<ChampionRow> ChampionRows { get; }=[];
    public ObservableCollection<ChampionCard> CustomChampions { get; }=[];
    public ObservableCollection<SkinCard> Skins { get; }=[];
    public ObservableCollection<ChromaChoice> Chromas { get; }=[];
    public ObservableCollection<FormChoice> Forms { get; }=[];
    public ObservableCollection<CustomRow> CustomSkins { get; }=[];
    public List<string> Languages { get; }=["default","ar_ae","cs_cz","de_de","el_gr","en_au","en_gb","en_ph","en_sg","es_ar","es_es","es_mx","fr_fr","hu_hu","id_id","it_it","ja_jp","ko_kr","pl_pl","pt_br","ro_ro","ru_ru","th_th","tr_tr","zh_cn","zh_tw"];
    public List<ConflictChoice> ConflictChoices { get; }=[ConflictChoice.Reject,ConflictChoice.PreferRepository,ConflictChoice.PreferCustom];
    public bool IsSkins=>Page=="Skins";
    public double SidebarWidth=>CompactLayout?76:220;
    public Thickness SidebarMargin=>CompactLayout?new(12,22,12,16):new(22,30,22,22);
    public Thickness PageMargin=>new(CompactLayout?16:28,ShortLayout?8:22,CompactLayout?16:28,ShortLayout?8:22);
    public string SkinsNavigation=>CompactLayout?"◇":"◇   Skins";
    public string CustomNavigation=>CompactLayout?"☷":"☷   Custom Skins";
    public string DuoNavigation=>CompactLayout?"♡":"♡   Duo";
    public string SettingsNavigation=>CompactLayout?"⚙":"⚙   Settings";
    public string MascotSource=>$"pack://application:,,,/MadokaChanger;component/Assets/{(HomuraTheme?"HomuraChibi":"MadokaChibi")}.png";
    public string MascotName=>HomuraTheme?"Homura chibi":"Madoka chibi";
    public string MascotToggleLabel=>HomuraTheme?"Switch to Madoka's pink theme":"Switch to Homura's purple and grey theme";
    public string AtelierLabel=>HomuraTheme?"HOMURA SKIN ATELIER":"MADOKA SKIN ATELIER";
    public double CustomChampionWidth=>NarrowLayout?150:210;
    public double SkinCarouselHeight=>ShortLayout?76:112;
    public double SkinThumbnailHeight=>ShortLayout?36:60;
    public double SkinThumbnailWidth=>ShortLayout?88:112;
    public bool IsCustom=>Page=="Custom Skins";
    public bool IsDuo=>Page=="Duo";
    public bool IsSettings=>Page=="Settings";
    public bool IsBrowser=>IsSkins && CurrentChampion is null;
    public bool IsDetail=>IsSkins && CurrentChampion is not null;
    public bool IsEmpty=>allChampions.Count==0;
    public bool IsSearchEmpty=>!IsEmpty && ChampionRows.Count==0;
    public string ChampionCount=>$"{allChampions.Count} champions · {catalog?.Patch??"metadata unavailable"}";
    public string ResultCount=>$"{ChampionRows.Sum(r=>r.Champions.Count)} champions";
    public string LibraryVersion=>library.Manifest is { } m ? $"Library {m.Commit[..8]} · {m.Packages.Count:N0} packages":"Skin library not installed";
    public string UpdateLabel=>updateReport is { } r && (r.AppUpdateAvailable||r.LtkActionNeeded)?"↻  Update available":library.Manifest is null?"↓  Install Skin Library":"↻  Update";
    public string AppVersionText=>$"Independent fan project · v{UpdateService.CurrentVersion.ToString(3)}";
    public bool BackendReady=>native.BackendAvailable;
    public string PatcherLabel=>patcher.Status.State switch
    {
        PatcherState.Unavailable => "Unavailable",
        PatcherState.Waiting when !Duo.HasLockedChampion => "Waiting for champion lock-in",
        PatcherState.Waiting => "Waiting for a supported skin for the locked champion",
        PatcherState.Stopped when !BackendReady => "Unavailable",
        _ => patcher.Status.State.ToString()
    };
    public string PatcherDetail=>patcher.Status.Message;
    public string GameStatus=>GameLocator.Validate(Store.State.Settings.GameFolder)?"League installation found":"League folder needed";
    public string DependencyStatus=>(updateReport?.Summary??$"MadokaChanger v{UpdateService.CurrentVersion.ToString(3)} · press Update to check for new versions\nLTK Manager {LtkInstall.Detect().Display}")+
        $"\nBuilt-in backend: {(BackendReady?"ready":"incomplete, reinstall MadokaChanger")}\nPatcher: {native.PatcherDescription}\nMetadata: {catalog?.Patch??"unavailable"} / {catalog?.Language??Language}";
    public bool CanUpdate=>!IsBusy&&!patcher.IsActive&&catalog is not null;
    public bool CanStart=>!IsBusy&&!patcher.IsActive&&BackendReady&&GameLocator.Validate(Store.State.Settings.GameFolder);
    public bool CanStop=>patcher.IsActive || patcher.Status.State==PatcherState.Error;
    public bool CanImport=>!IsBusy&&!patcher.IsActive&&native.Available;
    public bool HasCustomSelection=>SelectedCustom is not null;
    public bool IsSelectionAvailable(Selection selection)=>library.Find(selection).Count==1;
    public string DisabledReason=>patcher.IsActive?"Stop the patcher to install, update, import, or uninstall packages.":IsBusy?"A library operation is in progress. Browsing and saving selections remain available.":!BackendReady?"Built-in backend files are missing. Extract the full MadokaChanger download again.":"";
    public bool HasDisabledReason=>DisabledReason.Length>0;
    public string ChampionName=>CurrentChampion?.Name??"";
    public string SkinName=>SelectedSkin?.Name??"";
    public string Splash=>SelectedForm?.Preview is {Length:>0} p? p:SelectedSkin?.Model.Splash??"";
    public string ChromaPreview=>SelectedChroma?.Preview??"";
    public string VariantName=>SelectedForm?.Id is not null ? SelectedForm.Name : SelectedChroma?.Model?.Name??"Original colors";
    public bool HasChromas=>Chromas.Count>1;
    public bool HasForms=>Forms.Count>1;
    public bool HasChromaPreview=>ChromaPreview.Length>0;
    public Selection? PreviewSelection=>CurrentChampion is { } c && SelectedSkin is { } s ? new(c.Model.Id,s.Model.Id,SelectedChroma?.Model?.Id,SelectedForm?.Id):null;
    public bool CanApply=>PreviewSelection is { } s && (SelectedSkin?.Model.IsBase==false||s.ChromaId is not null||s.Form is not null) && library.Find(s).Count==1;
    public string AvailabilityText=>CanApply?"◆ Package installed · ready to save":SelectedSkin?.Model.IsBase==true&&SelectedChroma?.Model is null&&SelectedForm?.Id is null?"Original appearance · use Reset to Default":library.Manifest is null?"Preview only · install the skin library to apply":"Package unavailable or unmapped · this variant cannot be applied";
    public string SavedText
    {
        get
        {
            if(CurrentChampion is null || !Store.State.Selections.TryGetValue(CurrentChampion.Model.Id,out var saved)) return "No saved override";
            if(library.Find(saved).Count!=1) return "Saved package unavailable · choose a replacement or reset";
            var name=catalog?.Champions.SelectMany(c=>c.Skins).FirstOrDefault(s=>s.Id==saved.SkinId)?.Name??saved.SkinId.ToString();
            return $"Saved: {name}"+(saved.ChromaId is { } ch?$" · chroma {ch}":"")+(saved.Form is { } f?$" · form {f}":"");
        }
    }
    public string ActiveText
    {
        get
        {
            if(patcher.Status.State==PatcherState.Waiting)
                return !Duo.HasLockedChampion
                    ? "Waiting for your local champion to lock in · only that champion's saved skin is applied"
                    : "Waiting for a saved skin for your locked-in champion · Stop cancels automatic patching";
            if(Duo.HasParty)
            {
                if(Duo.PreparationState is "unavailable" or "error")return Duo.LocalStatus;
                if(patcher.ActiveConfiguration is null)return "Duo party active · press Start to prepare the locked champion's selection";
                if(patcher.ActiveConfiguration==patcher.ConfigurationHash())return "Active configuration matches Duo selections";
                return Duo.PreparationState=="pending"?"Duo changes pending · rebuild automatically when League closes":"Duo changes preparing · rebuild automatically when safe";
            }
            return patcher.ActiveConfiguration is null?"Patcher stopped · saved changes take effect on Start":patcher.ActiveConfiguration==patcher.ConfigurationHash()?"Active configuration matches the locked champion's saved selection":Duo.PreparationState=="pending"?"Committed changes pending · rebuild automatically when League closes":"Committed changes preparing · rebuild automatically when safe";
        }
    }
    public string SelectedCustomDetails=>SelectedCustom is { } c?$"{c.Name}\n{c.Model.Description}\n\n{c.Model.FileName}\n{c.Model.Overrides.Count:N0} file overrides · SHA-256 {c.Model.Sha256}\n\n{c.Status}":"Select an imported skin to inspect its package and conflicts.";
    public string CustomCount=>$"{CustomSkins.Count} imported skins";
    public string ConflictSummary { get { try {var conflicts=ConflictResolver.Find(patcher.Sources());return conflicts.Count==0?"No enabled file conflicts":$"{conflicts.Count:N0} file conflicts · choose a priority or disable conflicting skins";} catch(InvalidOperationException e) {return e.Message;} } }
    public MainViewModel(StateStore store,MetadataProvider metadata,LibraryStore library,LibraryInstaller installer,CustomSkinService custom,PatcherController patcher,NativeBridge native,AppPaths paths,UpdateService updates,LeagueClientApi leagueClient,DuoViewModel duo,ILogger<MainViewModel> log)
    {
        Store=store;this.metadata=metadata;this.library=library;this.installer=installer;this.custom=custom;this.patcher=patcher;this.native=native;this.paths=paths;this.updates=updates;this.leagueClient=leagueClient;Duo=duo;this.log=log;
        Duo.PropertyChanged+=(_,_)=>{OnPropertyChanged(nameof(ActiveText));OnPropertyChanged(nameof(PatcherLabel));};
        patcher.StatusChanged+=s=>Application.Current.Dispatcher.InvokeAsync(()=> { OnPropertyChanged(nameof(PatcherLabel));OnPropertyChanged(nameof(PatcherDetail));NotifyActions();OnPropertyChanged(nameof(ActiveText)); });
    }
    public async Task InitializeAsync()
    {
        await Store.LoadAsync(); var s=Store.State.Settings;
        HomuraTheme=s.HomuraTheme;ThemePalette.Apply(HomuraTheme);
        GameFolder=s.GameFolder.Length>0?s.GameFolder:GameLocator.Detect()??""; s.GameFolder=GameFolder;
        LibraryFolder=s.LibraryFolder; Language=s.Language; ReducedMotion=s.ReducedMotion; changing=true;ConflictChoice=Store.State.ConflictChoice;EnforceSkinhackScan=s.EnforceSkinhackScan;ElevatePatcher=s.ElevatePatcher;changing=false;
        await RunAsync(async ct=>
        {
            try {await library.LoadAsync(ct);} catch(Exception e) when(e is IOException or System.Text.Json.JsonException) {log.LogError(e,"Library manifest could not be loaded");Message=e.Message;}
            catalog=await metadata.LoadAsync(Language,progress:new Progress<string>(m=>Activity=m),ct:ct);Duo.SetCatalog(catalog);
            RemoveClassicPreferences();
            PopulateChampions(); Message=metadata.IsOffline?"Offline · cached collection available":"Your collection is ready. Choose a champion to preview skins.";
            await Store.SaveAsync(ct); NotifyLibrary();
            if(await installer.MigrateAsync(new Progress<TransferProgress>(p=>{Activity=p.Display;Progress=p.Percent;}),ct) is { } migrated)
                log.LogInformation("{Migration}",migrated);
            NotifyLibrary();
        });
        Duo.StartMonitoring();
        OnPropertyChanged(nameof(DependencyStatus)); OnPropertyChanged(nameof(GameStatus));
        _=CheckUpdatesQuietlyAsync();
    }
    /// <summary>One background check at launch so the Update button and Settings show what's new, without installing anything.</summary>
    private async Task CheckUpdatesQuietlyAsync()
    {
        try
        {
            updateReport=await updates.CheckAsync(CancellationToken.None);
            OnPropertyChanged(nameof(UpdateLabel));OnPropertyChanged(nameof(DependencyStatus));
            if(HasError||IsBusy)return;
            if(updateReport.LtkMissing){Message="LTK Manager isn't installed, so League will block skins. Press Update to install it.";HasError=false;}
            else if(updateReport.AppUpdateAvailable||updateReport.LtkUpdateAvailable){Message="An update is available. Press Update to install it.";HasError=false;}
        }
        catch(Exception e){log.LogWarning(e,"Launch update check failed");}
    }
    private void PopulateChampions()
    {
        allChampions=catalog!.Champions.OrderBy(c=>c.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(c=>c.Id).Select(c=>new ChampionCard(c,this)).ToList(); Refilter(); FilterCustomChampions();
        OnPropertyChanged(nameof(ChampionCount));OnPropertyChanged(nameof(IsEmpty)); NotifyActions();
    }
    private void RemoveClassicPreferences()
    {
        foreach(var id in metadata.ExcludedChampionIds){Store.State.Selections.Remove(id);Store.State.Favorites.Remove(id);}
        for(var i=0;i<Store.State.CustomSkins.Count;i++)
            if(metadata.ExcludedChampionIds.Contains(Store.State.CustomSkins[i].ChampionId))Store.State.CustomSkins[i]=Store.State.CustomSkins[i] with{Enabled=false};
    }
    public void SetViewport(double width,double height)
    {
        CompactLayout=width<1100;ShortLayout=height<760;
        SetContentViewport(contentWidth,contentHeight);
    }
    public void SetContentViewport(double width,double height)
    {
        if(!double.IsFinite(width)||!double.IsFinite(height)||width<=0||height<=0)return;
        contentWidth=width;contentHeight=height;NarrowLayout=width<850;
        CustomTableHeight=Math.Clamp(height-200,160,360);
        SetColumns(width);
    }
    public void SetColumns(double width)
    {
        var available=Math.Max(180,width-(CompactLayout?32:56)-20);
        var count=Math.Max(1,(int)(available/146));
        ChampionCardWidth=Math.Floor(available/count)-8;ChampionPortraitHeight=Math.Round(ChampionCardWidth*.89);
        if(columns==count)return;columns=count;Refilter();
    }
    private void Refilter()
    {
        var filtered=allChampions.Where(c=>c.Name.Contains(Search,StringComparison.CurrentCultureIgnoreCase)&&(!FavoritesOnly||c.IsFavorite)&&(!SavedOnly||c.HasSelection)).ToList();
        ChampionRows.Clear(); foreach(var row in filtered.Chunk(columns)) ChampionRows.Add(new(row.ToList()));
        OnPropertyChanged(nameof(ResultCount));OnPropertyChanged(nameof(IsSearchEmpty));
    }
    private void FilterCustomChampions()
    {
        CustomChampions.Clear();foreach(var c in allChampions.Where(c=>c.Name.Contains(CustomSearch,StringComparison.CurrentCultureIgnoreCase))) CustomChampions.Add(c);
        if(CustomChampion is null) CustomChampion=CustomChampions.FirstOrDefault();
    }
    partial void OnSearchChanged(string value)=>Refilter();
    partial void OnFavoritesOnlyChanged(bool value)=>Refilter();
    partial void OnSavedOnlyChanged(bool value)=>Refilter();
    partial void OnCustomSearchChanged(string value)=>FilterCustomChampions();
    partial void OnPageChanged(string value) {foreach(var p in new[]{nameof(IsSkins),nameof(IsCustom),nameof(IsDuo),nameof(IsSettings),nameof(IsBrowser),nameof(IsDetail)})OnPropertyChanged(p);}
    partial void OnIsBusyChanged(bool value)=>NotifyActions();
    partial void OnCustomChampionChanged(ChampionCard? value)=>RefreshCustom();
    partial void OnSelectedCustomChanged(CustomRow? value) {OnPropertyChanged(nameof(SelectedCustomDetails));OnPropertyChanged(nameof(HasCustomSelection));}
    partial void OnCurrentChampionChanged(ChampionCard? value)
    {
        OnPropertyChanged(nameof(IsBrowser));OnPropertyChanged(nameof(IsDetail));OnPropertyChanged(nameof(ChampionName));
        if(value is null)return;
        Skins.Clear(); foreach(var skin in value.Model.Skins) Skins.Add(new(skin,library.Find(new(value.Model.Id,skin.Id)).Count==1));
        Store.State.Selections.TryGetValue(value.Model.Id,out var saved);
        SelectedSkin=Skins.FirstOrDefault(s=>s.Model.Id==saved?.SkinId)??Skins.FirstOrDefault();
        if(saved?.ChromaId is { } chroma) SelectedChroma=Chromas.FirstOrDefault(c=>c.Model?.Id==chroma)??Chromas.FirstOrDefault();
        if(saved?.Form is { } form) SelectedForm=Forms.FirstOrDefault(f=>f.Id==form)??Forms.FirstOrDefault();
        NotifyPreview();
    }
    partial void OnSelectedSkinChanged(SkinCard? value)
    {
        changing=true;Chromas.Clear();Forms.Clear();Chromas.Add(new(null));Forms.Add(new(null,"Original form",null));
        if(value is not null)
        {
            foreach(var c in value.Model.Chromas) Chromas.Add(new(c));
            if(CurrentChampion is not null && library.Manifest is not null)
                foreach(var p in library.Manifest.Packages.Where(p=>p.ChampionId==CurrentChampion.Model.Id&&p.SkinId==value.Model.Id&&p.Form is not null&&p.MappingError is null))
                    Forms.Add(new(p.Form,p.Name,p.Preview is not null?ArchiveSafety.SafeDestination(library.ActiveDirectory!,p.Preview):null));
        }
        SelectedChroma=Chromas[0];SelectedForm=Forms[0];changing=false;NotifyPreview();
    }
    partial void OnSelectedChromaChanged(ChromaChoice? value) {if(!changing&&value?.Model is not null){changing=true;SelectedForm=Forms.FirstOrDefault();changing=false;}NotifyPreview();}
    partial void OnSelectedFormChanged(FormChoice? value) {if(!changing&&value?.Id is not null){changing=true;SelectedChroma=Chromas.FirstOrDefault();changing=false;}NotifyPreview();}
    partial void OnConflictChoiceChanged(ConflictChoice value)
    {
        if(changing)return;
        Store.State.ConflictChoice=value;
        _=PersistAsync();OnPropertyChanged(nameof(ActiveText));
    }
    // Patcher options save as soon as they change and apply on the next Start, like the conflict priority.
    partial void OnEnforceSkinhackScanChanged(bool value)
    {
        if(changing)return;
        Store.State.Settings.EnforceSkinhackScan=value;_=PersistAsync();
        Message=value?"Anti-skinhack scan enforced. Library skins are official skins and will be blocked in game; custom skins still load.":"Anti-skinhack scan off. Library skins load in game from the next Start.";HasError=false;
    }
    partial void OnElevatePatcherChanged(bool value)
    {
        if(changing)return;
        Store.State.Settings.ElevatePatcher=value;_=PersistAsync();
        Message=value?"The patcher will ask for administrator access on every Start.":"The patcher runs without administrator access unless League itself is set to run as administrator.";HasError=false;
    }
    private async Task<bool> PersistAsync() {try{await Store.SaveAsync();return true;}catch(Exception e){ShowError(e);return false;}}
    private void NotifyPreview()
    {
        foreach(var p in new[]{nameof(SkinName),nameof(Splash),nameof(ChromaPreview),nameof(VariantName),nameof(HasChromas),nameof(HasForms),nameof(HasChromaPreview),nameof(CanApply),nameof(AvailabilityText),nameof(SavedText),nameof(ActiveText)})OnPropertyChanged(p);
        ApplySelectionCommand.NotifyCanExecuteChanged();SetProfileBackgroundCommand.NotifyCanExecuteChanged();
    }
    private void NotifyActions()
    {
        foreach(var p in new[]{nameof(CanUpdate),nameof(CanStart),nameof(CanStop),nameof(CanImport),nameof(DisabledReason),nameof(HasDisabledReason)})OnPropertyChanged(p);
        UpdateCommand.NotifyCanExecuteChanged();StartCommand.NotifyCanExecuteChanged();StopCommand.NotifyCanExecuteChanged();
    }
    private void NotifyLibrary()
    {
        OnPropertyChanged(nameof(LibraryVersion));OnPropertyChanged(nameof(UpdateLabel));OnPropertyChanged(nameof(DependencyStatus));OnPropertyChanged(nameof(ActiveText));
        OnPropertyChanged(nameof(PatcherLabel));
        if(CurrentChampion is { } c) {CurrentChampion=null;CurrentChampion=c;}
        foreach(var card in allChampions)card.Refresh();RefreshCustom();NotifyActions();
    }
    private void RefreshCustom()
    {
        var selected=SelectedCustom?.Model.Id; CustomSkins.Clear();
        List<OverrideConflict> conflicts=[];try{conflicts=ConflictResolver.Find(patcher.Sources());}catch(InvalidOperationException){ }
        foreach(var c in Store.State.CustomSkins.Where(c=>c.ChampionId==CustomChampion?.Model.Id))
        {
            var count=conflicts.Count(x=>x.FirstId=="custom:"+c.Id||x.SecondId=="custom:"+c.Id);
            CustomSkins.Add(new(c,count>0?$"{count} conflicts":c.Enabled?"Enabled":"Disabled"));
        }
        SelectedCustom=CustomSkins.FirstOrDefault(c=>c.Model.Id==selected);
        OnPropertyChanged(nameof(CustomCount));OnPropertyChanged(nameof(ConflictSummary));OnPropertyChanged(nameof(ActiveText));
    }
    [RelayCommand] private void Navigate(string page)=>Page=page;
    [RelayCommand] private void OpenChampion(ChampionCard card)=>CurrentChampion=card;
    [RelayCommand] private void Back()=>CurrentChampion=null;
    [RelayCommand] private async Task FavoriteAsync(ChampionCard card)
    {
        var wasFavorite=Store.State.Favorites.Contains(card.Model.Id);
        if(!Store.State.Favorites.Remove(card.Model.Id))Store.State.Favorites.Add(card.Model.Id);
        if(!await PersistAsync()){if(wasFavorite)Store.State.Favorites.Add(card.Model.Id);else Store.State.Favorites.Remove(card.Model.Id);}
        card.Refresh();if(FavoritesOnly)Refilter();
    }
    [RelayCommand] private void PreviousSkin() {var i=SelectedSkin is null?0:Skins.IndexOf(SelectedSkin);if(Skins.Count>0)SelectedSkin=Skins[(i-1+Skins.Count)%Skins.Count];}
    [RelayCommand] private void NextSkin() {var i=SelectedSkin is null?0:Skins.IndexOf(SelectedSkin);if(Skins.Count>0)SelectedSkin=Skins[(i+1)%Skins.Count];}
    [RelayCommand(CanExecute=nameof(CanApply))] private async Task ApplySelectionAsync()
    {
        if(PreviewSelection is not { } s || !CanApply)return;
        Selection? old;lock(Store.State.Selections){Store.State.Selections.TryGetValue(s.ChampionId,out old);Store.State.Selections[s.ChampionId]=s;}
        if(!await PersistAsync()){lock(Store.State.Selections){if(old is null)Store.State.Selections.Remove(s.ChampionId);else Store.State.Selections[s.ChampionId]=old;}NotifyPreview();return;}
        Duo.NotifyCommittedSelectionChanged();foreach(var c in allChampions)c.Refresh();if(SavedOnly)Refilter();NotifyPreview();RefreshCustom();Message="Selection saved. "+ActiveText;HasError=false;
    }
    [RelayCommand] private async Task ResetAsync()
    {
        if(CurrentChampion is null)return;var id=CurrentChampion.Model.Id;Selection? old;lock(Store.State.Selections){Store.State.Selections.TryGetValue(id,out old);Store.State.Selections.Remove(id);}
        if(!await PersistAsync()){if(old is not null)lock(Store.State.Selections)Store.State.Selections[id]=old;NotifyPreview();return;}
        Duo.NotifyCommittedSelectionChanged();SelectedSkin=Skins.FirstOrDefault(s=>s.Model.IsBase);foreach(var c in allChampions)c.Refresh();if(SavedOnly)Refilter();NotifyPreview();RefreshCustom();Message="Champion override removed. "+ActiveText;HasError=false;
    }
    /// <summary>
    /// One button for everything: installs or updates LTK Manager, offers a newer MadokaChanger, then refreshes the skin library.
    /// </summary>
    [RelayCommand(CanExecute=nameof(CanUpdate))] private async Task UpdateAsync()=>await RunAsync(async ct=>
    {
        var steps=new Progress<string>(p=>Activity=p);var notes=new List<string>();
        Activity="Checking MadokaChanger and LTK Manager releases";
        updateReport=await updates.CheckAsync(ct);OnPropertyChanged(nameof(DependencyStatus));OnPropertyChanged(nameof(UpdateLabel));
        if(updateReport.Problem is { } problem)notes.Add(problem);
        if(updateReport.LtkActionNeeded)
        {
            try{notes.Add(await updates.InstallLtkAsync(updateReport.LtkLatest!,steps,ct));}
            catch(OperationCanceledException e) when(!ct.IsCancellationRequested){notes.Add(e.Message);}
            catch(Exception e) when(e is IOException or InvalidDataException or HttpRequestException or TimeoutException){log.LogError(e,"LTK Manager install failed");notes.Add("LTK Manager wasn't installed: "+e.Message);}
            updateReport=updateReport with{Ltk=LtkInstall.Detect()};OnPropertyChanged(nameof(DependencyStatus));NotifyLibrary();
        }
        if(updateReport.AppUpdateAvailable && updateReport.AppLatest is { } latest &&
           MessageBox.Show($"MadokaChanger {latest.Version.ToString(3)} is available (you have {UpdateService.CurrentVersion.ToString(3)}).\n\nUpdate now? MadokaChanger closes, installs the update and reopens. Your skins and settings stay.","MadokaChanger update",MessageBoxButton.YesNo,MessageBoxImage.Information)==MessageBoxResult.Yes)
        {
            var setup=await updates.DownloadAppUpdateAsync(latest,steps,ct);
            Message=$"Installing MadokaChanger {latest.Version.ToString(3)}. It reopens when it's done.";
            UpdateService.LaunchAppSetup(setup);
            _=Application.Current.Dispatcher.InvokeAsync(()=>Application.Current.MainWindow?.Close());
            return;
        }
        Activity="Checking the skin library";
        notes.Insert(0,await installer.InstallAsync(catalog!,new Progress<TransferProgress>(p=> {Activity=p.Display;Progress=p.Percent;}),ct));
        Message=string.Join(" · ",notes);NotifyLibrary();OnPropertyChanged(nameof(UpdateLabel));
    });
    public bool CanSetProfileBackground=>SelectedSkin is not null && CurrentChampion is not null;
    /// <summary>Forked from league-profile-tool: sets the League profile background to the previewed skin's splash.</summary>
    [RelayCommand(CanExecute=nameof(CanSetProfileBackground))] private async Task SetProfileBackgroundAsync()
    {
        if(SelectedSkin is not { } skin)return;
        try
        {
            Activity="Setting profile background";
            await leagueClient.SetProfileBackgroundAsync(skin.Model.Id,Store.State.Settings.GameFolder,CancellationToken.None);
            Message=$"Profile background set to {skin.Name}. Open your profile in the League client to see it.";HasError=false;
        }
        catch(Exception e) when(e is InvalidOperationException or InvalidDataException or ArgumentOutOfRangeException or TaskCanceledException){ShowError(e);}
        finally{Activity="Ready";}
    }
    [RelayCommand(CanExecute=nameof(CanStart))] private async Task StartAsync()=>await RunAsync(async ct=>
    {
        await Duo.RefreshLocalStateAsync(ct);
        await patcher.StartAsync(new Progress<string>(p=>Activity=p),ct);Message=patcher.Status.Message;OnPropertyChanged(nameof(ActiveText));
    });
    [RelayCommand(CanExecute=nameof(CanStop))] private async Task StopAsync()
    {
        operation?.Cancel();await patcher.StopAsync();Message="Patcher stopped.";OnPropertyChanged(nameof(ActiveText));NotifyActions();
    }
    [RelayCommand] private void Cancel()=>operation?.Cancel();
    [RelayCommand] private async Task RetryAsync()
    {
        if(retry is not null)await RunAsync(retry);
        else await RefreshMetadataAsync();
    }
    [RelayCommand] private async Task RefreshMetadataAsync()=>await RunAsync(async ct=>
    {
        catalog=await metadata.LoadAsync(Language,true,new Progress<string>(p=>Activity=p),ct);Duo.SetCatalog(catalog);RemoveClassicPreferences();await Store.SaveAsync(ct);CurrentChampion=null;PopulateChampions();NotifyLibrary();Message=metadata.IsOffline?"Offline · using cached metadata":"Champion metadata refreshed.";
    });
    public async Task ImportFilesAsync(IEnumerable<string> files)
    {
        if(!CanImport || catalog is null)return;
        var champion=CustomChampion?.Model.Id??0;
        await RunAsync(async ct=> {foreach(var f in files){var imported=await custom.ImportAsync(f,champion,catalog,ct);CustomChampion=allChampions.First(c=>c.Model.Id==imported.ChampionId);Activity="Imported "+imported.Name;}RefreshCustom();Message="Custom skins imported. Enable them when ready.";});
    }
    [RelayCommand] private async Task ToggleCustomAsync()
    {
        if(SelectedCustom is not { } c)return;try{await custom.SetEnabledAsync(c.Model,!c.Enabled,CancellationToken.None);RefreshCustom();Message=ActiveText;HasError=false;}catch(Exception e){ShowError(e);}
    }
    [RelayCommand] private async Task UninstallCustomAsync()
    {
        if(SelectedCustom is not { } c)return;await RunAsync(async ct=>{await custom.UninstallAsync(c.Model,ct);RefreshCustom();Message="Custom skin uninstalled.";});
    }
    [RelayCommand] private void OpenCustomFolder() {if(SelectedCustom is { } c)OpenPath(Path.GetDirectoryName(c.Model.ProjectPath)!);}
    [RelayCommand] private async Task SaveSettingsAsync()
    {
        try
        {
            if(patcher.IsActive||IsBusy)throw new InvalidOperationException("Stop the patcher and finish the current operation before changing storage settings.");
            var game=GameFolder.Length==0?"":GameLocator.Normalize(GameFolder);
            if(game.Length>0&&!GameLocator.Validate(game))throw new InvalidOperationException("League folder must contain League of Legends.exe and DATA/FINAL.");
            var root=Path.GetFullPath(LibraryFolder);Directory.CreateDirectory(root);
            if(new[]{paths.Custom,paths.Metadata,paths.Images,paths.Temporary,paths.Overlay,paths.Logs}.Any(p=>root.Equals(p,StringComparison.OrdinalIgnoreCase)||root.StartsWith(p+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Choose a separate library directory.");
            Store.State.Settings=new(){GameFolder=game,LibraryFolder=root,Language=Language,ReducedMotion=ReducedMotion,HomuraTheme=HomuraTheme,EnforceSkinhackScan=EnforceSkinhackScan,ElevatePatcher=ElevatePatcher};await Store.SaveAsync();
            GameFolder=game;await library.LoadAsync();NotifyLibrary();OnPropertyChanged(nameof(GameStatus));Message="Settings saved. Refresh metadata after changing language. Existing libraries stay in their original folders.";HasError=false;
        }catch(Exception e){ShowError(e);}
    }
    partial void OnHomuraThemeChanged(bool value)=>ThemePalette.Apply(value);
    [RelayCommand] private async Task ToggleThemeAsync()
    {
        var previous=HomuraTheme;
        HomuraTheme=!previous;Store.State.Settings.HomuraTheme=HomuraTheme;
        try {await Store.SaveAsync();}
        catch(Exception e) {HomuraTheme=previous;Store.State.Settings.HomuraTheme=previous;ShowError(e);}
    }
    [RelayCommand] private void DetectGame() {GameFolder=GameLocator.Detect()??"";Message=GameFolder.Length>0?"League installation detected. Save settings to use it.":"No League installation found. Choose the Game folder manually.";}
    [RelayCommand] private void OpenLogs()=>OpenPath(paths.Logs);
    [RelayCommand] private void ViewLog()
    {
        var file=Directory.EnumerateFiles(paths.Logs,"*.jsonl").OrderDescending().FirstOrDefault();if(file is not null)OpenPath(file);
    }
    [RelayCommand] private void OpenData()=>OpenPath(paths.Root);
    [RelayCommand] private void ClearCache()
    {
        try {foreach(var p in Directory.EnumerateFiles(paths.Images))File.Delete(p);Message="Image cache cleared. Images download again as needed.";HasError=false;}catch(Exception e){ShowError(e);}
    }
    private void OpenPath(string path) {try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch(Exception e){ShowError(e);}}
    private async Task RunAsync(Func<CancellationToken,Task> action)
    {
        if(IsBusy)return;retry=action;IsBusy=true;HasError=false;Progress=0;operation=new();
        try {await action(operation.Token);}
        catch(OperationCanceledException){Message="Operation cancelled. The previously active library is preserved.";}
        catch(Exception e){ShowError(e);}
        finally {operation.Dispose();operation=null;IsBusy=false;Activity="Ready";OnPropertyChanged(nameof(PatcherLabel));NotifyPreview();}
    }
    private void ShowError(Exception e) {log.LogError(e,"Application operation failed");HasError=true;Message=e.Message;}
    public async Task ShutdownAsync() {operation?.Cancel();await Duo.ShutdownAsync();await patcher.StopAsync();await Store.SaveAsync();}
}
