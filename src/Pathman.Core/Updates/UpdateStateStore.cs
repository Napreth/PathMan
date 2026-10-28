using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pathman.Core.Updates;

public sealed class UpdateState
{
    public AvailableRelease? Latest {get;set;}
    public DateTime? LastCheckedUtc {get;set;}
    public bool ReceivePrerelease {get;set;}
    public HashSet<string> NotifiedVersions {get;set;}=[];
}

public sealed class UpdateStateStore
{
    private readonly string file;
    private readonly string mutexName;
    private static readonly JsonSerializerOptions options=new() {PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
    public UpdateStateStore(string dataDirectory)
    {
        file=Path.Combine(Path.GetFullPath(dataDirectory),"updates.json");
        mutexName=@"Local\PathMan.Updates."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToUpperInvariant())))[..24];
    }
    private T Locked<T>(Func<T> action)
    {
        using var mutex=new Mutex(false,mutexName);
        try {if(!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException(L.Get("更新记录正在使用中，请稍后重试。"));}catch(AbandonedMutexException) {}
        try {return action();}finally {mutex.ReleaseMutex();}
    }
    private UpdateState Load()
    {
        if(!File.Exists(file)) return new();
        try {var state=JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(file),options) ?? new();state.NotifiedVersions??=[];if(state.Latest?.Manifest is {} manifest)ReleaseManifest.Parse(JsonSerializer.Serialize(manifest,options));return state;}
        catch(Exception error) when(error is JsonException or InvalidDataException) {return new();}
    }
    private void Save(UpdateState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(temporary,JsonSerializer.Serialize(state,options),new UTF8Encoding(false));File.Move(temporary,file,true);}
        finally {if(File.Exists(temporary)) File.Delete(temporary);}
    }
    public UpdateState Read()=>Locked(Load);
    public void Record(AvailableRelease? latest,DateTime checkedUtc,bool receivePrerelease=false)=>Locked(()=> {var state=Load();state.Latest=latest;state.LastCheckedUtc=checkedUtc;state.ReceivePrerelease=receivePrerelease;Save(state);return true;});
    public bool ReserveNotification(string tag)=>Locked(()=>
    {
        var state=Load();var version=ReleaseIdentity.Normalize(tag);
        if(!state.NotifiedVersions.Add(version)) return false;
        Save(state);return true;
    });
    public void ReleaseNotification(string tag)=>Locked(()=> {var state=Load();if(state.NotifiedVersions.Remove(ReleaseIdentity.Normalize(tag))) Save(state);return true;});
}
