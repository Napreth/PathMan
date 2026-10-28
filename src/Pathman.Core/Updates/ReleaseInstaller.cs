using System.Net.Http;
using System.Security.Cryptography;

namespace Pathman.Core.Updates;

public sealed record UpdatePackage(string Architecture,bool SelfContained)
{
    public string FileName(string tag)=>$"PathMan-{ReleaseIdentity.Normalize(tag)}-{Architecture.ToLowerInvariant()}{(SelfContained ? "-SelfContained" : "")}.exe";
    public ReleaseAsset? Select(AvailableRelease release)
    {
        if(release.Manifest is {} manifest)
        {
            var package=manifest.Installers.FirstOrDefault(item=>item.Architecture.Equals(Architecture,StringComparison.OrdinalIgnoreCase) && item.SelfContained==SelfContained);
            if(package is null)return null;
            var asset=release.Assets.FirstOrDefault(item=>item.Name.Equals(package.FileName,StringComparison.Ordinal));
            if(asset is null)return null;
            if(asset.Size!=package.Size)throw new InvalidDataException(L.Get("安装包信息无效。"));
            return asset with {Digest="sha256:"+package.Sha256};
        }
        return null;
    }
}

public sealed class ReleaseInstaller(HttpClient client,string dataDirectory)
{
    private const long MaximumSize=1024L*1024*1024;
    public static bool IsDownloadUri(Uri uri)=>uri.IsAbsoluteUri && uri.Scheme==Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length==0 &&
        uri.AbsolutePath.StartsWith("/"+GitHubReleaseClient.Repository+"/releases/download/",StringComparison.OrdinalIgnoreCase);
    public async Task<string> DownloadAsync(ReleaseAsset asset,CancellationToken cancellationToken=default)
    {
        if(!IsDownloadUri(asset.Download) || asset.Size is <=0 or >MaximumSize) throw new InvalidDataException(L.Get("安装包信息无效。"));
        var digest=asset.Digest;
        if(digest is null || !digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase) || digest.Length!=71 || !digest[7..].All(char.IsAsciiHexDigit))
            throw new InvalidDataException(L.Get("无法验证安装包，请前往发布页下载。"));
        var expected=Convert.FromHexString(digest[7..]);
        var directory=Path.Combine(Path.GetFullPath(dataDirectory),"updates",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var response=await client.GetAsync(asset.Download,HttpCompletionOption.ResponseHeadersRead,cancellationToken);
            response.EnsureSuccessStatusCode();
            if(response.RequestMessage?.RequestUri?.Scheme!=Uri.UriSchemeHttps || response.Content.Headers.ContentLength is {} size && size!=asset.Size)
                throw new InvalidDataException(L.Get("安装包信息无效。"));
            var temporary=Path.Combine(directory,"installer.download");
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var input=await response.Content.ReadAsStreamAsync(cancellationToken))
            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
            {
                var buffer=new byte[81920];long total=0;int read;
                while((read=await input.ReadAsync(buffer,cancellationToken))>0)
                {
                    total+=read;if(total>asset.Size) throw new InvalidDataException(L.Get("安装包校验失败，请重新下载。"));
                    hash.AppendData(buffer,0,read);await output.WriteAsync(buffer.AsMemory(0,read),cancellationToken);
                }
                if(total!=asset.Size || !CryptographicOperations.FixedTimeEquals(expected,hash.GetHashAndReset()))
                    throw new InvalidDataException(L.Get("安装包校验失败，请重新下载。"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result=Path.Combine(directory,"PathMan-Setup.exe");File.Move(temporary,result);return result;
        }
        catch
        {
            // Only files from this download are placed in this newly created directory.
            try {Directory.Delete(directory,true);}catch(IOException) {}catch(UnauthorizedAccessException) {}
            throw;
        }
    }
}
