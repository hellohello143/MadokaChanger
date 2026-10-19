using System.Diagnostics;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MadokaChanger.Core;

// Explicit diagnostic tool. It writes only to the supplied root and never launches a game.
if(args.Length<3) {Console.WriteLine("MadokaChanger.Verify <catalog|install|custom|overlay|host> <data-root> <native-exe> [game-or-backend-folder]");return 1;}
var mode=args[0];var paths=new AppPaths(Path.GetFullPath(args[1]));
using var logs=LoggerFactory.Create(b=>b.AddSimpleConsole(o=>o.SingleLine=true).SetMinimumLevel(LogLevel.Warning));
using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(10)};
var remote=new RemoteClient(http,logs.CreateLogger<RemoteClient>());
var metadata=new MetadataProvider(remote,paths,logs.CreateLogger<MetadataProvider>());
var state=new StateStore(paths,logs.CreateLogger<StateStore>());await state.LoadAsync();
var native=new NativeBridge(logs.CreateLogger<NativeBridge>(),Path.GetFullPath(args[2]));
var validator=new PackageValidator(native);var gate=new OperationGate();var library=new LibraryStore(state);await library.LoadAsync();
using var cancellation=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
try
{
    var catalog=await metadata.LoadAsync("default",progress:new Progress<string>(s=>{if(s.Contains("/180")||s.EndsWith("0"))Console.WriteLine(s);}),ct:cancellation.Token);
    Console.WriteLine($"CATALOG {catalog.Patch}: {catalog.Champions.Count} champions, {catalog.Champions.Sum(c=>c.Skins.Count)} skins");
    if(mode=="catalog")
    {
        var cache=new ImageCache(remote,paths);var ahri=catalog.Champions.First(c=>c.Id==103);var skin=ahri.Skins.First(s=>s.Id==103001);
        foreach(var url in new[]{ahri.Portrait,skin.Splash,skin.Tile,skin.Chromas.First().Preview})if(await cache.GetAsync(url,cancellation.Token) is not {} file||!File.Exists(file))throw new Exception("Image failed: "+url);
        Console.WriteLine("PASS portrait, splash, tile, chroma preview");
    }
    else if(mode=="install")
    {
        var installer=new LibraryInstaller(new GitHubRepository(remote),library,validator,gate,logs.CreateLogger<LibraryInstaller>());
        var last=DateTime.UtcNow;
        var progress=new Progress<TransferProgress>(p=>{if(DateTime.UtcNow-last>TimeSpan.FromSeconds(10)||p.Completed==p.Total){Console.WriteLine(p.Display);last=DateTime.UtcNow;}});
        Console.WriteLine(await installer.InstallAsync(catalog,progress,cancellation.Token));
        Console.WriteLine($"MAP: {library.Manifest!.Packages.Count(p=>p.Selection is not null)} mapped; {library.Manifest.Packages.Count(p=>p.MappingError is not null)} retained but unavailable");
        await JsonFile.WriteAsync(Path.Combine(paths.Root,"mapping-unavailable.json"),library.Manifest.Packages.Where(p=>p.MappingError is not null).Select(p=>new{p.Path,p.MappingError}));
        Console.WriteLine(await installer.InstallAsync(catalog,progress,cancellation.Token));
    }
    else if(mode=="custom")
    {
        var file=Path.Combine(paths.Temporary,"sample.fantome");await remote.DownloadAsync("https://raw.githubusercontent.com/Alban1911/LeagueSkins/40573673466dd3fc250a9c2a8b945514ca119fe6/skins/103/103001/103001.fantome",file,cancellation.Token);
        var custom=new CustomSkinService(paths,state,validator,native,gate);var skin=await custom.ImportAsync(file,103,catalog,cancellation.Token);
        if(skin.Overrides.Count==0)throw new Exception("Missing overrides");
        try {await custom.ImportAsync(file,103,catalog,cancellation.Token);throw new Exception("Duplicate was accepted");}catch(InvalidOperationException e) when(e.Message.Contains("already imported")){Console.WriteLine("PASS duplicate detection");}
        await custom.SetEnabledAsync(skin,true,cancellation.Token);await state.LoadAsync();if(!state.State.CustomSkins.Single(c=>c.Id==skin.Id).Enabled)throw new Exception("Enabled state did not persist");
        var project=skin.ProjectPath;await custom.SetEnabledAsync(skin,false,cancellation.Token);await custom.UninstallAsync(skin,cancellation.Token);if(Directory.Exists(project))throw new Exception("Uninstall left project behind");
        Console.WriteLine("PASS real Fantome import, champion assignment, enable/disable, persistence, uninstall");
    }
    else if(mode=="overlay")
    {
        if(args.Length<4)throw new Exception("Supply the League Game folder");
        if(GameLocator.IsGameOpen())throw new Exception("Close the game before this verification");
        var p=library.Find(new Selection(103,103001)).Single();var project=Path.Combine(paths.Temporary,"overlay-test-project-"+Guid.NewGuid().ToString("N"));
        using(var r=await native.InvokeAsync(new{operation="import",package=library.GetPackagePath(p),output=project},cancellation.Token)){ }
        var folder=Path.Combine(paths.Overlay,"verify-"+Guid.NewGuid().ToString("N"));
        using var result=await native.InvokeAsync(new{operation="build",game=GameLocator.Normalize(args[3]),overlay=Path.Combine(folder,"overlay"),state=Path.Combine(folder,"state"),mods=new[]{new{id="ahri",path=project}}},cancellation.Token,new Progress<string>(Console.WriteLine));
        if(result.RootElement.GetProperty("wads").GetInt32()==0)throw new Exception("No WADs built");Console.WriteLine("PASS overlay built from real game assets: "+result.RootElement);
    }
    else if(mode=="native")
    {
        var fixture=Path.Combine(paths.Temporary,"native-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var project=Path.Combine(fixture,"project");Directory.CreateDirectory(Path.Combine(project,"content","base","Ahri.wad.client","data"));
        await File.WriteAllTextAsync(Path.Combine(project,"mod.config.json"),"""{"name":"verify-ahri","display_name":"Verification Ahri","version":"1.0.0","description":"Synthetic fixture","authors":["MadokaChanger verification"],"champions":["Ahri"],"layers":[{"name":"base","priority":0,"description":"Base layer"}]}""");
        await File.WriteAllTextAsync(Path.Combine(project,"content","base","Ahri.wad.client","data","test.bin"),"modified fixture data");
        var package=Path.Combine(fixture,"fixture.modpkg");using(var packed=await native.InvokeAsync(new{operation="pack",project,output=package},cancellation.Token)){ }
        var info=await validator.InspectAsync(package,cancellation.Token);if(info.Overrides.Count!=1)throw new Exception("modpkg override mapping failed");
        var corrupted=Path.Combine(fixture,"corrupted.modpkg");var corruptedBytes=await File.ReadAllBytesAsync(package);corruptedBytes[^1]^=0x40;await File.WriteAllBytesAsync(corrupted,corruptedBytes);
        try{await validator.InspectAsync(corrupted,cancellation.Token);throw new Exception("Corrupt modpkg was accepted");}catch(InvalidDataException){Console.WriteLine("PASS corrupt modpkg rejected");}
        var custom=new CustomSkinService(paths,state,validator,native,gate);var imported=await custom.ImportAsync(package,0,catalog,cancellation.Token);
        if(imported.ChampionId!=103)throw new Exception("modpkg champion metadata was not used");await custom.UninstallAsync(imported,cancellation.Token);
        Console.WriteLine("PASS real upstream modpkg packing, checksums, metadata association, import and uninstall");
        var game=Path.Combine(fixture,"game");var original=Path.Combine(game,"DATA","FINAL","Champions","Ahri.wad.client");Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        await using(var file=File.Create(original))
        using(var w=new BinaryWriter(file))
        {
            var data=System.Text.Encoding.UTF8.GetBytes("original fixture data");w.Write((byte)'R');w.Write((byte)'W');w.Write((byte)3);w.Write((byte)1);w.Write(new byte[256]);w.Write(0UL);w.Write(1);
            w.Write(XxHash64.HashToUInt64("data/test.bin"u8));w.Write(304u);w.Write(data.Length);w.Write(data.Length);w.Write((byte)0);w.Write((byte)0);w.Write((ushort)0);w.Write(XxHash3.HashToUInt64(data));w.Write(data);
        }
        var before=SHA256.HashData(await File.ReadAllBytesAsync(original));
        using var build=await native.InvokeAsync(new{operation="build",game,overlay=Path.Combine(fixture,"overlay"),state=Path.Combine(fixture,"state"),mods=new[]{new{id="fixture",path=project}}},cancellation.Token);
        if(build.RootElement.GetProperty("wads").GetInt32()!=1)throw new Exception("Fixture overlay was not built");
        var after=SHA256.HashData(await File.ReadAllBytesAsync(original));
        if(!before.SequenceEqual(after))throw new Exception("Original fixture WAD changed");
        Console.WriteLine("PASS native overlay builder against synthetic WAD; original fixture preserved. No game process or game files touched.");
    }
    else if(mode=="host")
    {
        if(args.Length<4)throw new Exception("Supply the official LTK resource folder");
        // No start scan command in this smoke test: it checks the host transport without attaching to a game.
        using var process=new Process{StartInfo=new(Path.Combine(args[3],"ltk_patcher_host.exe")){WorkingDirectory=args[3],UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        process.Start();var errors=process.StandardError.ReadToEndAsync();
        foreach(var line in new[]{"config loglevel 16","config flags 0","config prefix "+paths.Overlay+Path.DirectorySeparatorChar,"stop"})await process.StandardInput.WriteLineAsync(line);
        process.StandardInput.Close();var output=process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var text=await output;Console.WriteLine(text);Console.WriteLine(await errors);if(process.ExitCode!=0||!text.Contains("ok "))throw new Exception("Host transport failed");Console.WriteLine("PASS host launch, configuration acknowledgements and shutdown (no game attachment)");
    }
    else throw new Exception("Unknown mode");
    return 0;
}
catch(Exception e){Console.Error.WriteLine(e);return 1;}

