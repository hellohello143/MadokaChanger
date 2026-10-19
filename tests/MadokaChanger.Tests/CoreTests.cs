using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MadokaChanger.Core;
using Xunit;

namespace MadokaChanger.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"MadokaChanger-tests-"+Guid.NewGuid().ToString("N"));
    private static Catalog Metadata=>new("16.20","default",DateTimeOffset.UtcNow,[new(103,"Ahri","Ahri","the Nine-Tailed Fox","",[new(103001,"Dynasty Ahri",false,"","","",[new(103052,"Ahri-versary","",["#2E38C4","#C8003F"])])])]);
    private static PackageInspection Info=>new("Dynasty Ahri","Author","1.0","",[],["1234567890abcdef"],100);
    private static RepositoryFile FileRecord(string path)=>new(path,"abc",100);
    [Theory]
    [InlineData("../escape")][InlineData("C:/escape")][InlineData("/root")][InlineData("safe/../../escape")][InlineData("safe\\..\\escape")][InlineData("safe/NUL.txt")][InlineData("safe/trailing.")][InlineData("safe/a:b")]
    public void Archive_paths_cannot_escape_or_use_windows_special_names(string path)=>Assert.Throws<InvalidDataException>(()=>ArchiveSafety.ValidatePath(path));
    [Fact] public void Nested_archive_paths_stay_inside_the_destination()=>Assert.StartsWith(Path.GetFullPath(root),ArchiveSafety.SafeDestination(root,"META/info.json"));
    [Fact] public void Zip_rejects_case_collisions()
    {
        using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){zip.CreateEntry("RAW/file.bin");zip.CreateEntry("raw/File.bin");}memory.Position=0;using var read=new ZipArchive(memory);Assert.Throws<InvalidDataException>(()=>ArchiveSafety.ValidateEntries(read));
    }
    [Fact] public void Zip_rejects_symlinks()
    {
        using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){zip.CreateEntry("link").ExternalAttributes=unchecked((int)0xA1FF0000);}memory.Position=0;using var read=new ZipArchive(memory);Assert.Throws<InvalidDataException>(()=>ArchiveSafety.ValidateEntries(read));
    }
    [Fact] public async Task Arbitrary_zip_is_not_an_importable_package()
    {
        Directory.CreateDirectory(root);var file=Path.Combine(root,"invalid.zip");using(var zip=ZipFile.Open(file,ZipArchiveMode.Create))zip.CreateEntry("random.txt");
        await Assert.ThrowsAsync<InvalidDataException>(()=>Validator().InspectAsync(file,CancellationToken.None));
    }
    [Fact] public async Task Fantome_raw_structure_is_validated_and_hashed()
    {
        var file=CreatePackage(root,"good.fantome");var result=await Validator().InspectAsync(file,CancellationToken.None);
        Assert.Equal("Test skin",result.Name);Assert.Single(result.Overrides);Assert.Equal(PackageValidator.HashPath("assets/test.bin"),result.Overrides[0]);
    }
    [Fact] public void Numeric_chroma_matches_metadata_instead_of_a_formula()
    {
        var p=PackageMapper.Map(FileRecord("skins/103/103001/103052/103052.fantome"),Info,Metadata,new HashSet<string>());
        Assert.Equal(new Selection(103,103001,103052),p.Selection);
    }
    [Fact] public void Unknown_variant_remains_unavailable()
    {
        var p=PackageMapper.Map(FileRecord("skins/103/103001/103999/103999.fantome"),Info,Metadata,new HashSet<string>());
        Assert.Null(p.Selection);Assert.NotNull(p.MappingError);
    }
    [Fact] public void Form_requires_a_matching_package_and_explicit_preview()
    {
        const string path="skins/103/103001/103999/103999.fantome";
        var p=PackageMapper.Map(FileRecord(path),Info,Metadata,new HashSet<string>{"skins/103/103001/103999/103999.png"});
        Assert.Equal(new Selection(103,103001,null,"103999"),p.Selection);Assert.EndsWith("103999.png",p.Preview);
    }
    [Fact] public void Ambiguous_packages_are_not_silently_chosen()
    {
        var p=PackageMapper.Map(FileRecord("skins/103/103001/103001.fantome"),Info,Metadata,new HashSet<string>());
        var records=PackageMapper.RejectAmbiguities([p,p with{Path="second.modpkg"}]);Assert.All(records,r=>Assert.Null(r.Selection));
    }
    [Fact] public void Cross_source_conflicts_require_an_explicit_priority()
    {
        List<OverrideSource> sources=[new("repo","Repository",false,["a"]),new("custom","Custom",true,["a"])];
        Assert.Single(ConflictResolver.Find(sources));Assert.Throws<InvalidOperationException>(()=>ConflictResolver.Order(sources,ConflictChoice.Reject));
        Assert.Equal("custom",ConflictResolver.Order(sources,ConflictChoice.PreferCustom)[0].Id);Assert.Equal("repo",ConflictResolver.Order(sources,ConflictChoice.PreferRepository)[0].Id);
    }
    [Fact] public void Same_source_conflicts_need_one_package_disabled()
    {
        Assert.Throws<InvalidOperationException>(()=>ConflictResolver.Order([new("a","a",true,["x"]),new("b","b",true,["x"])],ConflictChoice.PreferCustom));
    }
    [Fact] public void Host_protocol_uses_the_verified_config_and_scanning_commands()
    {
        var lines=HostEvent.Configuration(@"C:\overlay",HookFlags.For(new AppSettings()));Assert.Equal("config loglevel 16",lines[0]);Assert.Equal("config flags 4",lines[1]);Assert.Equal("start scan",lines[3]);Assert.EndsWith(Path.DirectorySeparatorChar.ToString(),lines[2]);
        Assert.Equal("config flags 0",HostEvent.Configuration(@"C:\overlay",HookFlags.For(new AppSettings{EnforceSkinhackScan=true}))[1]);
        var e=HostEvent.Parse("status 0.001 injecting scanning for game");Assert.Equal("injecting",e?.State);Assert.Equal("scanning for game",e?.Message);
        Assert.Equal("unknown command",HostEvent.Parse("error 5.000 unknown command")?.Message);
        Assert.Throws<ArgumentException>(()=>HostEvent.Configuration("bad\nstop",0));
    }
    [Fact] public void Dll_records_split_level_from_message()
    {
        var e=HostEvent.Parse("dll 64.6055036 11776 24292 ERROR ltk_patcher_dll::verify: WAD scan failed status with c0000229 for briar.wad.client");
        Assert.Equal("dll",e?.Kind);Assert.Equal("ERROR",e?.Level);Assert.Equal("ltk_patcher_dll::verify: WAD scan failed status with c0000229 for briar.wad.client",e?.Message);
    }
    // Lines copied from a working ltk-manager 1.28.1 session with the anti-skinhack scan opted out.
    [Theory]
    [InlineData("WARN","ltk_patcher_dll::verify: AH wad scan failed c0000229 for ahri (opted out):skin0 is another skin: SkinAudioProperties.TagEventList names ahriSkin<n>",DllLineKind.Other)]
    [InlineData("INFO","ltk_patcher_dll::verify: overlay verified 21 wad(s)",DllLineKind.Other)]
    [InlineData("INFO","ltk_patcher_dll::entry: init done",DllLineKind.InitDone)]
    [InlineData("INFO","ltk_patcher_dll::hooks::fsov::imp: redirected wad: DATA/FINAL/Champions/Ahri.wad.client",DllLineKind.Redirected)]
    [InlineData("ERROR","ltk_patcher_dll::verify: WAD scan failed status with c0000229 for briar.wad.client",DllLineKind.Fatal)]
    [InlineData("ERROR","ltk_patcher_dll::verify: overlay verification failed, disabling overlay: wad data/final/champions/briar.wad.client: anti-hack scan blocked (c0000229): 21a1",DllLineKind.Fatal)]
    [InlineData("INFO","ltk_patcher_dll::entry: end of life reached, please update: 0x68a1b2c3",DllLineKind.Fatal)]
    [InlineData("WARN","ltk_patcher_dll::entry: joined too late, not overlaying",DllLineKind.Warning)]
    [InlineData("WARN","ltk_patcher_dll::verify: lazy verification failed, not overlaying: wad DATA/FINAL/Champions/Ahri.wad.client: invalid signature",DllLineKind.Warning)]
    public void Dll_lines_only_stop_the_session_for_real_blocks(string level,string message,DllLineKind expected)=>Assert.Equal(expected,DllLines.Classify(level,message).Kind);
    [Fact] public void Redirected_wads_report_the_archive_name()=>Assert.Equal("Ahri.wad.client",DllLines.Classify("INFO","redirected wad: DATA/FINAL/Champions/Ahri.wad.client").Text);
    [Theory]
    [InlineData(false,false,false,false)]
    [InlineData(true,false,false,true)]
    [InlineData(false,true,false,true)]
    [InlineData(true,true,true,false)]
    public void Elevation_matches_ltk_manager(bool optIn,bool leagueAdmin,bool appElevated,bool expected)=>Assert.Equal(expected,Elevation.Decide(optIn,leagueAdmin,appElevated));
    [Fact] public void Communitydragon_asset_paths_follow_the_documented_prefix_mapping()
    {
        Assert.Equal("https://raw.communitydragon.org/16.20/plugins/rcp-be-lol-game-data/global/default/assets/characters/ahri/a.jpg",MetadataProvider.AssetUrl("16.20","default","/lol-game-data/assets/ASSETS/Characters/Ahri/A.jpg"));
        Assert.Throws<InvalidDataException>(()=>MetadataProvider.AssetUrl("16.20","default","guess.jpg"));
    }
    [Fact] public async Task Settings_restore_selections_favorites_and_custom_enabled_state()
    {
        var paths=new AppPaths(root);var store=new StateStore(paths,NullLogger<StateStore>.Instance);await store.LoadAsync();store.State.Selections[103]=new(103,103001,103052,"special");store.State.Favorites.Add(103);
        store.State.CustomSkins.Add(new("id",103,"name","author","1","","hash","file","project",["a"],true));await store.SaveAsync();
        var restored=new StateStore(paths,NullLogger<StateStore>.Instance);await restored.LoadAsync();Assert.Equal(store.State.Selections[103],restored.State.Selections[103]);Assert.Contains(103,restored.State.Favorites);Assert.True(restored.State.CustomSkins[0].Enabled);
    }
    [Fact] public async Task Full_update_reinstalls_unchanged_files_and_unchanged_revision_downloads_nothing()
    {
        var (repo,library,installer)=await Fixture();await installer.InstallAsync(Metadata,null,CancellationToken.None);var first=library.ActiveDirectory;Assert.Equal(2,repo.Fetches);
        Assert.Equal("Already up to date.",await installer.InstallAsync(Metadata,null,CancellationToken.None));Assert.Equal(2,repo.Fetches);
        repo.Commit="b";await installer.InstallAsync(Metadata,null,CancellationToken.None);Assert.Equal(4,repo.Fetches);Assert.NotEqual(first,library.ActiveDirectory);Assert.True(Directory.Exists(first));
    }
    [Fact] public async Task Failed_update_keeps_the_active_pointer_and_previous_library()
    {
        var (repo,library,installer)=await Fixture();await installer.InstallAsync(Metadata,null,CancellationToken.None);var active=library.ActiveDirectory;
        repo.Commit="b";repo.Invalid=true;await Assert.ThrowsAsync<InvalidDataException>(()=>installer.InstallAsync(Metadata,null,CancellationToken.None));
        Assert.Equal("a",library.Manifest!.Commit);Assert.Equal(active,library.ActiveDirectory);await library.LoadAsync();Assert.Equal(active,library.ActiveDirectory);
    }
    [Fact] public async Task Cancelled_update_keeps_the_active_pointer_and_can_be_retried()
    {
        var (repo,library,installer)=await Fixture();await installer.InstallAsync(Metadata,null,CancellationToken.None);
        repo.Commit="b";using var cancel=new CancellationTokenSource();repo.AfterFetch=()=>cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>installer.InstallAsync(Metadata,null,cancel.Token));Assert.Equal("a",library.Manifest!.Commit);
        repo.AfterFetch=null;await installer.InstallAsync(Metadata,null,CancellationToken.None);Assert.Equal("b",library.Manifest!.Commit);
    }
    [Fact] public async Task Running_session_lease_blocks_library_mutation()
    {
        var gate=new OperationGate();using var lease=await gate.EnterAsync(CancellationToken.None);await Assert.ThrowsAsync<InvalidOperationException>(()=>gate.EnterAsync(CancellationToken.None));Assert.True(gate.IsBusy);
    }
    private PackageValidator Validator()=>new(new NativeBridge(NullLogger<NativeBridge>.Instance,"missing-native"));
    private async Task<(FakeRepository,LibraryStore,LibraryInstaller)> Fixture()
    {
        var paths=new AppPaths(root);var store=new StateStore(paths,NullLogger<StateStore>.Instance);await store.LoadAsync();var library=new LibraryStore(store);var repo=new FakeRepository(CreatePackage(root,"source.fantome"));
        return(repo,library,new(repo,library,Validator(),new(),NullLogger<LibraryInstaller>.Instance));
    }
    private static string CreatePackage(string directory,string filename)
    {
        Directory.CreateDirectory(directory);var file=Path.Combine(directory,filename);using var zip=ZipFile.Open(file,ZipArchiveMode.Create);
        using(var writer=new StreamWriter(zip.CreateEntry("META/info.json").Open()))writer.Write("{\"Name\":\"Test skin\",\"Author\":\"Tester\",\"Version\":\"1\"}");
        using(var writer=new StreamWriter(zip.CreateEntry("RAW/assets/test.bin").Open()))writer.Write("content");return file;
    }
    private sealed class FakeRepository(string source):IRepositorySource
    {
        public string Commit="a";public int Fetches;public bool Invalid;public Action? AfterFetch;
        public Task<RepositoryRevision> ResolveAsync(CancellationToken ct)=>Task.FromResult(new RepositoryRevision("main",Commit,[FileRecord("skins/103/103001/103001.fantome"),FileRecord("skins/103/103001/103052/103052.fantome")]));
        public async Task FetchAsync(RepositoryRevision revision,RepositoryFile file,string destination,Action<long> transferred,CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);Interlocked.Increment(ref Fetches);
            if(Invalid)await System.IO.File.WriteAllTextAsync(destination,"invalid archive",ct);else System.IO.File.Copy(source,destination);
            transferred(new FileInfo(destination).Length);AfterFetch?.Invoke();ct.ThrowIfCancellationRequested();
        }
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
