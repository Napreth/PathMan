using System.Net;
using System.Net.Http;
using Pathman.Core.Localization;
using Pathman.Core.Updates;
using Pathman.Core.EnvironmentVariables;

internal static class UpdateAndLocalizationTests
{
    private sealed class ResponseHandler(string body,HttpStatusCode status=HttpStatusCode.OK,string? manifest=null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {Request??=request;return Task.FromResult(new HttpResponseMessage(status) {Content=new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/release.json") ? manifest ?? "{}" : body)});}
    }
    public static void Run(Action<bool,string> assert)
    {
        assert(Text.ResolveLanguage("system","zh-CN")=="zh-CN" && Text.ResolveLanguage("system","fr-FR")=="en-US" && Text.ResolveLanguage("system","zh-TW")=="en-US","Language follows supported system language or falls back to English");
        Text.SetLanguage("en-US");assert(Text.Get("用户变量")=="User variables" && Text.Get("其他变量")=="Other variables","Neutral English resources are available");
        assert(Text.Format("第 {0} 行",2)=="Line 2" && Text.Get("CUSTOM_GROUP")=="CUSTOM_GROUP","Localized formatting preserves custom group names");
        Text.SetLanguage("zh-CN");assert(Text.Get("用户变量")=="用户变量" && Text.Get("此变量属于内置系统变量。可在“设置 → 高级设置”中解锁，PATH 始终不可选。").Contains("设置 →"),"Chinese satellite resources and advanced settings location");
        Task.Run(()=>Text.SetLanguage("en-US")).GetAwaiter().GetResult();
        assert(Text.Get("用户变量")=="User variables","Language selection survives an async execution-context boundary");
        Text.SetLanguage("zh-CN");
        var issueVariables=new[]{new EnvironmentVariableInfo("CHECK_VALUE","%MISSING_ONE% %MISSING_TWO%",Pathman.Core.Models.VariableScope.User,Microsoft.Win32.RegistryValueKind.ExpandString),new EnvironmentVariableInfo("CHECK_PATH",@"C:\one;;C:\one",Pathman.Core.Models.VariableScope.User,Microsoft.Win32.RegistryValueKind.String)};
        var chineseIssues=EnvironmentHealth.Analyze(issueVariables).Issues;
        Text.SetLanguage("en-US");var englishIssues=EnvironmentHealth.Analyze(issueVariables).Issues;
        assert(chineseIssues.Select(issue=>issue.Id).Order().SequenceEqual(englishIssues.Select(issue=>issue.Id).Order()),"Ignored health finding identities do not change with display language");
        assert(englishIssues.Count(issue=>issue.Kind==EnvironmentIssueKind.UndefinedReference)==2,"Different undefined references in one scalar retain separate identities");
        var oversized=new[]{new EnvironmentVariableInfo("LONG_VALUE",new string('x',262145),Pathman.Core.Models.VariableScope.User,Microsoft.Win32.RegistryValueKind.String)};
        assert(EnvironmentHealth.Analyze(oversized).Issues.Any(issue=>issue.Kind==EnvironmentIssueKind.ExpansionLimit),"Expansion-limit detection is independent of translated error wording");
        Text.SetLanguage("zh-CN");
        var handler=new ResponseHandler("""[{"draft":false,"prerelease":false,"tag_name":"v0.9.0","name":"PathMan 0.9","body":"Release notes","assets":[{"name":"release.json","browser_download_url":"https://github.com/Napreth/PathMan/releases/download/v0.9.0/release.json","size":100}]}]""",manifest:"""{"versionName":"0.9.0","versionNumber":"1.0.0","type":"official"}""");
        using var client=new HttpClient(handler);
        var update=new GitHubReleaseClient(client).GetLatestAsync().GetAwaiter().GetResult();
        assert(update is {Tag:"v0.9.0",Title:"PathMan 0.9",Body:"Release notes"} && update.Page.AbsoluteUri=="https://github.com/Napreth/PathMan/releases/tag/v0.9.0","Latest release metadata preserves the title and body");
        assert(handler.Request?.RequestUri?.AbsolutePath.EndsWith("/releases")==true && handler.Request.Headers.UserAgent.Any(),"Update checks enumerate releases with a user agent");
        assert(update is not null && !ReleaseIdentity.IsUpdate(update,"2.0.0",false),"Older numeric builds cannot be offered despite a different product name");
        foreach(var flags in new[]{"\"draft\":true,\"prerelease\":false","\"draft\":false,\"prerelease\":true"})
        {using var excluded=new HttpClient(new ResponseHandler("[{"+flags+",\"tag_name\":\"v2\"}]"));assert(new GitHubReleaseClient(excluded).GetLatestAsync().GetAwaiter().GetResult() is null,"Draft and prerelease releases are excluded");}
        using var failed=new HttpClient(new ResponseHandler("{}",HttpStatusCode.Forbidden));
        try {new GitHubReleaseClient(failed).GetLatestAsync().GetAwaiter().GetResult();throw new InvalidOperationException("Expected API failure");}catch(HttpRequestException) { }
        var directory=Path.Combine(Path.GetTempPath(),"PathMan.Core.Tests",Guid.NewGuid().ToString("N"));
        try {
            var store=new EnvironmentWorkspaceStore(directory);store.Update(state=> {state.Preferences.Language="en-US";state.Preferences.Theme="dark";state.Preferences.AutoCheckUpdates=false;state.AllowBuiltInVersionTargets=true;});
            var saved=store.Read();assert(saved.Preferences is {Language:"en-US",Theme:"dark",AutoCheckUpdates:false} && saved.AllowBuiltInVersionTargets,"Appearance and update preferences persist without changing advanced settings");
        }finally{if(Directory.Exists(directory)) Directory.Delete(directory,true);}
        Text.SetLanguage("zh-CN");
    }
}
