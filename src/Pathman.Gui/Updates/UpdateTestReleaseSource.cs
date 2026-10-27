#if PATHMAN_UPDATE_TEST
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Pathman.Core.Updates;

namespace Pathman.Gui;

internal static class UpdateTestReleaseSource
{
    public static string Fixture(string dataDirectory)=>Path.Combine(dataDirectory,"update-test-installer.exe");
    public static AvailableRelease GetLatest(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var counter=Path.Combine(dataDirectory,"update-test-sequence.txt");
        var number=File.Exists(counter) && long.TryParse(File.ReadAllText(counter),out var value) ? value+1 : 1;
        File.WriteAllText(counter,number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var tag=$"v99.0.{number}";
        var fixture=Fixture(dataDirectory);var bundled=Path.Combine(AppContext.BaseDirectory,"UpdateTestInstaller.exe");
        if(File.Exists(bundled)) File.Copy(bundled,fixture,true);
        var assets=new List<ReleaseAsset>();
        if(File.Exists(fixture))
        {
            using var stream=File.OpenRead(fixture);var digest="sha256:"+Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var name=AppInfo.Package.FileName(tag);
            assets.Add(new(name,new Uri($"https://github.com/{GitHubReleaseClient.Repository}/releases/download/{tag}/{name}"),stream.Length,digest));
        }
        var body=$"更新功能测试 {tag}\n\n此版本由独立的更新测试程序生成。每次检测都会生成下一版本。\n\n"+
            string.Join("\n\n",Enumerable.Range(1,20).Select(index=>$"测试更新说明 {index}\n检查版本详情、滚动正文和安装更新。"));
        return new(tag,$"PathMan {tag} - 更新功能测试",body,new Uri(GitHubReleaseClient.RepositoryPage+"/releases"),assets);
    }
}

internal sealed class UpdateTestDownloadHandler(string dataDirectory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fixture=UpdateTestReleaseSource.Fixture(dataDirectory);
        var response=new HttpResponseMessage(File.Exists(fixture) ? HttpStatusCode.OK : HttpStatusCode.NotFound) {RequestMessage=request};
        if(response.IsSuccessStatusCode) response.Content=new StreamContent(File.OpenRead(fixture));
        return Task.FromResult(response);
    }
}
#endif
