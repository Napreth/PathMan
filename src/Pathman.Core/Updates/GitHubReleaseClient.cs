using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Pathman.Core.Updates;

public sealed record ReleaseAsset(string Name,Uri Download,long Size,string? Digest);
public sealed record AvailableRelease(string Tag,string Title,string Body,Uri Page,IReadOnlyList<ReleaseAsset> Assets);

public static class ReleaseIdentity
{
    public static string Normalize(string tag)=>tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
    public static bool IsDifferent(string tag,string current)=>!string.Equals(Normalize(tag),Normalize(current),StringComparison.Ordinal);
}

public sealed class GitHubReleaseClient(HttpClient client)
{
    public const string Repository="Napreth/PathMan";
    public static readonly Uri RepositoryPage=new("https://github.com/"+Repository);
    public async Task<AvailableRelease?> GetLatestAsync(string currentVersion,CancellationToken cancellationToken=default)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.github.com/repos/"+Repository+"/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PathMan",currentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version","2022-11-28");
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken);
        if(response.StatusCode==HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        // Bound the response before parsing untrusted release metadata.
        using var input=await response.Content.ReadAsStreamAsync(cancellationToken);using var bounded=new MemoryStream();
        var buffer=new byte[16384];int count;
        while((count=await input.ReadAsync(buffer,cancellationToken))>0)
        {if(bounded.Length+count>8*1024*1024) throw new InvalidDataException("Release response is too large.");await bounded.WriteAsync(buffer.AsMemory(0,count),cancellationToken);}
        bounded.Position=0;using var document=await JsonDocument.ParseAsync(bounded,cancellationToken:cancellationToken);
        var release=document.RootElement;
        if(release.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("Invalid release response.");
        if(!release.TryGetProperty("draft",out var draft) || draft.ValueKind!=JsonValueKind.False ||
           !release.TryGetProperty("prerelease",out var prerelease) || prerelease.ValueKind!=JsonValueKind.False) return null;
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
        return new(tag,String(release,"name") is {Length:>0} title ? title : tag,String(release,"body") ?? "",
            new Uri(RepositoryPage.AbsoluteUri+"/releases/tag/"+Uri.EscapeDataString(tag)),assets);
    }
    private static string? String(JsonElement value,string property)=>value.TryGetProperty(property,out var item) && item.ValueKind==JsonValueKind.String ? item.GetString() : null;
}
