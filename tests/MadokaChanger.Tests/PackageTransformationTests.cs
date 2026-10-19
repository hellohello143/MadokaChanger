using System.IO.Compression;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MadokaChanger.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MessagePack;
using ZstdSharp;

namespace MadokaChanger.Tests;

public sealed class PackageTransformationTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"MadokaChanger-transform-tests-"+Guid.NewGuid().ToString("N"));
    private const string BasePath="skins/103/103001/103001.fantome";
    private const string ChromaPath="skins/103/103001/103052/103052.zip";
    private const string FormPath="skins/103/103001/special/special.fantome";
    private static Catalog Metadata=>new("16.20","default",DateTimeOffset.UtcNow,
        [new(103,"Ahri","Ahri","original champion","portrait",[new(103001,"Dynasty Ahri",false,"splash","tile","description",[new(103052,"Ahri-versary","chroma-preview",["#abcdef"])])])]);
    private static PackageValidator Validator(string? native=null)=>new(new(NullLogger<NativeBridge>.Instance,native??"missing-native"));

    [Theory]
    [InlineData(".fantome")][InlineData(".zip")][InlineData(".ZIP")]
    public async Task Rewritten_fantome_preserves_all_payload_bytes_paths_metadata_and_overrides(string extension)
    {
        var generation=Path.Combine(root,"stage");var path="skins/103/103001/103001"+extension;
        var source=ArchiveSafety.SafeDestination(generation,path);CreateFantome(source);
        var validator=Validator();var original=await validator.InspectAsync(source,CancellationToken.None);
        var record=PackageMapper.Map(Source(path,File.ReadAllBytes(source)),original,Metadata,new HashSet<string>());
        var entries=ZipContents(source);
        var transformed=await new PackageTransformer(validator).TransformAsync(generation,record,CancellationToken.None);
        var actual=ArchiveSafety.SafeDestination(generation,transformed.StoragePath!);
        var inspection=await validator.InspectAsync(actual,CancellationToken.None);
        Assert.False(File.Exists(source));Assert.EndsWith(extension,actual);
        Assert.Matches("^p[0-9a-f]{32}$",Path.GetFileNameWithoutExtension(actual));
        Assert.Equal(Path.GetFileNameWithoutExtension(actual),inspection.Name);Assert.NotEqual(original.Name,inspection.Name);
        Assert.Equal(record.Name,transformed.Name);Assert.Equal(record.Author,transformed.Author);Assert.Equal(record.Version,transformed.Version);
        Assert.Equal(record.Description,transformed.Description);Assert.Equal(record.Selection,transformed.Selection);
        Assert.Equal(record.Sha,transformed.Sha);Assert.Equal(record.Size,transformed.Size);
        Assert.Equal(Sha256(File.ReadAllBytes(actual)),transformed.LocalSha256);Assert.Equal(new FileInfo(actual).Length,transformed.LocalSize);
        Assert.True(original.Overrides.ToHashSet().SetEquals(inspection.Overrides));
        var rewritten=ZipContents(actual);Assert.Equal(entries.Keys.Order(),rewritten.Keys.Order());
        foreach(var (name,bytes) in entries.Where(e=>e.Key!="META/info.json")) Assert.Equal(bytes,rewritten[name]);
        var expected=JsonNode.Parse(entries["META/info.json"])!.AsObject();expected["Name"]=inspection.Name;
        Assert.True(JsonNode.DeepEquals(expected,JsonNode.Parse(rewritten["META/info.json"])));
        Assert.Equal(original.Name,transformed.OriginalMetadata!.Value.GetProperty("Name").GetString());
        Assert.Empty(Directory.EnumerateDirectories(generation,".transform-*"));
    }

    [Fact] public async Task Install_retains_frontend_identity_previews_favorites_and_saved_variant_mappings_after_restart()
    {
        var (state,library,repo,installer)=await Fixture();
        var selections=new[]{new Selection(103,103001),new Selection(103,103001,103052),new Selection(103,103001,null,"special")};
        state.State.Favorites.Add(103);state.State.Selections[103]=selections[2];await state.SaveAsync();
        var metadata=Metadata;var catalogJson=JsonSerializer.Serialize(metadata,JsonFile.Options);
        var phases=new List<string>();
        await installer.InstallAsync(metadata,new InlineProgress(p=>{lock(phases) phases.Add(p.Phase);}),CancellationToken.None);
        Assert.True(PackageTransformer.IsCurrent(library.Manifest!));
        Assert.Equal(repo.Files.Count,repo.Fetches);
        foreach(var selection in selections)
        {
            var record=Assert.Single(library.Find(selection));
            Assert.Equal("Original skin",record.Name);Assert.Equal("Creator",record.Author);Assert.Equal("1.2.3",record.Version);
            Assert.True(File.Exists(library.GetPackagePath(record)));Assert.False(File.Exists(ArchiveSafety.SafeDestination(library.ActiveDirectory!,record.Path)));
        }
        var form=library.Find(selections[2]).Single();Assert.Equal("skins/103/103001/special/special.png",form.Preview);
        Assert.Equal("preview",File.ReadAllText(ArchiveSafety.SafeDestination(library.ActiveDirectory!,form.Preview!)));
        var oldRecord=form with{StoragePath=null};Assert.Equal(library.GetPackagePath(form),library.GetPackagePath(oldRecord));
        var active=library.ActiveDirectory;var storage=library.Manifest!.Packages.Select(p=>p.StoragePath).ToArray();
        var restored=new LibraryStore(state);await restored.LoadAsync();await state.LoadAsync();
        Assert.Equal(active,restored.ActiveDirectory);Assert.Equal(storage,restored.Manifest!.Packages.Select(p=>p.StoragePath));
        Assert.Contains(103,state.State.Favorites);Assert.Equal(selections[2],state.State.Selections[103]);
        Assert.Equal(catalogJson,JsonSerializer.Serialize(metadata,JsonFile.Options));
        Assert.Equal("Already up to date.",await installer.InstallAsync(Metadata,null,CancellationToken.None));
        Assert.Equal(repo.Files.Count,repo.Fetches);Assert.Equal(active,library.ActiveDirectory);
        Assert.Contains("Obfuscating local package storage",phases);
        var packageFiles=Directory.EnumerateFiles(active!,"*",SearchOption.AllDirectories).Where(GitHubRepository.IsPackage).ToList();
        Assert.Equal(library.Manifest.Packages.Count,packageFiles.Count);
    }

    [Fact] public async Task Source_blob_and_lfs_integrity_remain_separate_from_final_local_sha256_and_size()
    {
        var (_,library,repo,installer)=await Fixture();
        var payload=repo.Content[BasePath];var oid=Sha256(payload);
        var pointer=Encoding.UTF8.GetBytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize {payload.Length}\n");
        var source=Source(BasePath,pointer) with{LfsOid=oid,LfsSize=payload.Length};repo.Files[0]=source;
        await installer.InstallAsync(Metadata,null,CancellationToken.None);
        var package=library.Manifest!.Packages.Single(p=>p.Path==BasePath);
        Assert.Equal(source,library.Manifest.Files.Single(f=>f.Path==BasePath));
        Assert.Equal(source.Sha,package.Sha);Assert.Equal(pointer.Length,package.Size);
        Assert.Equal(oid,package.LfsOid);Assert.Equal(payload.Length,package.LfsSize);
        Assert.Equal(Sha256(File.ReadAllBytes(library.GetPackagePath(package))),package.LocalSha256);
        Assert.NotEqual(oid,package.LocalSha256);Assert.NotEqual(source.Sha,package.LocalSha256);
        Assert.NotEqual(package.Size,package.LocalSize);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Legacy_migration_is_staged_and_runs_at_the_same_commit_or_offline_startup(bool startup)
    {
        var (state,library,repo,installer)=await Fixture();var legacy=await ActivateLegacy(library,repo);
        var oldPointer=File.ReadAllBytes(Path.Combine(library.Root,"active.json"));
        // New metadata must not remap a migration's saved forms or rename its original display metadata.
        if(startup) {repo.Offline=true;Assert.NotNull(await installer.MigrateAsync(null,CancellationToken.None));}
        else await installer.InstallAsync(Metadata with{Champions=[]},null,CancellationToken.None);
        Assert.Equal(0,repo.Fetches);Assert.Equal(startup?0:1,repo.Resolves);
        Assert.NotEqual(legacy,library.ActiveDirectory);Assert.True(Directory.Exists(legacy));
        Assert.NotEqual(oldPointer,File.ReadAllBytes(Path.Combine(library.Root,"active.json")));
        Assert.All(library.Manifest!.Packages,p=>Assert.NotNull(p.Selection));
        Assert.All(library.Manifest.Packages,p=>Assert.Equal("Original skin",p.Name));
        var active=library.ActiveDirectory;var bytes=File.ReadAllBytes(Path.Combine(active!,"manifest.json"));
        var restored=new LibraryStore(state);await restored.LoadAsync();
        var again=new LibraryInstaller(repo,restored,Validator(),new(),NullLogger<LibraryInstaller>.Instance);
        Assert.Null(await again.MigrateAsync(null,CancellationToken.None));
        Assert.Equal(active,restored.ActiveDirectory);Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(active!,"manifest.json")));
        Assert.All(repo.Files,f=>Assert.True(File.Exists(ArchiveSafety.SafeDestination(legacy,f.Path))));
    }

    [Fact] public async Task Legacy_lfs_without_pointer_metadata_is_fetched_at_its_pinned_commit_before_migration()
    {
        var (_,library,repo,installer)=await Fixture();var payload=repo.Content[BasePath];
        var pointer=Encoding.UTF8.GetBytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{Sha256(payload)}\nsize {payload.Length}\n");
        repo.Files[0]=Source(BasePath,pointer);repo.MaterializedLfs[BasePath]=(Sha256(payload),payload.Length);
        await ActivateLegacy(library,repo);repo.Commit="new-upstream-commit";
        await installer.MigrateAsync(null,CancellationToken.None);
        Assert.Equal(1,repo.Fetches);Assert.Equal(0,repo.Resolves);Assert.All(repo.FetchedCommits,c=>Assert.Equal("commit-a",c));
        Assert.Equal("commit-a",library.Manifest!.Commit);Assert.Equal(Sha256(payload),library.Manifest.Packages.Single(p=>p.Path==BasePath).LfsOid);
    }

    [Theory]
    [InlineData(false,0)][InlineData(false,1)][InlineData(true,0)][InlineData(true,1)]
    public async Task Cancellation_during_transformation_preserves_active_generation_and_cleans_the_stage(bool migration,int completed)
    {
        var (_,library,repo,installer)=await Fixture();
        if(migration) await ActivateLegacy(library,repo);else {await installer.InstallAsync(Metadata,null,CancellationToken.None);repo.Commit="commit-b";}
        var active=library.ActiveDirectory;var pointer=File.ReadAllBytes(Path.Combine(library.Root,"active.json"));
        var files=Directory.EnumerateDirectories(Path.Combine(library.Root,"versions")).Order().ToArray();
        using var cancel=new CancellationTokenSource();
        var progress=new InlineProgress(p=>{if(p.Phase=="Obfuscating local package storage"&&p.Completed==completed)cancel.Cancel();});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>
        {
            if(migration)await installer.MigrateAsync(progress,cancel.Token);else await installer.InstallAsync(Metadata,progress,cancel.Token);
        });
        Assert.Equal(active,library.ActiveDirectory);Assert.Equal(pointer,File.ReadAllBytes(Path.Combine(library.Root,"active.json")));
        Assert.Equal(files,Directory.EnumerateDirectories(Path.Combine(library.Root,"versions")).Order());
        await library.LoadAsync();Assert.Equal(active,library.ActiveDirectory);
        if(migration) await installer.MigrateAsync(null,CancellationToken.None);else await installer.InstallAsync(Metadata,null,CancellationToken.None);
        Assert.True(PackageTransformer.IsCurrent(library.Manifest!));
    }

    [Fact] public async Task Revalidation_override_mismatch_and_invalid_zip_never_replace_previous_generation()
    {
        var (_,library,repo,installer)=await Fixture();var active=await ActivateLegacy(library,repo);
        var bad=library.Manifest! with{Packages=library.Manifest!.Packages.Select(p=>p with{Overrides=["wrong"]}).ToList()};
        await library.ActivateAsync(active,bad,CancellationToken.None);
        var pointer=File.ReadAllBytes(Path.Combine(library.Root,"active.json"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>installer.MigrateAsync(null,CancellationToken.None));
        Assert.Equal(active,library.ActiveDirectory);Assert.Equal(pointer,File.ReadAllBytes(Path.Combine(library.Root,"active.json")));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(library.Root,"versions")));
        repo.Commit="commit-b";repo.Content[BasePath]="not a package"u8.ToArray();repo.Files[0]=Source(BasePath,repo.Content[BasePath]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>installer.InstallAsync(Metadata,null,CancellationToken.None));
        Assert.Equal(active,library.ActiveDirectory);Assert.Single(Directory.EnumerateDirectories(Path.Combine(library.Root,"versions")));
    }

    [Fact] public async Task Override_changes_are_rejected_by_the_transformer_and_temporary_files_are_removed()
    {
        var path=ArchiveSafety.SafeDestination(root,BasePath);CreateFantome(path);
        var inspection=await Validator().InspectAsync(path,CancellationToken.None);
        var record=PackageMapper.Map(Source(BasePath,File.ReadAllBytes(path)),inspection,Metadata,new HashSet<string>()) with{Overrides=["wrong"]};
        await Assert.ThrowsAsync<InvalidDataException>(()=>new PackageTransformer(Validator()).TransformAsync(root,record,CancellationToken.None));
        Assert.True(File.Exists(path));Assert.Empty(Directory.EnumerateDirectories(root,".transform-*"));
    }

    [Fact] public async Task Upstream_integrity_failure_prevents_any_rewriting_or_activation()
    {
        var (_,library,repo,installer)=await Fixture();repo.Files[0]=repo.Files[0] with{Sha=new string('0',40)};
        var phases=new List<string>();
        await Assert.ThrowsAsync<InvalidDataException>(()=>installer.InstallAsync(Metadata,new InlineProgress(p=>{lock(phases)phases.Add(p.Phase);}),CancellationToken.None));
        Assert.DoesNotContain("Obfuscating local package storage",phases);Assert.Null(library.Manifest);
        Assert.False(File.Exists(Path.Combine(library.Root,"active.json")));Assert.Empty(Directory.EnumerateDirectories(Path.Combine(library.Root,"versions")));
    }

    [Fact] public async Task Failed_pointer_commit_rolls_back_after_all_packages_and_staged_manifest_are_complete()
    {
        var (_,library,repo,installer)=await Fixture();await installer.InstallAsync(Metadata,null,CancellationToken.None);
        var active=library.ActiveDirectory;var pointer=Path.Combine(library.Root,"active.json");var bytes=File.ReadAllBytes(pointer);repo.Commit="commit-b";
        using(var locked=new FileStream(pointer,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var error=await Record.ExceptionAsync(()=>installer.InstallAsync(Metadata,null,CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(active,library.ActiveDirectory);Assert.Equal(bytes,File.ReadAllBytes(pointer));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(library.Root,"versions")));
        Assert.Empty(Directory.EnumerateFiles(library.Root,"*.tmp",SearchOption.AllDirectories));
    }

    [Fact] public async Task Migration_and_install_hold_the_operation_gate_through_transformation()
    {
        var (_,library,repo,_)=await Fixture();await ActivateLegacy(library,repo);var gate=new OperationGate();
        var installer=new LibraryInstaller(repo,library,Validator(),gate,NullLogger<LibraryInstaller>.Instance);
        var observed=false;
        await installer.MigrateAsync(new InlineProgress(p=>{if(p.Phase=="Obfuscating local package storage") {Assert.True(gate.IsBusy);observed=true;}}),CancellationToken.None);
        Assert.True(observed);Assert.False(gate.IsBusy);
        using var lease=await gate.EnterAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>installer.InstallAsync(Metadata,null,CancellationToken.None));
    }

    [NativePackageFact] public async Task Native_modpkg_round_trip_preserves_payloads_layers_credits_license_and_selection()
    {
        var native=new NativeBridge(NullLogger<NativeBridge>.Instance,Environment.GetEnvironmentVariable("MADOKACHANGER_TEST_NATIVE"));
        using(var version=await native.InvokeAsync(new{operation="version"},CancellationToken.None))
            Assert.Equal("acaf19e5e1e32ff11376f0af1bccfbaecc28026d",version.RootElement.GetProperty("league_mod").GetString());
        var validator=new PackageValidator(native);var generation=Path.Combine(root,"stage");
        var project=Path.Combine(root,"project");var content=Path.Combine(project,"content","base","Ahri.wad.client","data");Directory.CreateDirectory(content);
        await File.WriteAllBytesAsync(Path.Combine(content,"test.bin"),Enumerable.Range(0,4096).Select(i=>(byte)i).ToArray());
        await File.WriteAllTextAsync(Path.Combine(project,"README.md"),"Creator attribution and usage information");
        await File.WriteAllTextAsync(Path.Combine(project,"LICENSE"),"Custom license bytes\nCopyright Creator\n");
        await File.WriteAllTextAsync(Path.Combine(project,"mod.config.json"),"""
            {"name":"original-skin","display_name":"Original skin","version":"1.2.3","description":"Credits to Creator",
             "authors":[{"name":"Creator","role":"Artist"}],"license":{"name":"Creator terms"},"champions":["Ahri"],
             "layers":[{"name":"base","priority":0,"description":"Required layer","string_overrides":{"en_us":{"key":"Original game text"}}}]}
            """);
        const string path="skins/103/103001/103001.modpkg";var package=ArchiveSafety.SafeDestination(generation,path);Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        using(var result=await native.InvokeAsync(new{operation="pack",project,output=package},CancellationToken.None)) { }
        var inspection=await validator.InspectAsync(package,CancellationToken.None);
        var original=PackageMapper.Map(Source(path,File.ReadAllBytes(package)),inspection,Metadata,new HashSet<string>());
        var sourceProject=Path.Combine(root,"before");using(var result=await native.InvokeAsync(new{operation="import",package,output=sourceProject},CancellationToken.None)) { }
        var record=await new PackageTransformer(validator).TransformAsync(generation,original,CancellationToken.None);
        var final=ArchiveSafety.SafeDestination(generation,record.StoragePath!);
        var after=Path.Combine(root,"after");using(var result=await native.InvokeAsync(new{operation="import",package=final,output=after},CancellationToken.None)) { }
        var check=await validator.InspectAsync(final,CancellationToken.None);
        Assert.Equal(original.Name,record.Name);Assert.Equal("Creator",record.Author);Assert.Equal(original.Selection,record.Selection);
        Assert.Equal(original.Version,check.Version);Assert.Equal(original.Author,check.Author);Assert.Equal(original.Description,check.Description);
        Assert.Equal(original.Champions,check.Champions);Assert.True(inspection.Overrides.ToHashSet().SetEquals(check.Overrides));
        foreach(var file in Directory.EnumerateFiles(sourceProject,"*",SearchOption.AllDirectories).Where(p=>Path.GetFileName(p)!="mod.config.json"))
            Assert.Equal(File.ReadAllBytes(file),File.ReadAllBytes(Path.Combine(after,Path.GetRelativePath(sourceProject,file))));
        Assert.Equal(Sha256(File.ReadAllBytes(final)),record.LocalSha256);
    }

    [NativePackageFact] public async Task Modpkg_format_rewrite_preserves_raw_unknown_metadata_distributor_schema_and_compressed_payloads()
    {
        var native=Environment.GetEnvironmentVariable("MADOKACHANGER_TEST_NATIVE");var validator=Validator(native);
        foreach(var (compressed,metadataLast) in new[]{(false,false),(true,false),(false,true),(true,true)})
        {
            var stage=Path.Combine(root,$"{compressed}-{metadataLast}");const string path="skins/103/103001/103001.modpkg";
            var source=ArchiveSafety.SafeDestination(stage,path);var fixture=CreateModpkg(source,compressed,metadataLast:metadataLast);
            var inspection=await validator.InspectAsync(source,CancellationToken.None);
            var record=PackageMapper.Map(Source(path,File.ReadAllBytes(source)),inspection,Metadata,new HashSet<string>());
            var rewritten=await new PackageTransformer(validator).TransformAsync(stage,record,CancellationToken.None);
            var output=File.ReadAllBytes(ArchiveSafety.SafeDestination(stage,rewritten.StoragePath!));
            var storedSize=(int)BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(fixture.Toc+17));
            var bytes=output.AsSpan(fixture.Data,storedSize).ToArray();
            if(compressed){using var decoder=new Decompressor();bytes=decoder.Unwrap(bytes).ToArray();}
            var originalFields=MetadataFields(fixture.Metadata);var finalFields=MetadataFields(bytes);
            Assert.Equal(originalFields.Keys,finalFields.Keys);
            foreach(var (key,value) in originalFields.Where(p=>p.Key is not ("name" or "display_name")))Assert.Equal(value,finalFields[key]);
            var payloadOffset=(int)BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(fixture.Toc+61+8));
            Assert.Equal(fixture.Payload,output.AsSpan(payloadOffset,fixture.Payload.Length).ToArray());
            Assert.Equal(inspection.Name,rewritten.Name);Assert.Equal("Creator",rewritten.Author);
            Assert.True(inspection.Overrides.ToHashSet().SetEquals((await validator.InspectAsync(ArchiveSafety.SafeDestination(stage,rewritten.StoragePath!),CancellationToken.None)).Overrides));
        }
    }

    [NativePackageFact] public async Task Signed_modpkg_is_refused_transactionally_after_source_validation()
    {
        var (_,library,repo,_)=await Fixture();var old=await ActivateLegacy(library,repo);var pointer=File.ReadAllBytes(Path.Combine(library.Root,"active.json"));
        var file=Path.Combine(root,"signed.modpkg");CreateModpkg(file,false,signed:true);const string path="skins/103/103001/103001.modpkg";
        repo.Files.Clear();repo.Content.Clear();repo.Content[path]=File.ReadAllBytes(file);repo.Files.Add(Source(path,repo.Content[path]));repo.Commit="commit-b";
        var installer=new LibraryInstaller(repo,library,Validator(Environment.GetEnvironmentVariable("MADOKACHANGER_TEST_NATIVE")),new(),NullLogger<LibraryInstaller>.Instance);
        var phases=new List<string>();
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>installer.InstallAsync(Metadata,new InlineProgress(p=>{lock(phases)phases.Add(p.Phase);}),CancellationToken.None));
        Assert.Contains("Signed modpkg",error.Message);Assert.Contains("Obfuscating local package storage",phases);
        Assert.Equal(old,library.ActiveDirectory);Assert.Equal(pointer,File.ReadAllBytes(Path.Combine(library.Root,"active.json")));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(library.Root,"versions")));
    }

    // Independent format fixture, consumed by the pinned native parser as well as the application.
    private static (int Toc,int Data,byte[] Metadata,byte[] Payload) CreateModpkg(string path,bool compressed,bool signed=false,bool metadataLast=false)
    {
        var metadata=MessagePackSerializer.Serialize(new Dictionary<string,object?>
        {
            ["schema_version"]=2,["name"]=metadataLast?new string('n',100):"original",["display_name"]=metadataLast?new string('N',200):"Original skin",["description"]="Creator attribution",["version"]="1.2.3",
            ["authors"]=new[]{new{name="Creator",role="Artist"}},["license"]=new Dictionary<string,object>{{"type","spdx"},{"spdx_id","MIT"}},
            ["distributor"]=new{site_id="source",site_name="Original distributor",site_url="https://example.com",mod_id="123"},
            ["champions"]=new[]{"Ahri"},["generator"]="Original writer 1.0",["opaque_extension"]=new byte[]{255,0,128,42}
        },MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance));
        byte[] stored=metadata;if(compressed){using var encoder=new Compressor(3);stored=encoder.Wrap(metadata).ToArray();}
        using var compressor=new Compressor(3);var data="gameplay payload bytes with repeated repeated repeated content"u8.ToArray();var payload=compressor.Wrap(data).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var stream=File.Create(path);using var writer=new BinaryWriter(stream);
        writer.Write("_modpkg_"u8);writer.Write(1u);writer.Write(signed?4u:0u);writer.Write(2u);if(signed)writer.Write(new byte[]{1,2,3,4});
        writer.Write(1u);writer.Write(4u);writer.Write("base"u8);writer.Write(0);
        writer.Write(2u);writer.Write("_meta_/info.msgpack\0assets/fixture.bin\0"u8);
        writer.Write(1u);writer.Write("ahri.wad.client\0"u8);while(stream.Position%8!=0)writer.Write((byte)0);
        var toc=(int)stream.Position;var offset=toc+122;
        var metaOffset=metadataLast?offset+payload.Length:offset;
        Chunk("_meta_/info.msgpack",metaOffset,stored,metadata,compressed?1:0,0,uint.MaxValue,uint.MaxValue);
        Chunk("assets/fixture.bin",metadataLast?offset:offset+stored.Length,payload,data,1,1,0,0);
        if(metadataLast){writer.Write(payload);writer.Write(stored);}else{writer.Write(stored);writer.Write(payload);}
        return(toc,metaOffset,metadata,payload);
        void Chunk(string name,int position,byte[] bytes,byte[] expanded,int compression,uint pathIndex,uint layer,uint wad)
        {
            writer.Write(XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(name)));writer.Write((ulong)position);writer.Write((byte)compression);
            writer.Write((ulong)bytes.Length);writer.Write((ulong)expanded.Length);writer.Write(XxHash3.HashToUInt64(bytes));writer.Write(XxHash3.HashToUInt64(expanded));
            writer.Write(pathIndex);writer.Write(layer);writer.Write(wad);
        }
    }
    private static Dictionary<string,byte[]> MetadataFields(byte[] bytes)
    {
        var reader=new MessagePackReader(bytes);var count=reader.ReadMapHeader();var result=new Dictionary<string,byte[]>();
        for(var i=0;i<count;i++){var key=reader.ReadString()!;result[key]=reader.ReadRaw().ToArray();}return result;
    }

    private async Task<(StateStore,LibraryStore,FixtureRepository,LibraryInstaller)> Fixture()
    {
        var paths=new AppPaths(root);var state=new StateStore(paths,NullLogger<StateStore>.Instance);await state.LoadAsync();var library=new LibraryStore(state);
        var source=Path.Combine(root,"source.fantome");CreateFantome(source);var payload=File.ReadAllBytes(source);
        var repo=new FixtureRepository(new Dictionary<string,byte[]>{{BasePath,payload},{ChromaPath,payload},{FormPath,payload},{"skins/103/103001/special/special.png","preview"u8.ToArray()}});
        return(state,library,repo,new(repo,library,Validator(),new(),NullLogger<LibraryInstaller>.Instance));
    }

    private static async Task<string> ActivateLegacy(LibraryStore library,FixtureRepository repo)
    {
        var generation=Path.Combine(library.Root,"versions","legacy");Directory.CreateDirectory(generation);
        var paths=repo.Files.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);var records=new List<PackageRecord>();
        foreach(var file in repo.Files)
        {
            var destination=ArchiveSafety.SafeDestination(generation,file.Path);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);await File.WriteAllBytesAsync(destination,repo.Content[file.Path]);
            if(GitHubRepository.IsPackage(file.Path))records.Add(PackageMapper.Map(file,await Validator().InspectAsync(destination,CancellationToken.None),Metadata,paths));
        }
        // Round-trip an actual old JSON shape: version, storage and additional metadata fields are absent.
        var manifest=JsonSerializer.SerializeToNode(new LibraryManifest(repo.Commit,"main",DateTimeOffset.UtcNow,"16.20",records,repo.Files),JsonFile.Options)!.AsObject();
        manifest.Remove("transformationVersion");
        foreach(var package in manifest["packages"]!.AsArray().OfType<JsonObject>())
            foreach(var key in new[]{"storagePath","localSha256","localSize","transformationVersion","originalMetadata","description","champions","lfsOid","lfsSize"})package.Remove(key);
        await JsonFile.WriteAsync(Path.Combine(generation,"manifest.json"),manifest);
        await JsonFile.WriteAsync(Path.Combine(library.Root,"active.json"),new LibraryPointer("versions/legacy",repo.Commit));
        await library.LoadAsync();return generation;
    }

    private static void CreateFantome(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        Add("META/info.json",Encoding.UTF8.GetBytes("""{"Name":"Original skin","Author":"Creator","Version":"1.2.3","Description":"Credits to Creator","License":"Creator terms","RequiredField":{"keep":true}}"""));
        Add("META/LICENSE.txt","Copyright Creator. Keep this attribution."u8.ToArray());Add("META/preview.png",[0,1,2,3]);
        Add("RAW/assets/original/path.bin",[5,4,3,2,1]);Add("WAD/Ahri.wad.client",WadBytes());
        void Add(string name,byte[] bytes){using var stream=zip.CreateEntry(name).Open();stream.Write(bytes);}
    }

    private static byte[] WadBytes()
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write((byte)'R');writer.Write((byte)'W');writer.Write((byte)3);writer.Write((byte)1);writer.Write(new byte[256]);writer.Write(0UL);writer.Write(1);
        writer.Write(0x1234567890abcdefUL);writer.Write(304u);writer.Write(4u);writer.Write(4u);writer.Write((byte)0);writer.Write(new byte[11]);writer.Write(new byte[]{1,2,3,4});
        return stream.ToArray();
    }
    private static Dictionary<string,byte[]> ZipContents(string path)
    {
        using var zip=ZipFile.OpenRead(path);return zip.Entries.ToDictionary(e=>e.FullName,e=>{using var input=e.Open();using var output=new MemoryStream();input.CopyTo(output);return output.ToArray();});
    }
    private static string Sha256(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static RepositoryFile Source(string path,byte[] bytes)=>new(path,Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray())).ToLowerInvariant(),bytes.Length);
    private sealed class InlineProgress(Action<TransferProgress> report):IProgress<TransferProgress>{public void Report(TransferProgress value)=>report(value);}
    private sealed class FixtureRepository(Dictionary<string,byte[]> content):IRepositorySource
    {
        public Dictionary<string,byte[]> Content {get;}=content;
        public List<RepositoryFile> Files {get;}=content.Select(p=>Source(p.Key,p.Value)).ToList();
        public Dictionary<string,(string Oid,long Size)> MaterializedLfs {get;}=[];
        public List<string> FetchedCommits {get;}=[];
        public string Commit="commit-a";public int Fetches,Resolves;public bool Offline;
        public Task<RepositoryRevision> ResolveAsync(CancellationToken ct){Interlocked.Increment(ref Resolves);if(Offline)throw new IOException("offline");return Task.FromResult(new RepositoryRevision("main",Commit,Files));}
        public async Task<RepositoryFile> FetchAsync(RepositoryRevision revision,RepositoryFile file,string destination,Action<long> transferred,CancellationToken ct)
        {
            if(Offline)throw new IOException("offline");Interlocked.Increment(ref Fetches);lock(FetchedCommits)FetchedCommits.Add(revision.Commit);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);await File.WriteAllBytesAsync(destination,Content[file.Path],ct);
            var verified=MaterializedLfs.TryGetValue(file.Path,out var lfs)?file with{LfsOid=lfs.Oid,LfsSize=lfs.Size}:file;
            await SourceIntegrity.VerifyAsync(verified,destination,ct);transferred(Content[file.Path].Length);return verified;
        }
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}

public sealed class NativePackageFactAttribute : FactAttribute
{
    public NativePackageFactAttribute()
    {
        if(!File.Exists(Environment.GetEnvironmentVariable("MADOKACHANGER_TEST_NATIVE")))
            Skip="Set MADOKACHANGER_TEST_NATIVE to the pinned native adapter to run the real modpkg round trip.";
    }
}
