using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Pathman.Core.Updates;

public sealed record ReleaseAsset(string Name,Uri Download,long Size,string? Digest);
public sealed record AvailableRelease(string Tag,string Title,string Body,Uri Page,IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseManifest? Manifest {get;init;}
    public bool Prerelease {get;init;}
}

public static class ReleaseIdentity
{
    public static string Normalize(string tag)=>tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
    public static bool IsUpdate(AvailableRelease release,string currentNumber,bool receivePrerelease)
    {
        if(release.Manifest is not {} manifest || !BuildVersion.TryParse(currentNumber,out var current))return false;
        if((release.Prerelease || manifest.Prerelease) && !receivePrerelease || manifest.Number.Z>0)return false;
        return manifest.Number.CompareTo(current)>0;
    }
}

public sealed class GitHubReleaseClient(HttpClient client)
{
    public const string Repository="Napreth/PathMan";
    public static readonly Uri RepositoryPage=new("https://github.com/"+Repository);
    public async Task<AvailableRelease?> GetLatestAsync(bool receivePrerelease=false,CancellationToken cancellationToken=default)
    {
        var endpoint="https://api.github.com/repos/"+Repository+"/releases";
        AvailableRelease? latest=null;
        using var parallelism=new SemaphoreSlim(6);
        for(var page=1;page<=20;page++)
        {
            var json=await ReadAsync(endpoint+"?per_page=100"+(page==1?"":"&page="+page),8*1024*1024,cancellationToken);
            if(json is null)return latest;
            using var document=JsonDocument.Parse(json);var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Invalid release list.");
            var releases=await Task.WhenAll(root.EnumerateArray().Select(async element=>
            {
                await parallelism.WaitAsync(cancellationToken);
                try{return await ParseAsync(element,receivePrerelease,cancellationToken);}finally{parallelism.Release();}
            }));
            foreach(var release in releases)
            {
                if(release is null)continue;
                if(latest is null || release.Manifest!.Number.CompareTo(latest.Manifest!.Number)>0)latest=release;
            }
            if(root.GetArrayLength()<100)return latest;
        }
        throw new InvalidDataException("Release list is too large.");
    }
    private async Task<AvailableRelease?> ParseAsync(JsonElement release,bool receivePrerelease,CancellationToken cancellationToken)
    {
        if(release.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("Invalid release response.");
        if(!release.TryGetProperty("draft",out var draft) || draft.ValueKind!=JsonValueKind.False ||
           !release.TryGetProperty("prerelease",out var prerelease) || prerelease.ValueKind is not (JsonValueKind.False or JsonValueKind.True) || !receivePrerelease && prerelease.ValueKind==JsonValueKind.True) return null;
        var tag=String(release,"tag_name");
        if(string.IsNullOrWhiteSpace(tag) || tag.Length>200) throw new InvalidDataException("Invalid release tag.");
        var assets=new List<ReleaseAsset>();
        if(release.TryGetProperty("assets",out var array) && array.ValueKind==JsonValueKind.Array)
            foreach(var asset in array.EnumerateArray())
            {
                var name=String(asset,"name");var url=String(asset,"browser_download_url");
                if(name is null || !Uri.TryCreate(url,UriKind.Absolute,out var uri) || !ReleaseInstaller.IsDownloadUri(uri)) continue;
                var size=asset.TryGetProperty("size",out var length) && length.TryGetInt64(out var value) ? value : 0;
                assets.Add(new(name,uri,size,String(asset,"digest")));
            }
        if(assets.FirstOrDefault(asset=>asset.Name=="release.json") is not {} metadata)return null;
        var json=await ReadAsync(metadata.Download.AbsoluteUri,65536,cancellationToken);
        if(json is null)return null;
        ReleaseManifest manifest;
        try{manifest=ReleaseManifest.Parse(json);}catch(Exception error) when(error is JsonException or InvalidDataException){return null;}
        if(manifest.Number.Z>0 || !receivePrerelease && manifest.Prerelease)return null;
        return new(tag,String(release,"name") is {Length:>0} title ? title : tag,String(release,"body") ?? "",
            new Uri(RepositoryPage.AbsoluteUri+"/releases/tag/"+Uri.EscapeDataString(tag)),assets)
        {Manifest=manifest,Prerelease=prerelease.ValueKind==JsonValueKind.True};
    }
    private async Task<string?> ReadAsync(string uri,int maximum,CancellationToken cancellationToken)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PathMan","1"));
        if(request.RequestUri!.Host=="api.github.com") {request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));request.Headers.Add("X-GitHub-Api-Version","2022-11-28");}
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        using var input=await response.Content.ReadAsStreamAsync(cancellationToken);using var bounded=new MemoryStream();
        var buffer=new byte[16384];int count;
        while((count=await input.ReadAsync(buffer,cancellationToken))>0)
        {if(bounded.Length+count>maximum)throw new InvalidDataException("Release response is too large.");await bounded.WriteAsync(buffer.AsMemory(0,count),cancellationToken);}
        return System.Text.Encoding.UTF8.GetString(bounded.ToArray());
    }
    private static string? String(JsonElement value,string property)=>value.TryGetProperty(property,out var item) && item.ValueKind==JsonValueKind.String ? item.GetString() : null;
}
