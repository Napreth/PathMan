using Microsoft.Win32;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

static void Assert(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
static void Reject(Action action,string message) { try { action(); } catch(IOException) { return; } throw new InvalidOperationException(message); }
static void RejectMove(Action action,string message) { try { action(); } catch(ArgumentException) { return; } throw new InvalidOperationException(message); }
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
PathCleanupTests.Run(Assert);
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
    Assert(Category(V("SystemRoot",@"C:\Windows",VariableScope.Machine))=="system","Windows-owned machine names belong to system");
    Assert(Category(V("SystemRoot",@"C:\custom"))=="other" && state.VisibleGroups(VariableScope.User).All(group=>group.Rule!="builtin"),"User scope must not contain a built-in system category");
    Assert(Category(V("DEVELOP_PATH",@"C:\one"),V("Path",@"%DEVELOP_PATH%\;C:\two"))=="paths","A trailing separator must still count as a pure reference");
    Assert(Category(V("TEMP",@"C:\Temp"))=="other","Frequently edited TEMP should not be hidden among system built-ins");
    Assert(Category(V("PROCESSOR_CUSTOM","mine"))=="other","Prefix alone must not make a custom variable Windows-owned");
    Assert(Category(V("Path",@"%SystemRoot%\system32;\\server\share"))=="paths","Expanded Windows paths and UNC paths must qualify");
    Assert(Category(V("SDK_PATH",@"C:\sdk",VariableScope.Machine),V("Path","%SDK_PATH%",VariableScope.User))=="other","Machine variable must not use a user-only PATH reference");
    store.Update(settings=> { for(var i=0;i<7;i++) settings.Groups.Add(new() {Id=$"custom-{i}",Name=$"分组 {i}",Scope=VariableScope.User}); settings.Assignments[EnvironmentWorkspace.Key(VariableScope.User,"JAVA_HOME")]="custom-6"; });
    state=store.Read();
    Assert(state.Groups.Count==11 && state.VisibleGroups(VariableScope.User).Count()==9,"Group count must come from data, with external and user builtin groups hidden");
    Assert(Category(V("JAVA_HOME",@"C:\Java"))=="custom-6","Manual assignments to arbitrary groups must persist");
    store.Update(settings=>settings.Groups.First(group=>group.Id=="custom-6").Name="我的工具");
    Assert(store.Read().Groups.Last().Name=="我的工具","Group renames must survive reload");
    store.Update(settings=>
    {
        settings.PinGroup(VariableScope.User,"other",true);
        settings.PinGroup(VariableScope.User,"paths",true);
        settings.MoveGroup(VariableScope.User,"paths","other");
        settings.MoveGroup(VariableScope.User,"custom-6","custom-0");
        settings.PinVariable(VariableScope.User,"Z_PIN",true);
        settings.PinVariable(VariableScope.User,"A_PIN",true);
        settings.MoveVariable(VariableScope.User,"A_PIN","other","other","Z_PIN");
        settings.MoveVariable(VariableScope.User,"FOOPATH","paths","custom-6");
    });
    state=store.Read();
    Assert(state.VisibleGroups(VariableScope.User).Take(3).Select(group=>group.Id).SequenceEqual(["paths","other","custom-6"]),"Pinned and ordinary groups must retain separate drag orders after reload");
    Assert(state.VisibleGroups(VariableScope.Machine).First().Id=="paths" && !state.IsGroupPinned(VariableScope.Machine,"other"),"Group pinning/order must be independent between user and machine scopes");
    Assert(state.OrderVariables([V("B","b"),V("Z_PIN","z"),V("A_PIN","a"),V("A","a")]).Select(item=>item.Name).SequenceEqual(["A_PIN","Z_PIN","A","B"]),"Pinned variables must precede alphabetical ordinary variables and retain drag order");
    Assert(Category(V("FOOPATH",@"C:\one;D:\two"))=="custom-6","Dragging to a manual group must override automatic path classification");
    RejectMove(()=>state.MoveGroup(VariableScope.User,"custom-0","other"),"Ordinary groups must not enter the pinned group region");
    RejectMove(()=>state.MoveGroup(VariableScope.User,"paths",null),"Pinned groups must not leave the pinned region");
    RejectMove(()=>state.MoveVariable(VariableScope.User,"B","other","other"),"Ordinary variables cannot be reordered within a group");
    RejectMove(()=>state.MoveVariable(VariableScope.User,"B","other","custom-6","Z_PIN"),"Ordinary variables cannot acquire a pinned position by dragging");
    RejectMove(()=>state.MoveVariable(VariableScope.User,"B","other","missing"),"Dropping outside a real group must be rejected");
    state.PinVariable(VariableScope.User,"A_PIN",false);
    Assert(state.OrderVariables([V("A_PIN","a"),V("Z_PIN","z")]).First().Name=="Z_PIN","Unpinning must return a variable to ordinary order");
    state.MonitoringEnabled=true; state.PinGroup(VariableScope.User,"external",true); state.MonitoringEnabled=false;
    state.MoveGroup(VariableScope.User,"custom-0",null); state.MonitoringEnabled=true;
    Assert(state.IsGroupPinned(VariableScope.User,"external"),"Reordering visible groups must preserve hidden monitored-group pinning");
    state.MonitoringEnabled=true;
    state.Snapshot=[new(VariableScope.User,"OLD","before",(int)RegistryValueKind.String),new(VariableScope.Machine,"REMOVED","gone",(int)RegistryValueKind.String)];
    Assert(EnvironmentMonitor.Compare(state,[V("OLD","after"),V("NEW","added")]),"Monitor must detect changes");
    Assert(state.Changes.Count==3 && state.ExternalVariables.Contains("User:NEW"),"Monitor must record additions, edits and removals");
    Assert(Category(V("NEW","added"))=="external","External changes must use the configured external group");
    state.Assignments[EnvironmentWorkspace.Key(VariableScope.User,"NEW")]="custom-6";
    Assert(Category(V("NEW","added"))=="custom-6","User grouping must have higher priority than external-change classification");
    state.Assignments.Remove(EnvironmentWorkspace.Key(VariableScope.User,"NEW"));
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
    var keys=new Dictionary<VariableScope,RegistryKey> { [VariableScope.User]=key };
    var original=(string)key.GetValue("TEMP")!;
    Reject(()=>WindowsEnvironmentVariableEditor.ApplyBatchKeys(keys,[new("create",VariableScope.User,"BATCH_NEW","one"),new("create",VariableScope.User,"TEMP","conflict")],[]),"Batch conflicts must be detected before the first write");
    Assert(key.GetValue("BATCH_NEW")==null,"A rejected batch must not leave an orphan software variable");
    Reject(()=>WindowsEnvironmentVariableEditor.ApplyBatchKeys(keys,[new("create",VariableScope.User,"BATCH_NEW","one")],[new(VariableScope.User,"TEMP","stale",(int)RegistryValueKind.String)]),"Changed referenced values must invalidate a cleanup preview");
    var rollback=WindowsEnvironmentVariableEditor.ApplyBatchKeys(keys,[new("create",VariableScope.User,"BATCH_NEW","one"),new("set",VariableScope.User,"TEMP","two",ExpectedValue:original,ExpectedKind:RegistryValueKind.String)],[]);
    Assert((string)key.GetValue("BATCH_NEW")! == "one" && (string)key.GetValue("TEMP")! == "two","A validated batch must apply every mutation");
    rollback();
    Assert(key.GetValue("BATCH_NEW")==null && (string)key.GetValue("TEMP")! == original,"Batch rollback must restore all original values");
    using var readOnly=Registry.CurrentUser.OpenSubKey(registryFixture,false)!;
    var failed=false;
    try { WindowsEnvironmentVariableEditor.ApplyBatchKeys(new Dictionary<VariableScope,RegistryKey> { [VariableScope.User]=key,[VariableScope.Machine]=readOnly },[new("create",VariableScope.User,"BATCH_NEW","one"),new("set",VariableScope.Machine,"TEMP","unwritable",ExpectedValue:original)],[]); }
    catch(UnauthorizedAccessException) { failed=true; }
    Assert(failed && key.GetValue("BATCH_NEW")==null,"A failed registry write must roll back earlier batch writes");
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(registryFixture,false);
    var allowed=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"PathMan.Core.Tests"))+Path.DirectorySeparatorChar;
    var target=Path.GetFullPath(fixtureRoot);
    if(!target.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup path");
    Directory.Delete(target,true);
}
Console.WriteLine("PASS: lossless semicolon values; classification and manual precedence; configurable groups; persisted scope-specific pins and drag ordering; invalid-drop protection; external tracking; registry edits, conflicts and stale-write protection. No real Environment keys were modified.");
