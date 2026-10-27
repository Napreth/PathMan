using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Pathman.Core.Updates;

internal static class UpdateDeliveryTests
{
    private sealed class DownloadHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {Requests++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {RequestMessage=request,Content=new ByteArrayContent(bytes)});}
    }
    public static void Run(Action<bool,string> assert)
    {
        var directory=Path.Combine(Path.GetTempPath(),"PathMan.Update.Tests",Guid.NewGuid().ToString("N"));
        try
        {
            var bytes=System.Text.Encoding.UTF8.GetBytes("verified installer fixture");
            var digest="sha256:"+Convert.ToHexString(SHA256.HashData(bytes));
            var assets=(from arch in new[]{"x86","x64","arm64"} from self in new[]{false,true} let name=new UpdatePackage(arch,self).FileName("v2.0.0")
                select new ReleaseAsset(name,new Uri("https://github.com/Napreth/PathMan/releases/download/v2.0.0/"+name),bytes.Length,digest)).ToArray();
            var release=new AvailableRelease("v2.0.0","Release title","First line\n\nLast line",new("https://github.com/Napreth/PathMan/releases/tag/v2.0.0"),assets);
            foreach(var arch in new[]{"x86","x64","arm64"}) foreach(var self in new[]{false,true})
            {var package=new UpdatePackage(arch,self);assert(package.Select(release)?.Name==package.FileName(release.Tag),$"Select exact architecture and runtime package: {arch}/{self}");}
            assert(new UpdatePackage("x64",true).Select(release with {Assets=[assets[0]]}) is null,"Missing architecture/runtime package uses the release-page fallback");
            var store=new UpdateStateStore(directory);store.Record(release,DateTime.UtcNow);
            var saved=new UpdateStateStore(directory).Read();
            assert(saved.LastCheckedUtc is not null && saved.Latest is {Tag:"v2.0.0",Title:"Release title",Body:"First line\n\nLast line"} && saved.Latest.Assets.Count==6,"Release title, body, assets and result survive restarting the application");
            assert(store.ReserveNotification("v2.0.0") && !new UpdateStateStore(directory).ReserveNotification("2.0.0"),"A version is notified only once across application restarts");
            assert(store.ReserveNotification("v2.1.0"),"A different version can notify again");
            store.ReleaseNotification("v2.1.0");assert(store.ReserveNotification("v2.1.0"),"A failed Toast delivery can be retried");
            store.Record(null,DateTime.UtcNow);assert(new UpdateStateStore(directory).Read().Latest is null,"A fresh check replaces an old cached update result");
            var handler=new DownloadHandler(bytes);using var http=new HttpClient(handler);var downloader=new ReleaseInstaller(http,directory);
            var file=downloader.DownloadAsync(assets[0]).GetAwaiter().GetResult();
            assert(File.ReadAllBytes(file).SequenceEqual(bytes),"The verified download is returned only after matching SHA-256");
            var originalCount=Directory.GetFiles(directory,"*.exe",SearchOption.AllDirectories).Length;
            foreach(var asset in new[]{assets[0] with {Digest="sha256:"+new string('0',64)},assets[0] with {Digest=null},assets[0] with {Digest="sha256:broken"},assets[0] with {Size=bytes.Length+1},assets[0] with {Download=new("http://github.com/Napreth/PathMan/releases/download/v2.0.0/a.exe")}})
            {
                try {downloader.DownloadAsync(asset).GetAwaiter().GetResult();throw new InvalidOperationException("Invalid installer was accepted");}
                catch(InvalidDataException) {}
            }
            assert(Directory.GetFiles(directory,"*.exe",SearchOption.AllDirectories).Length==originalCount && !Directory.GetFiles(directory,"*.download",SearchOption.AllDirectories).Any(),"Bad digests, missing digests, truncated content and unsafe URLs leave no runnable installer");
            assert(handler.Requests==3,"Missing digests and unsafe URLs are rejected before downloading");
        }
        finally {if(Directory.Exists(directory)) Directory.Delete(directory,true);}
    }
}
