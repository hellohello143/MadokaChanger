using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MadokaChanger.Core;

public sealed class RemoteClient(HttpClient http, ILogger<RemoteClient> log)
{
    public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        for (var attempt=0;;attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,url);
            request.Headers.UserAgent.ParseAdd("MadokaChanger/1.0 (+https://github.com/LeagueToolkit)");
            if (new Uri(url).Host=="api.github.com" && Environment.GetEnvironmentVariable("MADOKACHANGER_GITHUB_TOKEN") is { Length:>0 } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",token);
            HttpResponseMessage response;
            try { response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct); }
            catch (HttpRequestException) when(attempt<3) { await Task.Delay(TimeSpan.FromSeconds(1<<attempt),ct); continue; }
            if (response.IsSuccessStatusCode) return response;
            var code=response.StatusCode;
            var seconds=response.Headers.RetryAfter?.Delta?.TotalSeconds ?? (1<<Math.Min(attempt,5));
            var reset=response.Headers.TryGetValues("X-RateLimit-Reset",out var values) ? values.FirstOrDefault() : null;
            response.Dispose();
            if (code==HttpStatusCode.Forbidden)
                throw new HttpRequestException($"GitHub denied this request or its API quota is exhausted. Retry after {reset ?? "the reset time"}, or set MADOKACHANGER_GITHUB_TOKEN for a higher quota.");
            if (attempt>=3 || ((int)code<500 && code!=HttpStatusCode.TooManyRequests))
                throw new HttpRequestException($"{new Uri(url).Host} returned {(int)code}. Check the connection and retry.");
            log.LogWarning("Retry {Attempt} for {Host} after HTTP {Status}",attempt+1,new Uri(url).Host,code);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds,1,30)),ct);
        }
    }
    public async Task<JsonDocument> JsonAsync(string url, CancellationToken ct)
    {
        using var r=await GetAsync(url,ct);
        await using var s=await r.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(s,cancellationToken:ct);
    }
    public async Task DownloadAsync(string url, string destination, CancellationToken ct, Action<long>? transferred=null, long maxBytes=8L*1024*1024*1024)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var response=await GetAsync(url,ct);
        if(response.Content.Headers.ContentLength>maxBytes) throw new InvalidDataException("Download exceeds the configured size limit.");
        await using var input=await response.Content.ReadAsStreamAsync(ct);
        await using var output=new FileStream(destination,FileMode.Create,FileAccess.Write,FileShare.None,65536,true);
        var buffer=new byte[65536]; long count=0;
        for(int n;(n=await input.ReadAsync(buffer,ct))>0;)
        {
            count+=n; if(count>maxBytes) throw new InvalidDataException("Download exceeds the configured size limit.");
            await output.WriteAsync(buffer.AsMemory(0,n),ct); transferred?.Invoke(n);
        }
        await output.FlushAsync(ct);
    }
}

public sealed partial class MetadataProvider(RemoteClient remote, AppPaths paths, ILogger<MetadataProvider> log)
{
    public Catalog? Current { get; private set; }
    public bool IsOffline { get; private set; }
    public HashSet<int> ExcludedChampionIds { get; }=[];
    public static string AssetUrl(string patch, string language, string asset)
    {
        const string prefix="/lol-game-data/assets/";
        if(!asset.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unknown CommunityDragon asset path: {asset}");
        var tail=asset[prefix.Length..].ToLowerInvariant();
        if(tail.Contains("..") || tail.Contains('\\')) throw new InvalidDataException("Invalid CommunityDragon asset path.");
        return $"https://raw.communitydragon.org/{patch}/plugins/rcp-be-lol-game-data/global/{language}/{tail}";
    }
    public async Task<Catalog> LoadAsync(string language, bool refresh=false, IProgress<string>? progress=null, CancellationToken ct=default)
    {
        if(!Regex.IsMatch(language,"^[a-z]{2,8}(_[a-z]{2})?$")) throw new ArgumentException("Select a supported metadata language.");
        var cache=Path.Combine(paths.Metadata,$"catalog-{language}.json");
        var saved=await JsonFile.ReadAsync<Catalog>(cache,ct);
        if(saved is not null)
        {
            ExcludedChampionIds.UnionWith(saved.Champions.Where(c=>CatalogPolicy.IsClassicChampion(c.Alias)).Select(c=>c.Id));
            saved=CatalogPolicy.WithoutClassic(saved);
        }
        if(saved is not null && !refresh) { Current=saved; return saved; }
        try
        {
            using var v=await remote.JsonAsync("https://raw.communitydragon.org/latest/content-metadata.json",ct);
            var match=PatchRegex().Match(v.RootElement.GetProperty("version").GetString()!);
            if(!match.Success) throw new InvalidDataException("CommunityDragon returned an unrecognized patch version.");
            var patch=match.Value;
            var baseUrl=$"https://raw.communitydragon.org/{patch}/plugins/rcp-be-lol-game-data/global/{language}/v1";
            using var summary=await remote.JsonAsync(baseUrl+"/champion-summary.json",ct);
            var entries=summary.RootElement.EnumerateArray().Where(x=>x.GetProperty("id").GetInt32()>0).ToList();
            ExcludedChampionIds.UnionWith(entries.Where(x=>CatalogPolicy.IsClassicChampion(Text(x,"alias"))).Select(x=>x.GetProperty("id").GetInt32()));
            var ids=entries.Where(x=>!CatalogPolicy.IsClassicChampion(Text(x,"alias"))).Select(x=>x.GetProperty("id").GetInt32()).ToArray();
            var champions=new System.Collections.Concurrent.ConcurrentBag<Champion>(); int done=0;
            await Parallel.ForEachAsync(ids,new ParallelOptions{MaxDegreeOfParallelism=6,CancellationToken=ct},async (id,token)=>
            {
                using var doc=await remote.JsonAsync($"{baseUrl}/champions/{id}.json",token);
                champions.Add(ParseChampion(doc.RootElement,patch,language));
                progress?.Report($"Loading champion metadata · {Interlocked.Increment(ref done)}/{ids.Length}");
            });
            var catalog=new Catalog(patch,language,DateTimeOffset.UtcNow,champions.OrderBy(c=>c.Name,StringComparer.CurrentCultureIgnoreCase).ToList());
            await JsonFile.WriteAsync(cache,catalog,ct); Current=catalog; IsOffline=false; return catalog;
        }
        catch(Exception e) when(saved is not null && e is not OperationCanceledException)
        {
            log.LogWarning(e,"Metadata refresh failed; using cached patch {Patch}",saved.Patch);
            Current=saved; IsOffline=true; progress?.Report($"Offline · using cached patch {saved.Patch}. {e.Message}"); return saved;
        }
    }
    public static Champion ParseChampion(JsonElement j,string patch,string language)
    {
        string Url(JsonElement e,string key) => e.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.String ? AssetUrl(patch,language,v.GetString()!) : "";
        var skins=j.GetProperty("skins").EnumerateArray().Select(s=>new Skin(s.GetProperty("id").GetInt32(),Text(s,"name"),s.TryGetProperty("isBase",out var b)&&b.GetBoolean(),
            Url(s,"splashPath"),Url(s,"tilePath"),Text(s,"description"),s.TryGetProperty("chromas",out var chromas) && chromas.ValueKind==JsonValueKind.Array
                ? chromas.EnumerateArray().Select(c=>new Chroma(c.GetProperty("id").GetInt32(),Text(c,"name"),Url(c,"chromaPath"),
                    c.TryGetProperty("colors",out var colors) ? colors.EnumerateArray().Select(x=>x.GetString()??"#AC96E8").ToList():[])).ToList():[])).ToList();
        return new(j.GetProperty("id").GetInt32(),Text(j,"name"),Text(j,"alias"),Text(j,"title"),Url(j,"squarePortraitPath"),skins);
    }
    public static string Text(JsonElement e,string key) => e.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.String ? v.GetString()??"" : "";
    [GeneratedRegex(@"^\d+\.\d+")] private static partial Regex PatchRegex();
}

public sealed class ImageCache(RemoteClient remote, AppPaths paths)
{
    private readonly SemaphoreSlim slots=new(8,8);
    public async Task<string?> GetAsync(string url,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(url)) return null;
        if(Path.IsPathFullyQualified(url) && File.Exists(url)) return url;
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.Host!="raw.communitydragon.org") return null;
        var path=Path.Combine(paths.Images,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))+".image");
        if(File.Exists(path)) return path;
        await slots.WaitAsync(ct);
        var temp=path+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            if(!File.Exists(path)) { await remote.DownloadAsync(url,temp,ct,maxBytes:30*1024*1024); File.Move(temp,path,true); }
            return path;
        }
        catch(HttpRequestException) { return null; }
        finally { if(File.Exists(temp)) File.Delete(temp); slots.Release(); }
    }
}

public interface IRepositorySource
{
    Task<RepositoryRevision> ResolveAsync(CancellationToken ct);
    Task FetchAsync(RepositoryRevision revision, RepositoryFile file, string destination, Action<long> transferred, CancellationToken ct);
}

public sealed class GitHubRepository(RemoteClient remote) : IRepositorySource
{
    public const string Repository="Alban1911/LeagueSkins";
    public async Task<RepositoryRevision> ResolveAsync(CancellationToken ct)
    {
        using var repo=await remote.JsonAsync($"https://api.github.com/repos/{Repository}",ct);
        var branch=repo.RootElement.GetProperty("default_branch").GetString()!;
        using var commit=await remote.JsonAsync($"https://api.github.com/repos/{Repository}/commits/{Uri.EscapeDataString(branch)}",ct);
        var sha=commit.RootElement.GetProperty("sha").GetString()!;
        using var tree=await remote.JsonAsync($"https://api.github.com/repos/{Repository}/git/trees/{sha}?recursive=1",ct);
        List<RepositoryFile> files=[];
        if(!tree.RootElement.GetProperty("truncated").GetBoolean()) Add(tree.RootElement,"",files);
        else
        {
            // Git Trees has no pagination. Walk each non-recursive subtree when the recursive response is truncated.
            var pending=new Queue<(string Sha,string Prefix)>(); pending.Enqueue((sha,""));
            while(pending.TryDequeue(out var item))
            {
                using var subtree=await remote.JsonAsync($"https://api.github.com/repos/{Repository}/git/trees/{item.Sha}",ct);
                if(subtree.RootElement.GetProperty("truncated").GetBoolean()) throw new InvalidDataException("GitHub truncated a single directory; refusing an incomplete inventory.");
                Add(subtree.RootElement,item.Prefix,files);
                foreach(var e in subtree.RootElement.GetProperty("tree").EnumerateArray().Where(x=>x.GetProperty("type").GetString()=="tree"))
                    pending.Enqueue((e.GetProperty("sha").GetString()!,item.Prefix+e.GetProperty("path").GetString()+"/"));
            }
        }
        files=files.Where(f=>!CatalogPolicy.IsClassicRepositoryPath(f.Path)&&(IsPackage(f.Path) || f.Path.EndsWith(".png",StringComparison.OrdinalIgnoreCase))).ToList();
        if(files.Count==0 || !files.Any(f=>IsPackage(f.Path))) throw new InvalidDataException("Repository contains no supported packages.");
        return new(branch,sha,files);
    }
    private static void Add(JsonElement tree,string prefix,List<RepositoryFile> files)
    {
        foreach(var e in tree.GetProperty("tree").EnumerateArray())
            if(e.GetProperty("type").GetString()=="blob")
            {
                var path=prefix+e.GetProperty("path").GetString(); ArchiveSafety.ValidatePath(path);
                if(e.GetProperty("mode").GetString()=="120000") throw new InvalidDataException("Repository symlinks are not supported.");
                files.Add(new(path,e.GetProperty("sha").GetString()!,e.GetProperty("size").GetInt64()));
            }
    }
    public static bool IsPackage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".fantome" or ".modpkg" or ".zip";
    public async Task FetchAsync(RepositoryRevision r,RepositoryFile f,string destination,Action<long> transferred,CancellationToken ct)
    {
        var encoded=string.Join('/',f.Path.Split('/').Select(Uri.EscapeDataString));
        await remote.DownloadAsync($"https://raw.githubusercontent.com/{Repository}/{r.Commit}/{encoded}",destination,ct,transferred);
        if(new FileInfo(destination).Length!=f.Size) throw new InvalidDataException($"Download size mismatch: {f.Path}");
        await using(var s=File.OpenRead(destination))
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.UTF8.GetBytes($"blob {s.Length}\0"));
            var buffer=new byte[65536]; for(int n;(n=await s.ReadAsync(buffer,ct))>0;) hash.AppendData(buffer,0,n);
            if(!Convert.ToHexString(hash.GetHashAndReset()).Equals(f.Sha,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Git blob checksum mismatch: {f.Path}");
        }
        if(f.Size<1024)
        {
            var text=await File.ReadAllTextAsync(destination,ct);
            if(text.StartsWith("version https://git-lfs.github.com/spec/v1",StringComparison.Ordinal))
            {
                var oid=Regex.Match(text,@"oid sha256:([0-9a-f]{64})").Groups[1].Value;
                if(!long.TryParse(Regex.Match(text,@"size (\d+)").Groups[1].Value,out var size) || oid.Length!=64) throw new InvalidDataException("Invalid Git LFS pointer.");
                await remote.DownloadAsync($"https://media.githubusercontent.com/media/{Repository}/{r.Commit}/{encoded}",destination,ct,transferred);
                await using var actual=File.OpenRead(destination);
                if(actual.Length!=size || !Convert.ToHexString(await SHA256.HashDataAsync(actual,ct)).Equals(oid,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Git LFS verification failed: {f.Path}");
            }
        }
    }
}
