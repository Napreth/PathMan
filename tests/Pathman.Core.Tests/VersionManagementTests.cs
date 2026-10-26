using Microsoft.Win32;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;
using Pathman.Core.Versions;

internal static class VersionManagementTests
{
    public static void Run(Action<bool,string> assert)
    {
        var directory=Path.Combine(Path.GetTempPath(),"PathMan.Core.Tests",Guid.NewGuid().ToString("N"));
        var registry=@"Software\PathMan.Tests\"+Guid.NewGuid().ToString("N");
        void Reject(Action action,string message)
        {try {action();}catch(Exception error) when(error is ArgumentException or IOException) {return;}throw new InvalidOperationException(message);}
        try
        {
            var store=new EnvironmentWorkspaceStore(directory);
            store.Update(state=>
            {
                VersionCatalog.Add(state,"java","8",@"D:\Java\jdk-8", "JAVA_HOME",VariableScope.Machine);
                VersionCatalog.Add(state,"java","25","D:/Java/jdk-25/");
                VersionCatalog.Add(state,"java","lts",@"d:\java\jdk-25");
                VersionCatalog.Add(state,"python","3.14",@"D:\Python\314","PYTHON_HOME",VariableScope.User);
            });
            var state=store.Read();
            assert(state.VersionProjects.Count==2 && VersionCatalog.Find(state,"JAVA").Versions["LTS"]==@"d:\java\jdk-25","Version metadata must persist with case-insensitive project and tag lookup");
            EnvironmentVariableInfo V(string name,string value,VariableScope scope=VariableScope.Machine)=>new(name,value,scope);
            EnvironmentVariableInfo[] variables=[V("JAVA_HOME",@"D:\Java\jdk-25\"),V("Path","%TOOLS_PATH%"),V("TOOLS_PATH","%JAVA_HOME%\\bin"),V("PYTHON_HOME",@"D:\Python\314",VariableScope.User)];
            var java=VersionCatalog.Describe(state,variables).Single(item=>item.Name=="java");
            assert(java.RefsByPATH && java.CurrentVersions.SequenceEqual(new[] {"25","lts"}),"Current aliases must come from the real registry value and PATH references must traverse intermediate variables");
            assert(!VersionCatalog.Describe(state,variables).Single(item=>item.Name=="python").RefsByPATH,"A project outside the PATH reference graph must report false");
            var shadowed=VersionCatalog.Describe(state,[V("JAVA_HOME","machine"),V("JAVA_HOME","user",VariableScope.User),V("Path","%JAVA_HOME%",VariableScope.User)]).Single(item=>item.Name=="java");
            assert(!shadowed.RefsByPATH,"A user PATH reference to a user override must not count as a machine-variable reference");
            assert(VersionCatalog.Describe(state,[V("Path","%JAVA_HOME%")]).Single(item=>item.Name=="java").RefsByPATH,"An undefined target can still have an explicit PATH reference before its first switch");
            assert(VersionCatalog.Describe(state,[V("JAVA_HOME","outside")]).Single(item=>item.Name=="java").CurrentVersions.Count==0,"External current values must not be replaced by a remembered tag");
            Reject(()=>VersionCatalog.Add(state,"duplicate","1","x","java_home",VariableScope.Machine),"One scoped variable cannot have conflicting project bindings");
            VersionCatalog.Add(state,"user-java","1","user-value","JAVA_HOME",VariableScope.User);
            Reject(()=>VersionCatalog.Add(state,"java","bad:name","x"),"Invalid tag syntax must be rejected");
            Reject(()=>VersionCatalog.Add(state,"java","8","overwritten"),"Add must not silently overwrite a version");
            Reject(()=>VersionCatalog.Add(state,"java","26","x","WRONG_HOME"),"Add must not silently rebind an existing project");
            Reject(()=>VersionCatalog.Add(state,"unknown","1","x"),"The first registration needs an explicit environment-variable target");
            Reject(()=>VersionCatalog.Add(state,"java","zero","bad\0value"),"NUL values are not valid Windows environment values");
            using var key=Registry.CurrentUser.CreateSubKey(registry,true);
            key.SetValue("JAVA_HOME",@"D:\Java\jdk-25\",RegistryValueKind.String);
            key.SetValue("Path","%JAVA_HOME%\\bin",RegistryValueKind.ExpandString);
            key.SetValue("UNRELATED","retain");
            var mutation=VersionCatalog.PrepareSwitch(state,"JAVA","8",variables);
            WindowsEnvironmentVariableEditor.ApplyKey(key,mutation);
            assert((string)key.GetValue("JAVA_HOME")! == @"D:\Java\jdk-8" && key.GetValueKind("JAVA_HOME")==RegistryValueKind.String,"Switch must write the selected value into the bound registry variable");
            assert((string)key.GetValue("Path",null,RegistryValueOptions.DoNotExpandEnvironmentNames)! == "%JAVA_HOME%\\bin" && (string)key.GetValue("UNRELATED")! == "retain","Switch must preserve PATH and unrelated environment variables");
            Reject(()=>WindowsEnvironmentVariableEditor.ApplyKey(key,mutation),"A stale switch must not overwrite a newer external value");
            VersionCatalog.Add(state,"java","ref","%JDK_ROOT%",replace:false);
            var reference=VersionCatalog.PrepareSwitch(state,"java","ref",[V("JAVA_HOME",@"D:\Java\jdk-8")]);
            WindowsEnvironmentVariableEditor.ApplyKey(key,reference);
            assert(key.GetValueKind("JAVA_HOME")==RegistryValueKind.ExpandString,"Version values with references must use REG_EXPAND_SZ");
            var current=(string)key.GetValue("JAVA_HOME",null,RegistryValueOptions.DoNotExpandEnvironmentNames)!;
            VersionCatalog.Delete(state,"java","ref");VersionCatalog.Delete(state,"java");
            assert((string)key.GetValue("JAVA_HOME",null,RegistryValueOptions.DoNotExpandEnvironmentNames)! == current && !state.VersionProjects.Any(item=>item.Name=="java"),"Deleting the active tag or project must retain the environment variable and its current value");
            var create=VersionCatalog.PrepareSwitch(state,"python","3.14",[]);
            assert(create.Action=="create" && create.Scope==VariableScope.User,"First switch must create only the requested scoped variable");
            Reject(()=>VersionCatalog.PrepareSwitch(state,"python","missing",variables),"Unknown versions must never generate a registry write");
            VersionCatalog.Edit(state,"python","3.14","current","some literal value",@"D:\Python\314");
            assert(VersionCatalog.Find(state,"python").Versions["current"]=="some literal value","Versions support ordinary environment values as well as paths");
            Reject(()=>VersionCatalog.Edit(state,"python","current","new","x","stale"),"Editing metadata must detect stale configuration");
            VersionCatalog.Add(state,"python","current","new literal",replace:true);
            assert(VersionCatalog.Find(state,"python").Versions["current"]=="new literal","Explicit replacement must support updating an existing alias");
            VersionCatalog.CreateUnbound(state,"inline");VersionCatalog.Add(state,"inline","one","literal");
            assert(VersionCatalog.Describe(state,[]).Single(item=>item.Name=="inline").Current is null,"A named inline project can retain version metadata before choosing a target");
            Reject(()=>VersionCatalog.PrepareSwitch(state,"inline","one",[]),"An unbound project cannot perform a registry write");
            VersionCatalog.Bind(state,"inline","INLINE_HOME",VariableScope.User,"",VariableScope.User);
            VersionCatalog.Rename(state,"inline","renamed");
            assert(VersionCatalog.Find(state,"renamed").VariableName=="INLINE_HOME" && VersionCatalog.Find(state,"renamed").Versions["one"]=="literal","Inline project renaming and binding must preserve version metadata");
            Reject(()=>VersionCatalog.Bind(state,"renamed","OTHER",VariableScope.User,"stale",VariableScope.User),"A stale target binding must not overwrite another editor");
            VersionCatalog.CreateUnbound(state,"cli-binding");VersionCatalog.Add(state,"cli-binding","one","value","CLI_HOME",VariableScope.Machine);
            assert(VersionCatalog.Find(state,"cli-binding").Type==VariableScope.Machine,"CLI registration can finish an unbound GUI project");
            Reject(()=>VersionCatalog.Create(state,"path","pAtH",VariableScope.User),"PATH is always forbidden as a version target");
            Reject(()=>VersionCatalog.Create(state,"builtin","ComSpec",VariableScope.Machine),"Built-in targets require the independent advanced preference");
            state.Assignments[EnvironmentWorkspace.Key(VariableScope.Machine,"ComSpec")]="other";
            state.Assignments[EnvironmentWorkspace.Key(VariableScope.Machine,"CUSTOM_ENV")]="system";
            assert(VersionTargetPolicy.Restriction("ComSpec",false) is not null && VersionTargetPolicy.Restriction("CUSTOM_ENV",false) is null,"Target restrictions must use the built-in name list, regardless of user group assignment");
            state.AllowBuiltInVersionTargets=true;VersionCatalog.Add(state,"builtin","test","placeholder","ComSpec",VariableScope.Machine);
            assert(VersionCatalog.PrepareSwitch(state,"builtin","test",[]).Name=="ComSpec","The advanced preference permits built-in targets");
            Reject(()=>VersionCatalog.Create(state,"still-path","PATH",VariableScope.Machine),"The advanced preference must never unlock PATH");
            state.AllowBuiltInVersionTargets=false;
            Reject(()=>VersionCatalog.PrepareSwitch(state,"builtin","test",[]),"Turning off the preference also blocks preexisting built-in configurations");
            VersionCatalog.Delete(state,"builtin");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registry,false);
            if(Directory.Exists(directory)) Directory.Delete(directory,true);
        }
        Console.WriteLine("PASS: persisted version catalogs, scoped PATH references, shared tags, guarded registry switches, reference types and metadata-only deletion.");
    }
}
