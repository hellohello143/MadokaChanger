using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using MadokaChanger.Core;
using Xunit;

namespace MadokaChanger.Tests;

public sealed class ClassicRemovalTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"MadokaChanger-classic-tests-"+Guid.NewGuid().ToString("N"));
    private static Catalog Catalog=>new("16.20","default",DateTimeOffset.UtcNow,
        [new(103,"Ahri","Ahri","","",[new(103001,"Dynasty Ahri",false,"","","",[])]),new(60103,"Ahri","Jade_Ahri","","",[])]);
    [Fact] public async Task Cached_catalog_excludes_classic_without_a_network_refresh()
    {
        var paths=new AppPaths(root);await JsonFile.WriteAsync(Path.Combine(paths.Metadata,"catalog-default.json"),Catalog);
        using var http=new HttpClient(new NoNetwork());var remote=new RemoteClient(http,NullLogger<RemoteClient>.Instance);
        var provider=new MetadataProvider(remote,paths,NullLogger<MetadataProvider>.Instance);
        var catalog=await provider.LoadAsync("default");Assert.Single(catalog.Champions);Assert.Equal(103,catalog.Champions[0].Id);Assert.Contains(60103,provider.ExcludedChampionIds);
    }
    [Fact] public async Task Previously_installed_library_hides_classic_packages_at_the_same_revision()
    {
        var paths=new AppPaths(root);var state=new StateStore(paths,NullLogger<StateStore>.Instance);await state.LoadAsync();var store=new LibraryStore(state);
        var dir=Path.Combine(store.Root,"versions","old");Directory.CreateDirectory(dir);
        var manifest=new LibraryManifest("same-sha","main",DateTimeOffset.UtcNow,"16.20",
            [Record("skins/103/103001/103001.fantome",103),Record("classic/60103/103001/103001.fantome",60103)],
            [new("skins/103/103001/103001.fantome","a",1),new("classic/60103/103001/103001.fantome","b",1)]);
        await JsonFile.WriteAsync(Path.Combine(dir,"manifest.json"),manifest);
        await JsonFile.WriteAsync(Path.Combine(store.Root,"active.json"),new LibraryPointer("versions/old","same-sha"));
        await store.LoadAsync();Assert.Single(store.Manifest!.Packages);Assert.Single(store.Manifest.Files);Assert.Empty(store.Find(new(60103,103001)));
        Assert.True(File.Exists(Path.Combine(dir,"manifest.json"))); // Loading never deletes an existing generation.
    }
    [Fact] public async Task Full_library_install_never_fetches_classic_files()
    {
        var paths=new AppPaths(root);var state=new StateStore(paths,NullLogger<StateStore>.Instance);await state.LoadAsync();var store=new LibraryStore(state);
        var package=Path.Combine(root,"fixture.fantome");using(var zip=ZipFile.Open(package,ZipArchiveMode.Create))
        {
            using(var w=new StreamWriter(zip.CreateEntry("META/info.json").Open()))w.Write("{\"Name\":\"Fixture\",\"Author\":\"Tester\",\"Version\":\"1\"}");
            using(var w=new StreamWriter(zip.CreateEntry("RAW/data/test.bin").Open()))w.Write("fixture");
        }
        var source=new FixtureRepository(package);
        var installer=new LibraryInstaller(source,store,new(new NativeBridge(NullLogger<NativeBridge>.Instance,"missing")),new(),NullLogger<LibraryInstaller>.Instance);
        await installer.InstallAsync(CatalogPolicy.WithoutClassic(Catalog),null,CancellationToken.None);
        Assert.Equal(1,source.Downloads);Assert.Single(store.Manifest!.Packages);Assert.DoesNotContain(store.Manifest.Files,f=>CatalogPolicy.IsClassicRepositoryPath(f.Path));
    }
    private static PackageRecord Record(string path,int champion)=>new(path,"sha",1,"Fixture","Tester","1",champion,103001,null,null,null,[],null);
    private sealed class NoNetwork:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>throw new InvalidOperationException("A cached catalog should not use the network.");
    }
    private sealed class FixtureRepository(string package):IRepositorySource
    {
        public int Downloads;
        public Task<RepositoryRevision> ResolveAsync(CancellationToken ct)=>Task.FromResult(new RepositoryRevision("main",new string('a',40),
            [new("skins/103/103001/103001.fantome","a",1),new("classic/60103/103001/103001.fantome","b",1)]));
        public Task FetchAsync(RepositoryRevision revision,RepositoryFile file,string destination,Action<long> transferred,CancellationToken ct)
        {
            Assert.False(CatalogPolicy.IsClassicRepositoryPath(file.Path));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(package,destination);Downloads++;transferred(new FileInfo(destination).Length);return Task.CompletedTask;
        }
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
