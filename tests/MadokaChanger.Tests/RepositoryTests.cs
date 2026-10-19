using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MadokaChanger.Core;
using Xunit;

namespace MadokaChanger.Tests;
public sealed class RepositoryTests
{
    [Fact] public async Task Truncated_recursive_inventory_walks_subtrees_at_the_resolved_commit()
    {
        var commit=new string('a',40);var requests=new List<string>();
        using var http=new HttpClient(new Handler(request=>
        {
            var url=request.RequestUri!.AbsoluteUri;requests.Add(url);
            if(url.EndsWith("/LeagueSkins"))return Json(new{default_branch="stable"});
            if(url.EndsWith("/commits/stable"))return Json(new{sha=commit});
            if(url.EndsWith("?recursive=1"))return Json(new{truncated=true,tree=Array.Empty<object>()});
            if(url.EndsWith("/trees/"+commit))return Json(new{truncated=false,tree=new[]{new{type="tree",sha="child",path="skins",mode="040000"}}});
            if(url.EndsWith("/trees/child"))return Json(new{truncated=false,tree=new[]{new{type="blob",sha="blob",path="103/103001/103001.fantome",mode="100644",size=10}}});
            throw new Exception("Unexpected request: "+url);
        }));
        var repo=new GitHubRepository(new RemoteClient(http,NullLogger<RemoteClient>.Instance));var result=await repo.ResolveAsync(CancellationToken.None);
        Assert.Equal(commit,result.Commit);Assert.Equal("stable",result.Branch);Assert.Single(result.Files);Assert.Equal("skins/103/103001/103001.fantome",result.Files[0].Path);
        Assert.Single(requests,u=>u.Contains("/commits/"));Assert.Equal(5,requests.Count);
    }
    [Fact] public async Task Git_lfs_payload_is_pinned_and_verified_after_its_pointer()
    {
        var root=Path.Combine(Path.GetTempPath(),"MadokaChanger-lfs-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            byte[] payload="materialized skin package fixture"u8.ToArray();var oid=Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var pointer=Encoding.UTF8.GetBytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize {payload.Length}\n");
            var blob=SHA1.HashData(Encoding.UTF8.GetBytes($"blob {pointer.Length}\0").Concat(pointer).ToArray());var commit=new string('b',40);var urls=new List<string>();
            using var http=new HttpClient(new Handler(request=>
            {
                var url=request.RequestUri!.AbsoluteUri;urls.Add(url);return new(HttpStatusCode.OK){Content=new ByteArrayContent(url.Contains("media.githubusercontent.com")?payload:pointer)};
            }));
            var repo=new GitHubRepository(new RemoteClient(http,NullLogger<RemoteClient>.Instance));var file=new RepositoryFile("skins/103/103001/103001.fantome",Convert.ToHexString(blob).ToLowerInvariant(),pointer.Length);
            var dest=Path.Combine(root,"package.fantome");var verified=await repo.FetchAsync(new("main",commit,[file]),file,dest,_=>{},CancellationToken.None);
            Assert.Equal(payload,await File.ReadAllBytesAsync(dest));Assert.Equal(2,urls.Count);Assert.All(urls,url=>Assert.Contains(commit,url));
            Assert.Equal(file.Sha,verified.Sha);Assert.Equal(pointer.Length,verified.Size);Assert.Equal(oid,verified.LfsOid);Assert.Equal(payload.Length,verified.LfsSize);
        }
        finally{Directory.Delete(root,true);}
    }
    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> get):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(get(request));
    }
}
