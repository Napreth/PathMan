using System.Net;
using System.Net.Http;
using System.Text.Json;
using Pathman.Core.Updates;
using Pathman.Core.EnvironmentVariables;

internal static class ReleaseIdentityTests
{
    private sealed class Handler(Dictionary<string,string> responses) : HttpMessageHandler
    {
        public List<string> Requests {get;}=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var path=request.RequestUri!.PathAndQuery;Requests.Add(path);
            return Task.FromResult(new HttpResponseMessage(responses.ContainsKey(path)?HttpStatusCode.OK:HttpStatusCode.NotFound)
                {RequestMessage=request,Content=new StringContent(responses.GetValueOrDefault(path,"{}"))});
        }
    }
    public static void Run(Action<bool,string> assert)
    {
        foreach(var value in new[]{"0.0.0","1.0.0","9.3.2","65535.65535.65535"})assert(BuildVersion.TryParse(value,out var parsed)&&parsed.ToString()==value,"Numeric build identity roundtrip");
        foreach(var value in new[]{"1.2","1.2.3.4","01.2.3","1.-1.0","1.2.65536","1.2.3-beta"," 1.2.3"})assert(!BuildVersion.TryParse(value,out _),"Malformed build number rejected");
        var official=ReleaseManifest.Parse("""{"versionName":"1.0.0","versionNumber":"3.0.0","type":"official"}""");
        var beta=ReleaseManifest.Parse("""{"versionName":"1.0.0-beta.1","versionNumber":"1.0.0","type":"beta"}""");
        var test=ReleaseManifest.Parse("""{"versionName":"1.0.0","versionNumber":"3.2.0","type":"development"}""");
        var dev=ReleaseManifest.Parse("""{"versionName":"1.0.0","versionNumber":"3.2.7","type":"development"}""");
        assert(!official.Prerelease&&beta.Prerelease&&test.Prerelease&&dev.Type=="development","Release types are independent of the product name; Y builds are public development releases");
        foreach(var json in new[]{"""{"versionName":"1.0.0","versionNumber":"3.0.0"}""","""{"versionName":"1.0.0","versionNumber":"3.0.0","type":"test"}""","""{"versionName":"1.0.0","versionNumber":"3.1.0","type":"official"}""","""{"versionName":"1.0.0","versionNumber":"broken"}"""})
        {var rejected=false;try{ReleaseManifest.Parse(json);}catch(InvalidDataException){rejected=true;}assert(rejected,"Manifests require an explicit type consistent with the build identity");}
        AvailableRelease Release(ReleaseManifest manifest)=>new("test/"+manifest.VersionNumber,"title","body",new("https://github.com/Napreth/PathMan/releases"),[]) {Manifest=manifest,Prerelease=manifest.Prerelease};
        assert(!ReleaseIdentity.IsUpdate(Release(beta),"0.9.0","0.0.0",false)&&ReleaseIdentity.IsUpdate(Release(beta),"0.9.0","0.0.0",true),"Beta updates require opting in");
        assert(!ReleaseIdentity.IsUpdate(Release(test),"1.0.0","3.1.0",false)&&ReleaseIdentity.IsUpdate(Release(test),"1.0.0","3.1.0",true),"Same-name Y builds are detected by build number");
        assert(!ReleaseIdentity.IsUpdate(Release(test),"0.1.0","3.2.0",true)&&!ReleaseIdentity.IsUpdate(Release(test),"0.1.0","4.0.0",true)&&!ReleaseIdentity.IsUpdate(Release(dev),"0.1.0","0.0.0",true),"Equal, older and local-development identities are not offered");
        assert(official.Number.CompareTo(new(2,65535,65535))>0,"A new X sorts after all previous Y/Z builds");
        var sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fixture"))).ToLowerInvariant();
        var responses=new Dictionary<string,string>();
        string Json(string tag,string number,string type,bool prerelease)
        {
            var url="https://github.com/Napreth/PathMan/releases/download/"+tag+"/release.json";
            responses[new Uri(url).PathAndQuery]=JsonSerializer.Serialize(new {versionName="1.0.0",versionNumber=number,type,installers=new[]{new {fileName="custom-setup.exe",architecture="x64",selfContained=true,size=7,sha256=sha}}});
            return JsonSerializer.Serialize(new {draft=false,prerelease,tag_name=tag,name=tag,body="notes",published_at="2026-10-28T00:00:00Z",assets=new[]{new {name="release.json",browser_download_url=url,size=100}}});
        }
        var stable=Json("v1.0.0","3.0.0","official",false);
        var development=Json("test/3.2.0","3.2.0","development",true);
        var older=Json("test/3.1.0","3.1.0","development",true);
        responses["/repos/Napreth/PathMan/releases/latest"]=stable;
        responses["/repos/Napreth/PathMan/releases?per_page=100"]="["+older+","+stable+","+development+"]";
        var handler=new Handler(responses);using var http=new HttpClient(handler);var client=new GitHubReleaseClient(http);
        assert(client.GetLatestAsync("1.0.0").GetAwaiter().GetResult()?.Manifest?.VersionNumber=="3.0.0","Default checks use the official release endpoint and release.json");
        var latest=client.GetLatestAsync("1.0.0",true).GetAwaiter().GetResult();
        assert(latest?.Manifest is {VersionNumber:"3.2.0",Type:"development",Installers.Count:1},"Opt-in checks read installer metadata and choose the highest numeric build across all branches");
        var package=new UpdatePackage("x64",true);
        var installer=new ReleaseAsset("custom-setup.exe",new("https://github.com/Napreth/PathMan/releases/download/test%2F3.2.0/a.exe"),7,null);
        var entry=new ReleasePackage(installer.Name,"x64",true,installer.Size,sha);
        var manifest=test with {Installers=[entry]};
        var selected=package.Select(latest! with {Assets=[installer],Manifest=manifest});
        assert(selected is not null && selected.Name==installer.Name && selected.Digest=="sha256:"+sha,"Installer selection uses manifest architecture/runtime fields and its checksum, including nonstandard filenames");
        assert(new UpdatePackage("arm64",true).Select(latest! with {Assets=[installer],Manifest=manifest}) is null && new UpdatePackage("x64",false).Select(latest! with {Assets=[installer],Manifest=manifest}) is null,"Missing architecture/runtime entries use the release-page fallback");
        foreach(var invalid in new[]{manifest with {Installers=[entry,entry]},manifest with {Installers=[entry with {Sha256="bad"}]},manifest with {Installers=[entry with {Size=0}]},manifest with {Installers=[entry with {FileName="../escape.exe"}]},manifest with {Installers=[entry with {Architecture="unknown"}]}})
        {var rejected=false;try{ReleaseManifest.Parse(JsonSerializer.Serialize(invalid));}catch(InvalidDataException){rejected=true;}assert(rejected,"Unsafe, duplicate or incomplete installer entries are rejected");}
        var directory=Path.Combine(Path.GetTempPath(),"PathMan.Release.Tests",Guid.NewGuid().ToString("N"));
        try{
            responses[installer.Download.PathAndQuery]="fixture";
            var path=new ReleaseInstaller(http,directory).DownloadAsync(selected!).GetAwaiter().GetResult();
            assert(File.ReadAllText(path)=="fixture","The manifest checksum verifies the downloaded installer even when the GitHub asset has no digest");
            var bad=package.Select(latest! with {Assets=[installer],Manifest=manifest with {Installers=[entry with {Sha256=new string('0',64)}]}});
            var invalidHash=false;try{new ReleaseInstaller(http,directory).DownloadAsync(bad!).GetAwaiter().GetResult();}catch(InvalidDataException){invalidHash=true;}assert(invalidHash,"Downloaded bytes must match the checksum selected from the release manifest");
            var mismatch=false;try{package.Select(latest! with {Assets=[installer with {Size=8}],Manifest=manifest});}catch(InvalidDataException){mismatch=true;}assert(mismatch,"Manifest and GitHub asset size mismatches prevent installation");
            var state=new EnvironmentWorkspaceStore(directory);assert(!state.Read().Preferences.ReceivePrereleaseUpdates,"Prerelease updates default off");
            state.Update(workspace=>workspace.Preferences.ReceivePrereleaseUpdates=true);assert(state.Read().Preferences.ReceivePrereleaseUpdates,"Prerelease preference persists");
            var updates=new UpdateStateStore(directory);updates.Record(latest! with {Manifest=manifest},DateTime.UtcNow,true);
            assert(updates.Read() is {ReceivePrerelease:true,Latest.Manifest.VersionNumber:"3.2.0",Latest.Manifest.Installers.Count:1},"Cached result retains release type, installer metadata and prerelease preference");
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
