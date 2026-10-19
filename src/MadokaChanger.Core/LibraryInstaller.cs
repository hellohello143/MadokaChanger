using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MadokaChanger.Core;

public sealed class LibraryInstaller(IRepositorySource repository,LibraryStore library,PackageValidator validator,OperationGate gate,ILogger<LibraryInstaller> log)
{
    public async Task<string> InstallAsync(Catalog metadata,IProgress<TransferProgress>? progress,CancellationToken ct)
    {
        using var lease=await gate.EnterAsync(ct);
        progress?.Report(new("Resolving repository revision",0,0,0,0));
        var revision=CatalogPolicy.WithoutClassic(await repository.ResolveAsync(ct)); // Branch is resolved exactly once per transaction.
        CheckVersion();
        if(library.Manifest is { } current && current.Commit==revision.Commit)
        {
            if(PackageTransformer.IsCurrent(current)) return "Already up to date.";
            return await StageAsync(new(current.Branch,current.Commit,current.Files),null,current,progress,ct);
        }
        return await StageAsync(revision,metadata,null,progress,ct);
    }

    // Called after loading a library at startup. No repository resolution is needed for the pinned migration.
    public async Task<string?> MigrateAsync(IProgress<TransferProgress>? progress,CancellationToken ct)
    {
        if(library.Manifest is null) return null;
        using var lease=await gate.EnterAsync(ct);
        CheckVersion();
        var current=library.Manifest;
        if(PackageTransformer.IsCurrent(current)) return null;
        return await StageAsync(new(current.Branch,current.Commit,current.Files),null,current,progress,ct);
    }

    private void CheckVersion()
    {
        if(library.Manifest is { } manifest && (manifest.TransformationVersion>PackageTransformer.CurrentVersion ||
            manifest.Packages.Any(p=>p.TransformationVersion>PackageTransformer.CurrentVersion)))
            throw new InvalidDataException("This library uses a newer storage transformation. Update MadokaChanger before modifying it.");
    }

    private async Task<string> StageAsync(RepositoryRevision revision,Catalog? metadata,LibraryManifest? migration,IProgress<TransferProgress>? progress,CancellationToken ct)
    {
        Directory.CreateDirectory(library.Root);
        var size=revision.Files.Sum(f=>f.LfsSize??f.Size);
        var drive=new DriveInfo(Path.GetPathRoot(library.Root)!);
        if(drive.AvailableFreeSpace<size*2+512L*1024*1024) throw new IOException($"Insufficient storage. Allow at least {(size*2+512L*1024*1024)/1048576:N0} MB for staging and validation.");
        var staged=ArchiveSafety.SafeDestination(library.Root,"versions/"+revision.Commit+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        long bytes=0; int complete=0,retries=0;bool activated=false;
        try
        {
            var verified=new ConcurrentDictionary<string,RepositoryFile>(StringComparer.Ordinal);
            var previous=migration?.Packages.ToDictionary(p=>p.Path,StringComparer.Ordinal);
            progress?.Report(new(migration is null?"Downloading full library":"Migrating library storage",0,revision.Files.Count,0,size));
            await Parallel.ForEachAsync(revision.Files,new ParallelOptions{MaxDegreeOfParallelism=6,CancellationToken=ct},async(f,token)=>
            {
                var destination=ArchiveSafety.SafeDestination(staged,f.Path);
                // Reuse only untransformed source bytes whose upstream integrity can still be proved.
                // Older LFS manifests omitted the pointer's OID: those packages must be fetched again at the pinned commit.
                if(migration is not null && (previous is null || !previous.TryGetValue(f.Path,out var old) || old.TransformationVersion==0 && old.StoragePath is null))
                {
                    var existing=ArchiveSafety.SafeDestination(library.ActiveDirectory!,f.Path);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using(var input=File.OpenRead(existing))
                        await using(var output=File.Create(destination)) await input.CopyToAsync(output,token);
                        await SourceIntegrity.VerifyAsync(f,destination,token);
                        verified[f.Path]=f;
                        Interlocked.Add(ref bytes,new FileInfo(destination).Length);
                        progress?.Report(new("Migrating library storage",Interlocked.Increment(ref complete),revision.Files.Count,Volatile.Read(ref bytes),size));
                        return;
                    }
                    catch(Exception e) when(e is IOException or InvalidDataException)
                    {
                        log.LogInformation("Fetching pinned source for migration: {Path} ({Reason})",f.Path,e.Message);
                    }
                }
                for(var attempt=0;;attempt++)
                {
                    try
                    {
                        verified[f.Path]=await repository.FetchAsync(revision,f,destination,n=>
                        {
                            var current=Interlocked.Add(ref bytes,n);
                            progress?.Report(new("Downloading full library",Volatile.Read(ref complete),revision.Files.Count,current,size,Volatile.Read(ref retries)));
                        },token);
                        break;
                    }
                    catch(Exception e) when(attempt<2 && e is HttpRequestException or IOException)
                    {
                        Interlocked.Increment(ref retries); log.LogWarning(e,"Retrying package {Path}, attempt {Attempt}",f.Path,attempt+2);
                        await Task.Delay(TimeSpan.FromSeconds(1<<attempt),token);
                    }
                }
                Interlocked.Increment(ref complete);
            });
            var packages=revision.Files.Where(f=>GitHubRepository.IsPackage(f.Path)).ToArray();
            var paths=revision.Files.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var catalog=new ConcurrentBag<PackageRecord>(); complete=0;
            await Parallel.ForEachAsync(packages,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=ct},async(f,token)=>
            {
                PackageInspection inspection;
                try {inspection=await validator.InspectAsync(ArchiveSafety.SafeDestination(staged,f.Path),token);}
                catch(InvalidDataException e){throw new InvalidDataException($"Invalid package {f.Path}: {e.Message}",e);}
                if(previous is not null)
                {
                    if(!previous.TryGetValue(f.Path,out var old)) throw new InvalidDataException($"The legacy manifest has no package mapping: {f.Path}");
                    if(!old.Overrides.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(inspection.Overrides))
                        throw new InvalidDataException($"The source override set differs from the installed mapping: {f.Path}");
                    var source=verified[f.Path];
                    catalog.Add(old with {Sha=source.Sha,Size=source.Size,LfsOid=source.LfsOid,LfsSize=source.LfsSize,
                        Description=inspection.Description,Champions=inspection.Champions,OriginalMetadata=old.OriginalMetadata??inspection.Metadata});
                }
                else catalog.Add(PackageMapper.Map(verified[f.Path],inspection,metadata!,paths));
                progress?.Report(new("Validating packages",Interlocked.Increment(ref complete),packages.Length,bytes,size,retries));
            });
            var mapped=PackageMapper.RejectAmbiguities(catalog.OrderBy(p=>p.Path,StringComparer.Ordinal).ToList());
            // Finish all source verification, preflight and mapping before changing any package bytes.
            var transformer=new PackageTransformer(validator);complete=0;
            progress?.Report(new("Obfuscating local package storage",0,packages.Length,bytes,size,retries));
            var transformed=new ConcurrentBag<PackageRecord>();
            await Parallel.ForEachAsync(mapped,new ParallelOptions{MaxDegreeOfParallelism=2,CancellationToken=ct},async(p,token)=>
            {
                try {transformed.Add(await transformer.TransformAsync(staged,p,token));}
                catch(InvalidDataException e){throw new InvalidDataException($"Cannot obfuscate library package {p.Path}: {e.Message}",e);}
                progress?.Report(new("Obfuscating local package storage",Interlocked.Increment(ref complete),packages.Length,bytes,size,retries));
            });
            var manifest=new LibraryManifest(revision.Commit,revision.Branch,DateTimeOffset.UtcNow,metadata?.Patch??migration!.MetadataPatch,
                transformed.OrderBy(p=>p.Path,StringComparer.Ordinal).ToList(),revision.Files.Select(f=>verified[f.Path]).ToList())
                {TransformationVersion=PackageTransformer.CurrentVersion};
            ct.ThrowIfCancellationRequested();
            await library.ActivateAsync(staged,manifest,ct);
            activated=true;
            log.LogInformation("Activated full library {Commit}, {Count} packages",revision.Commit,mapped.Count);
            progress?.Report(new("Library activated",packages.Length,packages.Length,bytes,size,retries));
            return migration is null?$"Installed {packages.Length:N0} packages · revision {revision.Commit[..Math.Min(8,revision.Commit.Length)]}.":$"Migrated local storage for {packages.Length:N0} packages.";
        }
        catch
        {
            // Active pointer was not touched before the final commit; the previous version remains available.
            if(!activated)ArchiveSafety.DeleteOwnedDirectory(library.Root,staged); throw;
        }
    }
}
