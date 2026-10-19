using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MadokaChanger.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string Metadata => Path.Combine(Root,"metadata");
    public string Images => Path.Combine(Root,"images");
    public string Custom => Path.Combine(Root,"custom");
    public string Temporary => Path.Combine(Root,"temporary");
    public string Logs => Path.Combine(Root,"logs");
    public string Overlay => Path.Combine(Root,"patches");
    public string DefaultLibrary => Path.Combine(Root,"library");
    public AppPaths(string? root = null)
    {
        Root = root ?? ResolveDefaultRoot();
        foreach (var p in new[]{Root,Metadata,Images,Custom,Temporary,Logs,Overlay,DefaultLibrary}) Directory.CreateDirectory(p);
    }
    public static string ResolveDefaultRoot()
    {
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current=Path.Combine(local,"MadokaChanger");
        var legacy=Path.Combine(local,"SoulGem");
        return !File.Exists(Path.Combine(current,"settings.json"))&&File.Exists(Path.Combine(legacy,"settings.json"))?legacy:current;
    }
}

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return default;
        await using var s = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(s,Options,ct);
    }
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var s = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(s,value,Options,ct);
                await s.FlushAsync(ct); s.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp,path,true); // Same-volume atomic replacement; the old file survives a failed write.
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class StateStore(AppPaths paths, ILogger<StateStore> log)
{
    private readonly SemaphoreSlim gate = new(1,1);
    public UserState State { get; private set; } = new();
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var p = Path.Combine(paths.Root,"settings.json");
        try { State = await JsonFile.ReadAsync<UserState>(p,ct) ?? new(); }
        catch (Exception e) when(e is JsonException or IOException)
        {
            log.LogError(e,"Settings could not be read; keeping the file for recovery");
            if (File.Exists(p)) File.Copy(p,p + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),true);
            State = new();
        }
        if (string.IsNullOrWhiteSpace(State.Settings.LibraryFolder)) State.Settings.LibraryFolder = paths.DefaultLibrary;
    }
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await JsonFile.WriteAsync(Path.Combine(paths.Root,"settings.json"),State,ct); }
        finally { gate.Release(); }
    }
}

// One lease spans an entire install/build/running session. Browsing never takes it.
public sealed class OperationGate
{
    private readonly SemaphoreSlim semaphore = new(1,1);
    public bool IsBusy => semaphore.CurrentCount == 0;
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        if (!await semaphore.WaitAsync(0,ct)) throw new InvalidOperationException("Stop the patcher or finish the current library operation first.");
        return new Lease(semaphore);
    }
    private sealed class Lease(SemaphoreSlim s) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed,1)==0) s.Release(); }
    }
}

public sealed class LibraryStore(StateStore state)
{
    public LibraryManifest? Manifest { get; private set; }
    public string? ActiveDirectory { get; private set; }
    public string Root => Path.GetFullPath(state.State.Settings.LibraryFolder);
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var p = await JsonFile.ReadAsync<LibraryPointer>(Path.Combine(Root,"active.json"),ct);
        if (p is null) { Manifest=null; ActiveDirectory=null; return; }
        var dir = ArchiveSafety.SafeDestination(Root,p.Directory);
        var manifest = await JsonFile.ReadAsync<LibraryManifest>(Path.Combine(dir,"manifest.json"),ct)
            ?? throw new InvalidDataException("The active skin library has no manifest. Reinstall it with Update.");
        if (manifest.Commit != p.Commit) throw new InvalidDataException("The library pointer and manifest revisions disagree.");
        Manifest=CatalogPolicy.WithoutClassic(manifest); ActiveDirectory=dir;
    }
    public async Task ActivateAsync(string directory, LibraryManifest manifest, CancellationToken ct)
    {
        manifest=CatalogPolicy.WithoutClassic(manifest);
        await JsonFile.WriteAsync(Path.Combine(directory,"manifest.json"),manifest,ct);
        await JsonFile.WriteAsync(Path.Combine(Root,"active.json"),new LibraryPointer(Path.GetRelativePath(Root,directory),manifest.Commit),ct);
        ActiveDirectory=directory; Manifest=manifest;
    }
    public IReadOnlyList<PackageRecord> Find(Selection selection) => Manifest?.Packages.Where(p=>p.Selection==selection).ToList() ?? [];
    public string GetPackagePath(PackageRecord p) => ArchiveSafety.SafeDestination(ActiveDirectory ?? throw new InvalidOperationException("Install the skin library first."),p.Path);
}
