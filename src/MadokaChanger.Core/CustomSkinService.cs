using System.Security.Cryptography;
using System.Text.Json;

namespace MadokaChanger.Core;

public sealed class CustomSkinService(AppPaths paths,StateStore state,PackageValidator validator,NativeBridge native,OperationGate gate)
{
    public async Task<CustomSkin> ImportAsync(string file,int assignedChampionId,Catalog catalog,CancellationToken ct)
    {
        using var lease=await gate.EnterAsync(ct);
        if(!GitHubRepository.IsPackage(file)) throw new InvalidDataException("Select a .modpkg, .fantome, or recognized Fantome ZIP package.");
        var id=Guid.NewGuid().ToString("N"); var staging=Path.Combine(paths.Temporary,"import-"+id); Directory.CreateDirectory(staging);
        var installed=Path.Combine(paths.Custom,id); var moved=false;
        try
        {
            var package=Path.Combine(staging,"package"+Path.GetExtension(file).ToLowerInvariant());
            await using(var input=File.OpenRead(file))
            await using(var output=File.Create(package)) { if(input.Length>2L*1024*1024*1024) throw new InvalidDataException("Package exceeds 2 GB."); await input.CopyToAsync(output,ct); }
            string sha;
            await using(var s=File.OpenRead(package)) sha=Convert.ToHexString(await SHA256.HashDataAsync(s,ct));
            if(state.State.CustomSkins.Any(c=>c.Sha256==sha)) throw new InvalidOperationException("This exact package is already imported.");
            var inspection=await validator.InspectAsync(package,ct);
            if(inspection.Champions.Any(CatalogPolicy.IsClassicChampion))throw new InvalidDataException("Classic champion packages are no longer supported.");
            var declared=catalog.Champions.Where(c=>inspection.Champions.Any(n=>n.Equals(c.Alias,StringComparison.OrdinalIgnoreCase)||n==c.Id.ToString())).ToList();
            if(inspection.Champions.Count>0&&declared.Count==0)throw new InvalidDataException("The package declares a champion that is unavailable in this catalog.");
            int champion;
            if(declared.Count>1) throw new InvalidDataException("Package declares several champions. This manager requires a single champion association.");
            if(declared.Count==1) champion=declared[0].Id;
            else if(catalog.Champions.Any(c=>c.Id==assignedChampionId)) champion=assignedChampionId;
            else throw new InvalidOperationException("Select a champion before importing a package without champion metadata.");
            var project=Path.Combine(staging,"project");
            using(var result=await native.InvokeAsync(new{operation="import",package,output=project},ct)) { }
            var files=Directory.EnumerateFiles(project,"*",SearchOption.AllDirectories).ToList();
            if(files.Count>ArchiveSafety.MaximumEntries || files.Sum(p=>new FileInfo(p).Length)>ArchiveSafety.MaximumExpandedBytes) throw new InvalidDataException("Imported project exceeds extraction limits.");
            foreach(var p in files) { _=ArchiveSafety.SafeDestination(project,Path.GetRelativePath(project,p)); if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Imported project contains a reparse point."); }
            ct.ThrowIfCancellationRequested(); Directory.Move(staging,installed); moved=true;
            var skin=new CustomSkin(id,champion,inspection.Name,inspection.Author,inspection.Version,inspection.Description,sha,Path.GetFileName(package),Path.Combine(installed,"project"),inspection.Overrides);
            state.State.CustomSkins.Add(skin);
            try { await state.SaveAsync(ct); }
            catch { state.State.CustomSkins.Remove(skin); throw; }
            return skin;
        }
        catch { if(moved) ArchiveSafety.DeleteOwnedDirectory(paths.Custom,installed); throw; }
        finally { if(Directory.Exists(staging)) ArchiveSafety.DeleteOwnedDirectory(paths.Temporary,staging); }
    }
    public async Task SetEnabledAsync(CustomSkin skin,bool enabled,CancellationToken ct)
    {
        var index=state.State.CustomSkins.FindIndex(c=>c.Id==skin.Id);
        if(index<0) throw new InvalidOperationException("The custom skin is no longer installed.");
        var previous=state.State.CustomSkins[index]; state.State.CustomSkins[index]=previous with{Enabled=enabled};
        try { await state.SaveAsync(ct); } catch { state.State.CustomSkins[index]=previous; throw; }
    }
    public async Task UninstallAsync(CustomSkin skin,CancellationToken ct)
    {
        using var lease=await gate.EnterAsync(ct);
        var index=state.State.CustomSkins.FindIndex(c=>c.Id==skin.Id); if(index<0) return;
        state.State.CustomSkins.RemoveAt(index);
        try { await state.SaveAsync(ct); }
        catch { state.State.CustomSkins.Insert(index,skin); throw; }
        ArchiveSafety.DeleteOwnedDirectory(paths.Custom,Path.Combine(paths.Custom,skin.Id));
    }
}
