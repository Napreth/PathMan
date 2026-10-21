using Microsoft.Win32;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

static void Assert(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
static void Reject(Action action,string message) { try { action(); } catch(IOException) { return; } throw new InvalidOperationException(message); }
var cases=new (string Name,string Raw,bool List,string[] Entries)[]
{
    ("Path",@"%SystemRoot%\System32; C:\工具 ;C:\工具;;",true,[@"%SystemRoot%\System32",@" C:\工具 ",@"C:\工具","",""]),
    ("pAtHeXt",".COM;.EXE;.BAT",true,[".COM",".EXE",".BAT"]), ("PATHEXT",".EXE",true,[".EXE"]),
    ("PATH","",true,[""]), ("CUSTOM_LIST",";alpha;;alpha;",true,["","alpha","","alpha",""]),
    ("TEMP",@"C:\my temp",false,[@"C:\my temp"]), ("EMPTY","",false,[""]), ("SPACES","  ",false,["  "])
};
foreach(var (name,raw,list,entries) in cases)
{
    var variable=new EnvironmentVariableInfo(name,raw,VariableScope.User);
    Assert(variable.RawValue==raw && variable.IsList==list && variable.Entries.SequenceEqual(entries),$"Split preservation failed: {name}");
    Assert((list ? string.Join(';',variable.Entries) : variable.Entries.Single())==raw,$"Roundtrip failed: {name}");
}
var fixtureRoot=Path.Combine(Path.GetTempPath(),"PathMan.Core.Tests",Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
var registryFixture=@"Software\PathMan.Tests\"+Guid.NewGuid().ToString("N");
try
{
    var store=new EnvironmentWorkspaceStore(fixtureRoot);
    var state=store.Read();
    EnvironmentVariableInfo V(string name,string raw,VariableScope scope=VariableScope.User)=>new(name,raw,scope);
    var runtime=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { ["SystemRoot"]=@"C:\Windows" };
    string Category(EnvironmentVariableInfo variable,params EnvironmentVariableInfo[] context)=>EnvironmentVariableClassifier.Classify(variable,[variable,..context],state,runtime);
    Assert(Category(V("FOOPATH",@"C:\one;D:\two"))=="paths","Names ending PATH without underscore must qualify");
    Assert(Category(V("DEVELOP_PATH",@"C:\one"))=="other","An unreferenced single path must remain other");
    Assert(Category(V("DEVELOP_PATH",@"C:\one"),V("Path",@"%develop_path%;C:\two"))=="paths","Bare PATH reference must be case-insensitive");
    Assert(Category(V("DEVELOP_PATH",@"C:\one"),V("Path",@"%DEVELOP_PATH%\bin;C:\two"))=="other","Embedded reference must not count as bare");
    Assert(Category(V("DEVELOP_PATH",@"C:\one"),V("Path","\"%DEVELOP_PATH%\";C:\\two"))=="other","Quoted reference must not count as bare");
    Assert(Category(V("DEVELOP_PATH",@"C:\one;"))=="other","Trailing empty item must not invent a second path");
    Assert(Category(V("DEVELOP_PATH",@"C:\one;not-a-path"))=="other","All nonempty items must be paths");
    Assert(Category(V("CLASSPATH",@"C:\a.jar;D:\b.jar"))=="paths","Absolute file paths are valid paths");
    Assert(Category(V("PSModulePath",@"C:\modules;D:\modules"))=="paths","PSModulePath follows actual path content");
    Assert(Category(V("PATHEXT",".COM;.EXE"))=="other","PATHEXT is a list, not a path variable");
    Assert(Category(V("JAVA_HOME",@"C:\Java"))=="other","Tool grouping was removed");
    Assert(Category(V("SystemRoot",@"C:\Windows"))=="system","Windows-owned rarely edited names belong to system");
    Assert(Category(V("TEMP",@"C:\Temp"))=="other","Frequently edited TEMP should not be hidden among system built-ins");
    Assert(Category(V("PROCESSOR_CUSTOM","mine"))=="other","Prefix alone must not make a custom variable Windows-owned");
    Assert(Category(V("Path",@"%SystemRoot%\system32;\\server\share"))=="paths","Expanded Windows paths and UNC paths must qualify");
    Assert(Category(V("SDK_PATH",@"C:\sdk",VariableScope.Machine),V("Path","%SDK_PATH%",VariableScope.User))=="other","Machine variable must not use a user-only PATH reference");
    store.Update(settings=> { for(var i=0;i<7;i++) settings.Groups.Add(new() {Id=$"custom-{i}",Name=$"分组 {i}",Scope=VariableScope.User}); settings.Assignments[EnvironmentWorkspace.Key(VariableScope.User,"JAVA_HOME")]="custom-6"; });
    state=store.Read();
    Assert(state.Groups.Count==11 && state.VisibleGroups(VariableScope.User).Count()==10,"Group count must come from data, with external hidden when disabled");
    Assert(Category(V("JAVA_HOME",@"C:\Java"))=="custom-6","Manual assignments to arbitrary groups must persist");
    store.Update(settings=>settings.Groups.First(group=>group.Id=="custom-6").Name="我的工具");
    Assert(store.Read().Groups.Last().Name=="我的工具","Group renames must survive reload");
    state.MonitoringEnabled=true;
    state.Snapshot=[new(VariableScope.User,"OLD","before",(int)RegistryValueKind.String),new(VariableScope.Machine,"REMOVED","gone",(int)RegistryValueKind.String)];
    Assert(EnvironmentMonitor.Compare(state,[V("OLD","after"),V("NEW","added")]),"Monitor must detect changes");
    Assert(state.Changes.Count==3 && state.ExternalVariables.Contains("User:NEW"),"Monitor must record additions, edits and removals");
    Assert(Category(V("NEW","added"))=="external","External changes must use the configured external group");
    Assert(!EnvironmentMonitor.Compare(state,[V("OLD","after"),V("NEW","added")]),"Unchanged snapshot must not emit repeated events");
    state.MonitoringEnabled=false;
    Assert(Category(V("NEW","added"))=="other","External group must disappear while monitoring is disabled");

    using var key=Registry.CurrentUser.CreateSubKey(registryFixture,true);
    const string rawPath=@"%SystemRoot%\System32;;C:\工具;";
    key.SetValue("Path",rawPath,RegistryValueKind.ExpandString); key.SetValue("PATHEXT",".COM;.EXE;.BAT"); key.SetValue("TEMP",@"C:\fixture"); key.SetValue("Counter",42,RegistryValueKind.DWord); key.SetValue("","metadata");
    var variables=WindowsEnvironmentVariableReader.ReadKey(key,VariableScope.Machine);
    Assert(variables.Count==3 && variables.All(variable=>variable.Scope==VariableScope.Machine),"Reader must keep scopes and omit nonstring metadata");
    var path=variables.Single(variable=>variable.Name=="Path");
    Assert(path.RawValue==rawPath && path.Kind==RegistryValueKind.ExpandString,"Read must preserve references and registry type");
    WindowsEnvironmentVariableEditor.ApplyKey(key,new("create",VariableScope.User,"EMPTY",""));
    Assert(key.GetValueNames().Contains("EMPTY") && (string)key.GetValue("EMPTY")! == "","Creating an empty value must not delete it");
    Reject(()=>WindowsEnvironmentVariableEditor.ApplyKey(key,new("create",VariableScope.User,"empty","collision")),"Names must be unique ignoring case");
    WindowsEnvironmentVariableEditor.ApplyKey(key,new("set",VariableScope.User,"Path",@"%SystemRoot%\x;;",ExpectedValue:rawPath,ExpectedKind:RegistryValueKind.ExpandString));
    Assert((string)key.GetValue("Path",null,RegistryValueOptions.DoNotExpandEnvironmentNames)! == @"%SystemRoot%\x;;","Save must preserve empty items and references");
    Reject(()=>WindowsEnvironmentVariableEditor.ApplyKey(key,new("set",VariableScope.User,"Path","stale",ExpectedValue:rawPath)),"Stale edit must be rejected");
    WindowsEnvironmentVariableEditor.ApplyKey(key,new("rename",VariableScope.User,"Path",NewName:"Renamed_PATH",ExpectedValue:@"%SystemRoot%\x;;",ExpectedKind:RegistryValueKind.ExpandString));
    Assert(key.GetValue("Path")==null && key.GetValueKind("Renamed_PATH")==RegistryValueKind.ExpandString,"Rename must preserve value type");
    Reject(()=>WindowsEnvironmentVariableEditor.ApplyKey(key,new("rename",VariableScope.User,"Renamed_PATH",NewName:"TEMP",ExpectedValue:@"%SystemRoot%\x;;")),"Rename must not overwrite existing values");
    WindowsEnvironmentVariableEditor.ApplyKey(key,new("rename",VariableScope.User,"Renamed_PATH",NewName:"RENAMED_PATH",ExpectedValue:@"%SystemRoot%\x;;"));
    Assert(key.GetValueKind("RENAMED_PATH")==RegistryValueKind.ExpandString,"Case-only rename must be a lossless no-op for case-insensitive names");
    WindowsEnvironmentVariableEditor.ApplyKey(key,new("delete",VariableScope.User,"RENAMED_PATH",ExpectedValue:@"%SystemRoot%\x;;"));
    Assert(!key.GetValueNames().Any(name=>name.Equals("RENAMED_PATH",StringComparison.OrdinalIgnoreCase)),"Delete must remove only the chosen variable");
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(registryFixture,false);
    var allowed=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"PathMan.Core.Tests"))+Path.DirectorySeparatorChar;
    var target=Path.GetFullPath(fixtureRoot);
    if(!target.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup path");
    Directory.Delete(target,true);
}
Console.WriteLine("PASS: lossless semicolon values; content/reference-based classification; configurable groups; external change tracking; registry create/edit/rename/delete, conflict and stale-write protection. No real Environment keys were modified.");
