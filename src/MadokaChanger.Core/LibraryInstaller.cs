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
        if(library.Manifest?.Commit==revision.Commit) return "Already up to date.";
        Directory.CreateDirectory(library.Root);
        var size=revision.Files.Sum(f=>f.LfsSize??f.Size);
        var drive=new DriveInfo(Path.GetPathRoot(library.Root)!);
        if(drive.AvailableFreeSpace<size*2+512L*1024*1024) throw new IOException($"Insufficient storage. Allow at least {(size*2+512L*1024*1024)/1048576:N0} MB for staging and validation.");
        var staged=Path.Combine(library.Root,"versions",revision.Commit+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        long bytes=0; int complete=0,retries=0;bool activated=false;
        try
        {
            await Parallel.ForEachAsync(revision.Files,new ParallelOptions{MaxDegreeOfParallelism=6,CancellationToken=ct},async(f,token)=>
            {
                var destination=ArchiveSafety.SafeDestination(staged,f.Path);
                for(var attempt=0;;attempt++)
                {
                    try
                    {
                        await repository.FetchAsync(revision,f,destination,n=>
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
                catalog.Add(PackageMapper.Map(f,inspection,metadata,paths));
                progress?.Report(new("Validating packages",Interlocked.Increment(ref complete),packages.Length,bytes,size,retries));
            });
            var mapped=PackageMapper.RejectAmbiguities(catalog.OrderBy(p=>p.Path,StringComparer.Ordinal).ToList());
            var manifest=new LibraryManifest(revision.Commit,revision.Branch,DateTimeOffset.UtcNow,metadata.Patch,mapped,revision.Files);
            ct.ThrowIfCancellationRequested();
            await library.ActivateAsync(staged,manifest,ct);
            activated=true;
            log.LogInformation("Activated full library {Commit}, {Count} packages",revision.Commit,mapped.Count);
            progress?.Report(new("Library activated",packages.Length,packages.Length,bytes,size,retries));
            return $"Installed {packages.Length:N0} packages · revision {revision.Commit[..Math.Min(8,revision.Commit.Length)]}.";
        }
        catch
        {
            // Active pointer was not touched before the final commit; the previous version remains available.
            if(!activated)ArchiveSafety.DeleteOwnedDirectory(library.Root,staged); throw;
        }
    }
}
