using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MadokaChanger.Core;

/// <summary>Versioned local storage obfuscation for downloaded library packages only.</summary>
public sealed class PackageTransformer(PackageValidator validator)
{
    public const int CurrentVersion=1;

    public static bool IsCurrent(LibraryManifest manifest) => manifest.TransformationVersion==CurrentVersion &&
        manifest.Packages.All(p=>p.TransformationVersion==CurrentVersion && p.StoragePath is not null &&
            p.LocalSha256 is { Length:64 } && p.LocalSize is >0);

    public async Task<PackageRecord> TransformAsync(string generation,PackageRecord original,CancellationToken ct)
    {
        var source=ArchiveSafety.SafeDestination(generation,original.Path);
        var id="p"+Guid.NewGuid().ToString("N");
        var extension=Path.GetExtension(original.Path);
        if(!GitHubRepository.IsPackage(original.Path)) throw new InvalidDataException("Unsupported library package extension.");
        var storagePath="packages/"+id+extension;
        var destination=ArchiveSafety.SafeDestination(generation,storagePath);
        var work=ArchiveSafety.SafeDestination(generation,".transform-"+id);
        Directory.CreateDirectory(work);
        var output=Path.Combine(work,"package"+extension);
        try
        {
            if(extension.Equals(".modpkg",StringComparison.OrdinalIgnoreCase))
                await ModpkgRewriter.RewriteAsync(source,output,id,ct);
            else
                await RewriteFantomeAsync(source,output,id,ct);

            var rewritten=await validator.InspectAsync(output,ct);
            if(!original.Overrides.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(rewritten.Overrides))
                throw new InvalidDataException($"Rewriting changed package overrides: {original.Path}");
            if(rewritten.Name!=id) throw new InvalidDataException("The rewritten package has no local identity.");
            string sha;long size;
            await using(var final=File.OpenRead(output))
            {
                size=final.Length;
                sha=Convert.ToHexString(await SHA256.HashDataAsync(final,ct)).ToLowerInvariant();
            }
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(output,destination); // A collision fails the transaction; never replace another package.
            File.Delete(source);
            return original with {StoragePath=storagePath,LocalSha256=sha,LocalSize=size,TransformationVersion=CurrentVersion};
        }
        finally { ArchiveSafety.DeleteOwnedDirectory(generation,work); }
    }

    private static async Task RewriteFantomeAsync(string source,string output,string id,CancellationToken ct)
    {
        using var input=ZipFile.OpenRead(source);
        ArchiveSafety.ValidateEntries(input);
        var metadata=input.Entries.Single(e=>e.FullName.Equals("META/info.json",StringComparison.OrdinalIgnoreCase));
        JsonObject expected;
        await using(var stream=metadata.Open()) expected=await ReadObjectAsync(stream,ct);
        // This is the only Fantome identity field the supported format defines. Unknown fields and credits stay intact.
        expected["Name"]=id;
        var payloads=new Dictionary<string,string>(StringComparer.Ordinal);
        using(var result=ZipFile.Open(output,ZipArchiveMode.Create))
        {
            foreach(var entry in input.Entries)
            {
                ct.ThrowIfCancellationRequested();
                // Avoid making previously valid, highly compressible entries exceed the validator's ratio limit.
                var compression=entry.Length>1024*1024 && entry.Length/Math.Max(1,entry.CompressedLength)>200
                    ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                var copy=result.CreateEntry(entry.FullName,compression);
                copy.ExternalAttributes=entry.ExternalAttributes;copy.LastWriteTime=entry.LastWriteTime;
                await using var target=copy.Open();
                if(entry==metadata)
                    await System.Text.Json.JsonSerializer.SerializeAsync(target,expected,JsonFile.Options,ct);
                else
                {
                    await using var stream=entry.Open();
                    // Hash bytes read from the source, then compare against a fresh read of the rewritten ZIP.
                    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer=new byte[65536];
                    for(int n;(n=await stream.ReadAsync(buffer,ct))>0;)
                    {
                        hash.AppendData(buffer,0,n);await target.WriteAsync(buffer.AsMemory(0,n),ct);
                    }
                    payloads.Add(entry.FullName,Convert.ToHexString(hash.GetHashAndReset()));
                }
            }
        }
        using var check=ZipFile.OpenRead(output);
        ArchiveSafety.ValidateEntries(check);
        if(check.Entries.Count!=input.Entries.Count) throw new InvalidDataException("Rewriting changed ZIP entries.");
        foreach(var entry in check.Entries)
        {
            await using var stream=entry.Open();
            if(entry.FullName==metadata.FullName) EnsureMetadata(expected,await ReadObjectAsync(stream,ct));
            else if(!payloads.TryGetValue(entry.FullName,out var hash) || hash!=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)))
                throw new InvalidDataException($"Rewriting changed package content: {entry.FullName}");
        }
    }

    private static async Task<JsonObject> ReadObjectAsync(Stream stream,CancellationToken ct) =>
        await JsonNode.ParseAsync(stream,cancellationToken:ct) as JsonObject ?? throw new InvalidDataException("Package metadata must be an object.");

    private static void EnsureMetadata(JsonObject expected,JsonObject actual)
    {
        if(!JsonNode.DeepEquals(expected,actual)) throw new InvalidDataException("Rewriting changed protected package metadata.");
    }

}
