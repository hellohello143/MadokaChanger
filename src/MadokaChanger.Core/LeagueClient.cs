using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MadokaChanger.Core;

/// <summary>The League client's local API credentials, from its lockfile: <c>LeagueClient:pid:port:password:https</c>.</summary>
public sealed record LcuCredentials(int Port,string Password,string Protocol)
{
    public Uri BaseAddress=>new($"{Protocol}://127.0.0.1:{Port}");
    public static LcuCredentials Parse(string lockfile)
    {
        var parts=lockfile.Trim().Split(':');
        if(parts.Length<5 || !int.TryParse(parts[2],out var port) || port is <1 or >65535 || parts[3].Length==0)
            throw new InvalidDataException("The League client's lockfile isn't in the expected format.");
        var protocol=parts[4].Trim().ToLowerInvariant();
        if(protocol is not ("https" or "http")) throw new InvalidDataException("The League client's lockfile names an unknown protocol.");
        return new(port,parts[3],protocol);
    }
}

/// <summary>
/// Talks to the League client's local API (LCU) the way league-profile-tool does.
/// Ported from its background component: one POST of <c>{"key":"backgroundSkinId","value":id}</c>
/// to <c>/lol-summoner/v1/current-summoner/summoner-profile/</c>.
/// </summary>
public sealed class LeagueClientApi(ILogger<LeagueClientApi> log)
{
    public const string ProfileEndpoint="/lol-summoner/v1/current-summoner/summoner-profile/";

    /// <summary>The lockfile sits in the League install folder, one level above the Game folder MadokaChanger stores.</summary>
    public static IEnumerable<string> LockfileCandidates(string? gameFolder)
    {
        if(!string.IsNullOrWhiteSpace(gameFolder))
        {
            // TrimEndingDirectorySeparator keeps a root's separator; TrimEnd turned C:\ into the drive-relative C:.
            var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder));
            yield return Path.Combine(full,"lockfile");
            if(Path.GetDirectoryName(full) is { } parent) yield return Path.Combine(parent,"lockfile");
        }
        yield return @"C:\Riot Games\League of Legends\lockfile";
        yield return @"D:\Riot Games\League of Legends\lockfile";
    }

    public static LcuCredentials? FindCredentials(string? gameFolder)
    {
        // A stale or half-written lockfile must not hide a valid one further down the list.
        InvalidDataException? malformed=null;
        foreach(var path in LockfileCandidates(gameFolder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if(!File.Exists(path)) continue;
                // The running client keeps the lockfile open, so it must be read with shared access.
                using var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var r=new StreamReader(s);
                return LcuCredentials.Parse(r.ReadToEnd());
            }
            catch(InvalidDataException e) { malformed??=e; }
            catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }
        if(malformed is not null) throw malformed;
        return null;
    }

    public static string BackgroundBody(int skinId)=>JsonSerializer.Serialize(new{key="backgroundSkinId",value=skinId});

    public async Task SetProfileBackgroundAsync(int skinId,string? gameFolder,CancellationToken ct)
    {
        if(skinId<=0) throw new ArgumentOutOfRangeException(nameof(skinId),"Choose a skin first.");
        var credentials=FindCredentials(gameFolder) ?? throw new InvalidOperationException("The League client isn't running. Open it and log in, then try again.");
        using var handler=new HttpClientHandler
        {
            // The client serves a self-signed certificate on loopback only, so only loopback is trusted here.
            ServerCertificateCustomValidationCallback=(request,_,_,_)=>request.RequestUri is { IsLoopback:true },
            UseProxy=false
        };
        using var http=new HttpClient(handler){BaseAddress=credentials.BaseAddress,Timeout=TimeSpan.FromSeconds(15)};
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:"+credentials.Password)));
        using var content=new StringContent(BackgroundBody(skinId),Encoding.UTF8,"application/json");
        HttpResponseMessage response;
        try { response=await http.PostAsync(ProfileEndpoint,content,ct); }
        catch(HttpRequestException e) { throw new InvalidOperationException("Couldn't reach the League client. Make sure it's open and you're logged in. "+e.Message,e); }
        using(response)
        {
            if(response.IsSuccessStatusCode) { log.LogInformation("Profile background set to skin {Skin}",skinId); return; }
            var body=await response.Content.ReadAsStringAsync(ct);
            log.LogWarning("Profile background rejected: {Status} {Body}",(int)response.StatusCode,body);
            throw new InvalidOperationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>"The League client refused the request. Restart the client and try again.",
                HttpStatusCode.NotFound=>"You're not logged in yet. Finish logging in to the League client, then try again.",
                _=>$"The League client returned {(int)response.StatusCode}. {body}"
            });
        }
    }
}
