using System.IO;
using System.Net.Http;
using System.Text.Json;
using Pathman.Core.Updates;

namespace Pathman.Gui;

public sealed class ApplicationUpdateService : IDisposable
{
    private readonly HttpClient http=new() {Timeout=TimeSpan.FromSeconds(20)};
    private readonly HttpClient download;
    private readonly UpdateStateStore store;
    private readonly string dataDirectory;
    private readonly Func<AvailableRelease,bool> notify;
    private AvailableRelease? latest;
    private CancellationTokenSource? request;
    private bool automaticRequest;
    public AvailableRelease? Available=>latest is {} release && ReleaseIdentity.IsDifferent(release.Tag,AppInfo.Version) ? release : null;
    public AvailableRelease? Latest=>latest;
    public bool IsChecking=>request is not null;
    private enum CheckState {None,Checking,Complete,Failed}
    private CheckState state;
    public string Status=>state switch {CheckState.Checking=>L.Get("正在检查更新……"),CheckState.Complete=>Available is {} release ? L.Format("发现新版本 {0}。",release.Tag) : L.Get("当前已是最新版本。"),CheckState.Failed=>L.Get("无法检查更新，请稍后重试。"),_=>L.Get("待检测")};
    public bool HasResult=>state==CheckState.Complete;
    public DateTime LastChecked {get;private set;}
    public event EventHandler? Changed;
    public ApplicationUpdateService(string dataDirectory,Func<AvailableRelease,bool> notify)
    {
        this.dataDirectory=dataDirectory;this.notify=notify;store=new(dataDirectory);
        try {var saved=store.Read();latest=saved.Latest;LastChecked=saved.LastCheckedUtc ?? default;if(saved.LastCheckedUtc is not null) state=CheckState.Complete;}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException) {}
#if PATHMAN_UPDATE_TEST
        download=new(new UpdateTestDownloadHandler(dataDirectory)) {Timeout=TimeSpan.FromMinutes(10)};
#else
        download=new() {Timeout=TimeSpan.FromMinutes(10)};
#endif
    }
    public async Task CheckAsync(bool automatic=false)
    {
        if(request is not null) return;
        request=new();automaticRequest=automatic;state=CheckState.Checking;Changed?.Invoke(this,EventArgs.Empty);
        try
        {
            latest=await GetLatestAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            store.Record(latest,DateTime.UtcNow);
            LastChecked=DateTime.UtcNow;state=CheckState.Complete;
            if(automatic && Available is {} release && store.ReserveNotification(release.Tag))
            {
                var shown=false;
                try {shown=notify(release);}finally {if(!shown) store.ReleaseNotification(release.Tag);}
            }
        }
        catch(Exception error) when(error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {LastChecked=DateTime.UtcNow;state=CheckState.Failed;}
        finally {request.Dispose();request=null;Changed?.Invoke(this,EventArgs.Empty);}
    }
    private Task<AvailableRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
#if PATHMAN_UPDATE_TEST
        return Task.Run<AvailableRelease?>(()=>UpdateTestReleaseSource.GetLatest(dataDirectory),cancellationToken);
#else
        return new GitHubReleaseClient(http).GetLatestAsync(AppInfo.Version,cancellationToken);
#endif
    }
    public async Task<string?> DownloadAsync(AvailableRelease release,CancellationToken cancellationToken)
    {
        var asset=AppInfo.Package.Select(release);
        return asset is null ? null : await new ReleaseInstaller(download,dataDirectory).DownloadAsync(asset,cancellationToken);
    }
    public void CancelAutomaticCheck() {if(automaticRequest) request?.Cancel();}
    public void Dispose() {request?.Cancel();http.Dispose();download.Dispose();}
}
