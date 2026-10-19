using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MadokaChanger.Core;

public static partial class ArchiveSafety
{
    public const long MaximumExpandedBytes=4L*1024*1024*1024;
    public const long MaximumEntryBytes=512L*1024*1024;
    public const int MaximumEntries=100000;
    public static void ValidatePath(string path)
    {
        var p=path.Replace('\\','/');
        if(string.IsNullOrWhiteSpace(p) || p.StartsWith('/') || p.Length>1000 || p.Contains(':') || p.IndexOfAny(['\0','<','>','"','|','?','*'])>=0)
            throw new InvalidDataException($"Unsafe archive path: {path}");
        foreach(var part in p.TrimEnd('/').Split('/'))
            if(part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || ReservedName().IsMatch(part) || part.Any(char.IsControl))
                throw new InvalidDataException($"Unsafe archive path: {path}");
    }
    public static string SafeDestination(string root,string relative)
    {
        ValidatePath(relative);
        var basePath=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var full=Path.GetFullPath(Path.Combine(basePath,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive path leaves its destination.");
        return full;
    }
    public static void DeleteOwnedDirectory(string root,string directory)
    {
        var path=SafeDestination(root,Path.GetRelativePath(root,directory));
        if(Directory.Exists(path)) Directory.Delete(path,true);
    }
    public static void ValidateEntries(ZipArchive zip)
    {
        if(zip.Entries.Count>MaximumEntries) throw new InvalidDataException("Archive has too many entries.");
        long total=0; var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var e in zip.Entries)
        {
            ValidatePath(e.FullName);
            if(!names.Add(e.FullName.Replace('\\','/').TrimEnd('/'))) throw new InvalidDataException("Archive contains duplicate or case-colliding paths.");
            var type=(e.ExternalAttributes>>16)&0xF000;
            if(type is not (0 or 0x8000 or 0x4000)) throw new InvalidDataException("Archive contains a symlink or special file.");
            total=checked(total+e.Length);
            if(e.Length>MaximumEntryBytes || total>MaximumExpandedBytes || (e.Length>1048576 && e.Length/Math.Max(1,e.CompressedLength)>1000))
                throw new InvalidDataException("Archive exceeds extraction limits (100,000 files, 512 MB per file, 4 GB total, 1000:1 ratio).");
        }
    }
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase)] private static partial Regex ReservedName();
}

public sealed class PackageValidator(NativeBridge native)
{
    public async Task<PackageInspection> InspectAsync(string path,CancellationToken ct)
    {
        if(new FileInfo(path).Length>2L*1024*1024*1024) throw new InvalidDataException("Package exceeds the 2 GB download limit.");
        if(Path.GetExtension(path).Equals(".modpkg",StringComparison.OrdinalIgnoreCase))
        {
            await using(var f=File.OpenRead(path))
            {
                var header=new byte[20]; await f.ReadExactlyAsync(header,ct);
                if(!header.AsSpan(0,8).SequenceEqual("_modpkg_"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))!=1 ||
                   BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12))>16*1024*1024 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16))>ArchiveSafety.MaximumEntries)
                    throw new InvalidDataException("Invalid or oversized modpkg header.");
            }
            using var doc=await native.InvokeAsync(new {operation="probe",package=path},ct);
            var r=doc.RootElement;
            if(r.GetProperty("bytes").GetInt64()>ArchiveSafety.MaximumExpandedBytes || r.GetProperty("chunks").GetInt32()>ArchiveSafety.MaximumEntries) throw new InvalidDataException("modpkg exceeds extraction limits.");
            foreach(var p in r.GetProperty("paths").EnumerateArray()) ArchiveSafety.ValidatePath(p.GetString()!);
            await using(var package=File.OpenRead(path))
            {
                foreach(var record in r.GetProperty("records").EnumerateArray())
                {
                    var offset=record.GetProperty("offset").GetInt64();var length=record.GetProperty("compressed").GetInt64();var expanded=record.GetProperty("expanded").GetInt64();
                    if(offset<20||offset>package.Length||length<0||length>ArchiveSafety.MaximumEntryBytes||length>package.Length-offset||expanded<0||expanded>ArchiveSafety.MaximumEntryBytes)throw new InvalidDataException("modpkg chunk exceeds its bounds or extraction limits.");
                    package.Position=offset;var hash=new XxHash3();var buffer=new byte[65536];var left=length;
                    while(left>0){var n=await package.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,left)),ct);if(n==0)throw new EndOfStreamException();hash.Append(buffer.AsSpan(0,n));left-=n;}
                    if(hash.GetCurrentHashAsUInt64()!=record.GetProperty("checksum").GetUInt64())throw new InvalidDataException("modpkg compressed chunk checksum mismatch.");
                }
            }
            var j=r.GetProperty("project");
            return new(MetadataProvider.Text(j,"display_name"),Join(j,"authors"),MetadataProvider.Text(j,"version"),MetadataProvider.Text(j,"description"),
                Strings(j,"champions"),Strings(r,"overrides"),r.GetProperty("bytes").GetInt64());
        }
        return await Task.Run(async ()=>
        {
            using var zip=ZipFile.OpenRead(path); ArchiveSafety.ValidateEntries(zip);
            var meta=zip.Entries.SingleOrDefault(e=>e.FullName.Equals("META/info.json",StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Only recognized Fantome ZIP archives with META/info.json and WAD/ or RAW/ content can be imported.");
            if(meta.Length>1024*1024) throw new InvalidDataException("Package metadata is too large.");
            using var info=await JsonDocument.ParseAsync(meta.Open(),cancellationToken:ct);
            var hashes=new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded=0;
            foreach(var e in zip.Entries.Where(e=>!e.FullName.EndsWith('/')))
            {
                ct.ThrowIfCancellationRequested(); var parts=e.FullName.Replace('\\','/').Split('/');
                var content=parts[0].Equals("WAD",StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith("WAD_",StringComparison.OrdinalIgnoreCase);
                if(content && parts.Length>=2)
                {
                    if(parts.Length==2 && parts[1].EndsWith(".wad.client",StringComparison.OrdinalIgnoreCase))
                    {
                        await using var s=e.Open(); using var memory=new MemoryStream(); await s.CopyToAsync(memory,ct); memory.Position=0;
                        expanded=checked(expanded+ReadWad(memory,hashes));
                    }
                    else if(parts.Length>=3) hashes.Add(HashPath(string.Join('/',parts.Skip(2))));
                }
                else if(parts[0].Equals("RAW",StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith("RAW_",StringComparison.OrdinalIgnoreCase)) hashes.Add(HashPath(string.Join('/',parts.Skip(1))));
                // Read every ZIP stream so corrupt compressed entries cannot pass preflight.
                await using var input=e.Open(); var crc=new Crc32(); var buffer=new byte[65536]; long length=0;
                for(int n;(n=await input.ReadAsync(buffer,ct))>0;) { crc.Append(buffer.AsSpan(0,n)); length+=n; }
                if(length!=e.Length || crc.GetCurrentHashAsUInt32()!=e.Crc32) throw new InvalidDataException($"Archive checksum mismatch: {e.FullName}");
            }
            if(hashes.Count==0) throw new InvalidDataException("Package has no recognized WAD/RAW overrides.");
            if(expanded>ArchiveSafety.MaximumExpandedBytes) throw new InvalidDataException("WAD content exceeds extraction limits.");
            string Text(string key)=>MetadataProvider.Text(info.RootElement,key);
            if(string.IsNullOrWhiteSpace(Text("Name"))) throw new InvalidDataException("Package metadata is missing Name.");
            return new PackageInspection(Text("Name"),Text("Author"),Text("Version"),Text("Description"),[],hashes.ToList(),Math.Max(expanded,zip.Entries.Sum(e=>e.Length)));
        },ct);
    }
    private static long ReadWad(Stream stream,HashSet<string> hashes)
    {
        // Descriptor layout verified against LeagueToolkit.Core.Wad.WadFile and WadChunk.
        using var r=new BinaryReader(stream,Encoding.UTF8,true);
        if(r.ReadByte()!='R' || r.ReadByte()!='W') throw new InvalidDataException("Invalid WAD signature.");
        var major=r.ReadByte(); _=r.ReadByte();
        if(major!=3) throw new InvalidDataException("This adapter validates WAD v3 only. Convert legacy WADs with upstream tooling.");
        stream.Position=268; var count=r.ReadInt32();
        if(count<0 || count>ArchiveSafety.MaximumEntries || 272L+count*32L>stream.Length) throw new InvalidDataException("Invalid WAD table.");
        long total=0;
        for(int i=0;i<count;i++)
        {
            var hash=r.ReadUInt64(); var offset=r.ReadUInt32(); var compressed=r.ReadUInt32(); var expanded=r.ReadUInt32();
            var type=r.ReadByte()&15; _=r.ReadBytes(11);
            if(type>4 || (long)offset+compressed>stream.Length || offset<272L+count*32L || expanded>ArchiveSafety.MaximumEntryBytes) throw new InvalidDataException("WAD chunk has an invalid range or size.");
            total=checked(total+expanded); hashes.Add(hash.ToString("x16"));
        }
        return total;
    }
    public static string HashPath(string path)
    {
        var name=Path.GetFileNameWithoutExtension(path);
        if(name.Length==16 && ulong.TryParse(name,System.Globalization.NumberStyles.HexNumber,null,out var value)) return value.ToString("x16");
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(path.Replace('\\','/').ToLowerInvariant())).ToString("x16");
    }
    private static List<string> Strings(JsonElement j,string key) => j.TryGetProperty(key,out var a)&&a.ValueKind==JsonValueKind.Array ? a.EnumerateArray().Select(x=>x.GetString()??"").ToList():[];
    private static string Join(JsonElement j,string key)=>string.Join(", ",Strings(j,key));
}

public static class PackageMapper
{
    public static PackageRecord Map(RepositoryFile file,PackageInspection inspection,Catalog metadata,IReadOnlySet<string> paths)
    {
        var parts=file.Path.Split('/'); int? c=null,s=null,chroma=null; string? form=null,error=null,preview=null;
        if(parts.Length is not (4 or 5) || parts[0]!="skins" || !int.TryParse(parts[1],out var championId) || !int.TryParse(parts[2],out var skinId)) error="Unrecognized repository layout; retained in the library without a selectable mapping.";
        else
        {
            c=championId; s=skinId;
            var champion=metadata.Champions.SingleOrDefault(x=>x.Id==championId);
            var skin=champion?.Skins.SingleOrDefault(x=>x.Id==skinId);
            if(skin is null) error="Champion or parent skin ID is absent from the pinned metadata patch.";
            else if(parts.Length==4)
            {
                if(Path.GetFileNameWithoutExtension(parts[3])!=parts[2]) error="Package filename and parent skin ID disagree.";
            }
            else
            {
                var variant=parts[3];
                if(Path.GetFileNameWithoutExtension(parts[4])!=variant) error="Package filename and variant directory disagree.";
                else if(int.TryParse(variant,out var id) && skin.Chromas.Any(x=>x.Id==id)) chroma=id;
                else
                {
                    var previewPath=string.Join('/',parts.Take(4))+"/"+variant+".png";
                    if(paths.Contains(previewPath) && !champion!.Skins.Any(x=>x.Id.ToString()==variant)) { form=variant; preview=previewPath; }
                    else error="Unknown variant ID: no matching chroma metadata or explicit form preview.";
                }
            }
        }
        var basePreview=Path.ChangeExtension(file.Path,".png").Replace('\\','/');
        if(paths.Contains(basePreview)) preview=basePreview;
        return new(file.Path,file.Sha,file.Size,inspection.Name,inspection.Author,inspection.Version,c,s,chroma,form,preview,inspection.Overrides,error);
    }
    public static List<PackageRecord> RejectAmbiguities(List<PackageRecord> records)
    {
        var duplicates=records.Where(p=>p.Selection is not null).GroupBy(p=>p.Selection!.Key).Where(g=>g.Count()>1).Select(g=>g.Key).ToHashSet();
        return records.Select(p=>p.Selection is { } s && duplicates.Contains(s.Key) ? p with{MappingError="Multiple packages map to this variant; explicit upstream mapping is required."}:p).ToList();
    }
}

public sealed record OverrideSource(string Id,string Name,bool IsCustom,IReadOnlyCollection<string> Overrides);
public sealed record OverrideConflict(string Hash,string FirstId,string FirstName,string SecondId,string SecondName);
public static class ConflictResolver
{
    public static List<OverrideConflict> Find(IEnumerable<OverrideSource> sources)
    {
        var seen=new Dictionary<string,OverrideSource>(StringComparer.OrdinalIgnoreCase); var result=new List<OverrideConflict>();
        foreach(var s in sources)
            foreach(var hash in s.Overrides.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if(seen.TryGetValue(hash,out var previous)&&previous.Id!=s.Id) result.Add(new(hash,previous.Id,previous.Name,s.Id,s.Name));
                else seen[hash]=s;
            }
        return result;
    }
    public static List<OverrideSource> Order(List<OverrideSource> sources,ConflictChoice choice)
    {
        var conflicts=Find(sources);
        if(conflicts.Count!=0 && choice==ConflictChoice.Reject) throw new InvalidOperationException($"{conflicts.Count} file overrides conflict. Choose a priority in Custom Skins before starting. {conflicts[0].FirstName} / {conflicts[0].SecondName}");
        var byId=sources.ToDictionary(x=>x.Id);
        if(conflicts.Any(c=>byId[c.FirstId].IsCustom==byId[c.SecondId].IsCustom)) throw new InvalidOperationException("Two packages from the same source conflict. Disable one before starting.");
        // Upstream OverlayBuilder resolves conflicts in favor of the first mod.
        return sources.OrderByDescending(s=>choice==ConflictChoice.PreferCustom ? s.IsCustom : !s.IsCustom).ToList();
    }
}
