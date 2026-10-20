using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MadokaChanger.Core;
using Xunit;

namespace MadokaChanger.Tests;

public sealed class ReleaseParserTests
{
    private static ReleaseInfo Release(string tag,params string[] assetNames)=>
        new(tag,ReleaseParser.ParseVersion(tag)!,"",assetNames.Select(n=>new ReleaseAsset(n,"https://github.com/x/y/releases/download/"+n,1,new string('a',64))).ToList());

    [Fact] public void Parse_reads_tag_body_and_assets_with_lowercased_sha()
    {
        using var doc=JsonDocument.Parse("""
        {"tag_name":"v1.28.1","body":"notes here","assets":[
          {"name":"LTK.Manager_1.28.1_x64-setup.exe","browser_download_url":"https://github.com/a/b/x.exe","size":1234,"digest":"sha256:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789"},
          {"name":"no-digest.zip","browser_download_url":"https://github.com/a/b/y.zip","size":5,"digest":null},
          {"name":"md5.zip","browser_download_url":"https://github.com/a/b/z.zip","digest":"md5:abc"}]}
        """);
        var r=ReleaseParser.Parse(doc.RootElement);
        Assert.Equal("v1.28.1",r.Tag);Assert.Equal(new Version(1,28,1),r.Version);Assert.Equal("notes here",r.Notes);
        Assert.Equal(3,r.Assets.Count);
        Assert.Equal(new ReleaseAsset("LTK.Manager_1.28.1_x64-setup.exe","https://github.com/a/b/x.exe",1234,"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"),r.Assets[0]);
        Assert.Null(r.Assets[1].Sha256);Assert.Null(r.Assets[2].Sha256);Assert.Equal(0,r.Assets[2].Size);
    }

    [Fact] public void Parse_without_assets_gives_empty_list()
    {
        using var doc=JsonDocument.Parse("""{"tag_name":"1.0.1"}""");
        var r=ReleaseParser.Parse(doc.RootElement);Assert.Empty(r.Assets);Assert.Equal("",r.Notes);
    }

    [Theory]
    [InlineData("""{"tag_name":"latest"}""")]
    [InlineData("""{"body":"no tag"}""")]
    [InlineData("""{"tag_name":"build-20261018123456"}""")]
    public void Parse_rejects_non_version_tags_with_InvalidDataException(string json)
    {
        using var doc=JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(()=>ReleaseParser.Parse(doc.RootElement));
    }

    [Theory]
    [InlineData("v1.28.1",1,28,1)]
    [InlineData("1.0.1",1,0,1)]
    [InlineData("LTK Manager 1.28.1",1,28,1)]
    [InlineData("1.1",1,1,0)]
    [InlineData("v2",2,0,0)]
    public void ParseVersion_reads_three_part_versions(string text,int major,int minor,int build)=>
        Assert.Equal(new Version(major,minor,build),ReleaseParser.ParseVersion(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("99999999999")]
    public void ParseVersion_returns_null_for_non_versions(string? text)=>Assert.Null(ReleaseParser.ParseVersion(text));

    [Fact] public void Normalize_drops_revision_and_fills_missing_parts()
    {
        Assert.Equal(new Version(1,0,1),ReleaseParser.Normalize(new Version(1,0,1,0)));
        Assert.Equal(new Version(1,1,0),ReleaseParser.Normalize(new Version(1,1)));
        Assert.Equal(ReleaseParser.Normalize(new Version(1,1,0,0)),ReleaseParser.Normalize(new Version(1,1,0)));
    }

    [Theory]
    [InlineData("LTK.Manager_1.28.1_x64-setup.exe",true)]
    [InlineData("LTK.Manager_x64-setup.exe",true)]
    [InlineData("ltk.manager_1.28.1_X64-SETUP.EXE",true)]
    [InlineData("LTK.Manager_1.28.1_x64_en-US.msi",false)]
    [InlineData("LTK.Manager_1.28.1_x64-setup.exe.sig",false)]
    [InlineData("LTK.Manager_1.28.1_arm64-setup.exe",false)]
    [InlineData("LTK.Manager_1.28.1_x64_en-US.msi.sig",false)]
    [InlineData("latest.json",false)]
    public void LtkSetupAsset_matches_only_the_x64_nsis_installer(string name,bool matches)
    {
        var a=UpdateService.LtkSetupAsset(Release("v1.28.1",name));
        if(matches)Assert.Equal(name,a?.Name);else Assert.Null(a);
    }

    [Fact] public void LtkSetupAsset_picks_the_installer_among_real_asset_lists()
    {
        var r=Release("v1.28.1","LTK.Manager_1.28.1_arm64-setup.exe","LTK.Manager_1.28.1_x64-setup.exe.sig","LTK.Manager_1.28.1_x64_en-US.msi","LTK.Manager_1.28.1_x64-setup.exe","latest.json");
        Assert.Equal("LTK.Manager_1.28.1_x64-setup.exe",UpdateService.LtkSetupAsset(r)?.Name);
    }

    [Theory]
    [InlineData("MadokaChanger-Setup-1.0.2.exe",true)]
    [InlineData("madokachanger-setup-1.0.2.EXE",true)]
    [InlineData("MadokaChanger-Setup-1.0.2.exe.sig",false)]
    [InlineData("MadokaChanger-1.0.2-portable.zip",false)]
    [InlineData("Setup-MadokaChanger.exe",false)]
    public void AppSetupAsset_matches_only_the_setup_exe(string name,bool matches)
    {
        var a=UpdateService.AppSetupAsset(Release("v1.0.2",name));
        if(matches)Assert.Equal(name,a?.Name);else Assert.Null(a);
    }
}

public sealed class UpdateReportTests
{
    [Fact] public void App_update_targets_the_running_install_and_requests_relaunch_without_dependency_setup()
    {
        var start=UpdateService.AppSetupStartInfo(@"C:\Downloaded updates\MadokaChanger-Setup-1.2.0.exe");
        Assert.Equal(@"C:\Downloaded updates\MadokaChanger-Setup-1.2.0.exe",start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Contains("/VERYSILENT",start.Arguments);
        Assert.Contains("/RELAUNCH=1",start.Arguments);
        Assert.Contains("/SKIPLTK=1",start.Arguments);
        Assert.Contains("/DIR=\""+Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)+"\"",start.Arguments);
    }

    private static ReleaseInfo App(string tag,bool withSetup=true)=>new(tag,ReleaseParser.ParseVersion(tag)!,"",withSetup?[new("MadokaChanger-Setup-"+tag.TrimStart('v')+".exe","https://github.com/a/b/s.exe",1,new string('a',64))]:[]);
    private static ReleaseInfo Ltk(string tag,bool withSetup=true)=>new(tag,ReleaseParser.ParseVersion(tag)!,"",withSetup?[new("LTK.Manager_"+tag.TrimStart('v')+"_x64-setup.exe","https://github.com/a/b/l.exe",1,new string('b',64))]:[]);
    private static readonly LtkInstall Signed=new(new Version(1,28,1),@"C:\LTK",true);

    [Fact] public void Assembly_version_with_revision_is_not_an_update_for_the_same_tag()
    {
        var r=new UpdateReport(new Version(1,0,1,0),App("v1.0.1"),Signed,Ltk("v1.28.1"),null);
        Assert.False(r.AppUpdateAvailable);
        Assert.Equal("MadokaChanger v1.0.1 · up to date\nLTK Manager v1.28.1 · signed patcher ready · up to date",r.Summary);
    }

    [Fact] public void Newer_tag_with_setup_asset_is_an_update()
    {
        var r=new UpdateReport(new Version(1,0,1,0),App("v1.0.2"),Signed,null,null);
        Assert.True(r.AppUpdateAvailable);Assert.StartsWith("MadokaChanger v1.0.1 → v1.0.2 available\n",r.Summary);
    }

    [Fact] public void Newer_tag_without_setup_asset_is_not_an_update()=>
        Assert.False(new UpdateReport(new Version(1,0,1),App("v1.0.2",withSetup:false),Signed,null,null).AppUpdateAvailable);

    [Fact] public void Older_tag_is_not_an_update()=>
        Assert.False(new UpdateReport(new Version(1,0,2),App("v1.0.1"),Signed,null,null).AppUpdateAvailable);

    [Fact] public void Unknown_latest_release_reports_unknown_and_problem()
    {
        var r=new UpdateReport(new Version(1,0,1),null,new LtkInstall(null,null,false),null,"Couldn't check.");
        Assert.False(r.AppUpdateAvailable);Assert.True(r.LtkMissing);Assert.False(r.LtkActionNeeded);
        Assert.Equal("MadokaChanger v1.0.1 · latest unknown\nLTK Manager not installed\nCouldn't check.",r.Summary);
    }

    [Fact] public void Missing_ltk_needs_action_when_an_installer_is_published()
    {
        var r=new UpdateReport(new Version(1,0,1),null,new LtkInstall(null,null,false),Ltk("v1.28.1"),null);
        Assert.True(r.LtkMissing);Assert.False(r.LtkUpdateAvailable);Assert.True(r.LtkActionNeeded);
        Assert.EndsWith("LTK Manager not installed · v1.28.1 will be installed",r.Summary);
    }

    [Fact] public void Ltk_installed_without_signed_patcher_counts_as_missing()
    {
        var r=new UpdateReport(new Version(1,0,1),null,new LtkInstall(new Version(1,27,0),@"C:\LTK",false),Ltk("v1.28.1"),null);
        Assert.True(r.LtkMissing);Assert.True(r.LtkActionNeeded);
        Assert.EndsWith("LTK Manager v1.27.0 · patcher missing or unsigned · v1.28.1 will be installed",r.Summary);
    }

    [Fact] public void Older_signed_ltk_has_an_update()
    {
        var r=new UpdateReport(new Version(1,0,1),null,new LtkInstall(new Version(1,27,0),@"C:\LTK",true),Ltk("v1.28.1"),null);
        Assert.False(r.LtkMissing);Assert.True(r.LtkUpdateAvailable);Assert.True(r.LtkActionNeeded);
        Assert.EndsWith("LTK Manager v1.27.0 · signed patcher ready → v1.28.1 available",r.Summary);
    }

    [Fact] public void Ltk_update_without_installer_asset_needs_no_action()
    {
        var r=new UpdateReport(new Version(1,0,1),null,new LtkInstall(new Version(1,27,0),@"C:\LTK",true),Ltk("v1.28.1",withSetup:false),null);
        Assert.True(r.LtkUpdateAvailable);Assert.False(r.LtkActionNeeded);
    }

    [Fact] public void Signed_ltk_of_unknown_version_is_not_offered_an_update()
    {
        var r=new UpdateReport(new Version(1,0,1),null,new LtkInstall(null,null,true),Ltk("v1.28.1"),null);
        Assert.False(r.LtkMissing);Assert.False(r.LtkUpdateAvailable);Assert.False(r.LtkActionNeeded);
        Assert.EndsWith("LTK Manager installed · signed patcher ready · up to date",r.Summary);
    }
}

public sealed class DownloadGuardTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"MadokaChanger-update-tests-"+Guid.NewGuid().ToString("N"));
    private readonly HttpClient http=new(new ThrowingHandler());
    private UpdateService Service()=>new(new RemoteClient(http,NullLogger<RemoteClient>.Instance),new AppPaths(root),new NativeBridge(NullLogger<NativeBridge>.Instance),NullLogger<UpdateService>.Instance);
    private static readonly string Sha=new('a',64);

    [Theory]
    [InlineData("http://github.com/a/b/setup.exe")]
    [InlineData("https://evil.example/setup.exe")]
    [InlineData("https://github.com.evil.example/setup.exe")]
    [InlineData("https://github.com@evil.example/setup.exe")]
    [InlineData("https://objects.githubusercontent.com/setup.exe")]
    [InlineData("file:///C:/setup.exe")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Refuses_downloads_outside_https_github_before_any_request(string url)
    {
        var e=await Assert.ThrowsAsync<InvalidDataException>(()=>Service().DownloadVerifiedAsync(new("setup.exe",url,1,Sha),null,CancellationToken.None));
        Assert.Contains("Unexpected download address",e.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public async Task Refuses_assets_without_a_sha256(string? sha)
    {
        var e=await Assert.ThrowsAsync<InvalidDataException>(()=>Service().DownloadVerifiedAsync(new("setup.exe","https://github.com/a/b/setup.exe",1,sha),null,CancellationToken.None));
        Assert.Contains("no SHA-256",e.Message);
    }

    private sealed class ThrowingHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new Xunit.Sdk.XunitException("No request may be sent: "+request.RequestUri);
    }

    public void Dispose()
    {
        http.Dispose();
        try{if(Directory.Exists(root))Directory.Delete(root,true);}catch(IOException){}
    }
}

public sealed class LeagueClientTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"MadokaChanger-lcu-tests-"+Guid.NewGuid().ToString("N"));
    public LeagueClientTests()=>Directory.CreateDirectory(root);

    [Fact] public void Parse_reads_port_password_and_protocol()
    {
        var c=LcuCredentials.Parse("LeagueClient:1234:54321:Ab-c_dEf123:https\r\n");
        Assert.Equal(new LcuCredentials(54321,"Ab-c_dEf123","https"),c);
        Assert.Equal(new Uri("https://127.0.0.1:54321"),c.BaseAddress);
        Assert.Equal("http",LcuCredentials.Parse("LeagueClient:1:80:pw:HTTP").Protocol);
    }

    [Theory]
    [InlineData("")]
    [InlineData("LeagueClient:1234:54321:pw")]
    [InlineData("LeagueClient:1234:port:pw:https")]
    [InlineData("LeagueClient:1234:0:pw:https")]
    [InlineData("LeagueClient:1234:70000:pw:https")]
    [InlineData("LeagueClient:1234:54321::https")]
    [InlineData("LeagueClient:1234:54321:pw:ftp")]
    // Riot's lockfile passwords are base64url-style and never contain ':'. A colon would shift the protocol field,
    // so such a lockfile is rejected as malformed rather than silently truncating the password.
    [InlineData("LeagueClient:1234:54321:pa:ss:https")]
    public void Parse_rejects_malformed_lockfiles(string text)=>Assert.Throws<InvalidDataException>(()=>LcuCredentials.Parse(text));

    [Fact] public void LockfileCandidates_try_the_game_folder_then_its_parent_then_defaults()
    {
        Assert.Equal(new[]{@"C:\Riot Games\League of Legends\Game\lockfile",@"C:\Riot Games\League of Legends\lockfile",@"C:\Riot Games\League of Legends\lockfile",@"D:\Riot Games\League of Legends\lockfile"},
            LeagueClientApi.LockfileCandidates(@"C:\Riot Games\League of Legends\Game\").ToArray());
        Assert.Equal(new[]{@"C:\Riot Games\League of Legends\lockfile",@"D:\Riot Games\League of Legends\lockfile"},LeagueClientApi.LockfileCandidates(null).ToArray());
        Assert.Equal(new[]{@"C:\Riot Games\League of Legends\lockfile",@"D:\Riot Games\League of Legends\lockfile"},LeagueClientApi.LockfileCandidates("  ").ToArray());
    }

    [Fact] public void LockfileCandidates_keep_a_drive_root_absolute()=>
        Assert.Equal(@"C:\lockfile",LeagueClientApi.LockfileCandidates(@"C:\").First());

    [Fact] public void FindCredentials_reads_a_lockfile_the_client_holds_open()
    {
        var install=Path.Combine(root,"League of Legends");var game=Path.Combine(install,"Game");Directory.CreateDirectory(game);
        var lockfile=Path.Combine(install,"lockfile");
        using var held=new FileStream(lockfile,FileMode.Create,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete);
        var bytes=Encoding.ASCII.GetBytes("LeagueClient:4321:61234:secretPw:https");held.Write(bytes);held.Flush();
        Assert.Equal(new LcuCredentials(61234,"secretPw","https"),LeagueClientApi.FindCredentials(game));
    }

    [Fact] public void FindCredentials_skips_a_malformed_lockfile_and_uses_the_next_candidate()
    {
        var install=Path.Combine(root,"League of Legends");var game=Path.Combine(install,"Game");Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game,"lockfile"),"");
        File.WriteAllText(Path.Combine(install,"lockfile"),"LeagueClient:1:2345:pw:https");
        Assert.Equal(new LcuCredentials(2345,"pw","https"),LeagueClientApi.FindCredentials(game));
    }

    [Fact] public void BackgroundBody_matches_league_profile_tool()=>
        Assert.Equal("""{"key":"backgroundSkinId","value":123}""",LeagueClientApi.BackgroundBody(123));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task SetProfileBackground_requires_a_skin(int skin)=>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>new LeagueClientApi(NullLogger<LeagueClientApi>.Instance).SetProfileBackgroundAsync(skin,root,CancellationToken.None));

    [Fact] public async Task SetProfileBackground_posts_the_body_with_riot_basic_auth()
    {
        using var server=new FakeLcu(200);
        File.WriteAllText(Path.Combine(root,"lockfile"),$"LeagueClient:1:{server.Port}:s3cr3t-Pw:http");
        await new LeagueClientApi(NullLogger<LeagueClientApi>.Instance).SetProfileBackgroundAsync(103001,root,CancellationToken.None);
        var req=await server.Request;
        Assert.Equal("POST",req.Method);Assert.Equal(LeagueClientApi.ProfileEndpoint,req.Path);
        Assert.Equal("Basic "+Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:s3cr3t-Pw")),req.Headers["authorization"]);
        Assert.StartsWith("application/json",req.Headers["content-type"]);
        Assert.Equal("""{"key":"backgroundSkinId","value":103001}""",req.Body);
    }

    [Theory]
    [InlineData(404,"not logged in")]
    [InlineData(401,"refused the request")]
    [InlineData(500,"returned 500")]
    public async Task SetProfileBackground_maps_errors_to_messages(int status,string expected)
    {
        using var server=new FakeLcu(status);
        File.WriteAllText(Path.Combine(root,"lockfile"),$"LeagueClient:1:{server.Port}:pw:http");
        var e=await Assert.ThrowsAsync<InvalidOperationException>(()=>new LeagueClientApi(NullLogger<LeagueClientApi>.Instance).SetProfileBackgroundAsync(1,root,CancellationToken.None));
        Assert.Contains(expected,e.Message);
    }

    /// <summary>A one-request HTTP/1.1 server on loopback. TcpListener needs no URL ACL, unlike HttpListener on 127.0.0.1.</summary>
    private sealed class FakeLcu:IDisposable
    {
        public sealed record Received(string Method,string Path,Dictionary<string,string> Headers,string Body);
        private readonly TcpListener listener=new(IPAddress.Loopback,0);
        public int Port{get;}
        public Task<Received> Request{get;}
        public FakeLcu(int status){listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;Request=ServeAsync(status);}
        private async Task<Received> ServeAsync(int status)
        {
            using var client=await listener.AcceptTcpClientAsync();using var stream=client.GetStream();
            var buffer=new List<byte>();var one=new byte[1];
            while(!(buffer.Count>=4&&buffer[^4]=='\r'&&buffer[^3]=='\n'&&buffer[^2]=='\r'&&buffer[^1]=='\n'))
            {if(await stream.ReadAsync(one)==0)break;buffer.Add(one[0]);}
            var lines=Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n",StringSplitOptions.RemoveEmptyEntries);
            var first=lines[0].Split(' ');
            var headers=lines.Skip(1).Select(l=>l.Split(':',2)).ToDictionary(p=>p[0].Trim().ToLowerInvariant(),p=>p[1].Trim());
            var body=new byte[headers.TryGetValue("content-length",out var len)?int.Parse(len):0];
            for(int read=0;read<body.Length;)read+=await stream.ReadAsync(body.AsMemory(read));
            var reply=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}");
            await stream.WriteAsync(reply);await stream.FlushAsync();
            return new(first[0],first[1],headers,Encoding.UTF8.GetString(body));
        }
        public void Dispose()=>listener.Stop();
    }

    public void Dispose(){try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
}
