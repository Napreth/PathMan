using System.Text.RegularExpressions;
using Pathman.Core.EnvironmentVariables;
using Pathman.Core.Models;

namespace Pathman.Core.Versions;

public sealed class VersionProject
{
    public string Name {get;set;}="";
    public string VariableName {get;set;}="";
    public VariableScope Type {get;set;}=VariableScope.User;
    public Dictionary<string,string> Versions {get;set;}=new(StringComparer.OrdinalIgnoreCase);
}

public sealed record VersionProjectStatus(VersionProject Project,string? Current,bool RefsByPATH,IReadOnlyList<string> CurrentVersions)
{
    public string Name=>Project.Name;
    public string VariableName=>Project.VariableName;
    public VariableScope Type=>Project.Type;
}

public static partial class VersionCatalog
{
    [GeneratedRegex(@"^[\p{L}\p{N}_.+\-]+$",RegexOptions.CultureInvariant)] private static partial Regex Identifier();
    public static void ValidateIdentifier(string name)
    {
        if(name.Length is 0 or >128 || !Identifier().IsMatch(name))
            throw new ArgumentException(L.Get("项目名和版本名须为 1–128 个字符，只能含字母、数字、点、加号、下划线或连字符。"));
    }
    private static void ValidateValue(string value)
    {if(value.Length>32767 || value.Contains('\0')) throw new ArgumentException(L.Get("版本值不能含空字符，且最长为 32767 个字符。"));}
    public static void Validate(IReadOnlyList<VersionProject> projects)
    {
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var project in projects)
        {
            ValidateIdentifier(project.Name);
            if(project.VariableName.Length>0) WindowsEnvironmentVariableEditor.ValidateName(project.VariableName);
            if(project.Type is not (VariableScope.User or VariableScope.Machine)) throw new ArgumentException(L.Get("版本项目仅支持 User 或 Machine。"));
            if(!names.Add(project.Name) || project.VariableName.Length>0 && !targets.Add(EnvironmentWorkspace.Key(project.Type,project.VariableName)))
                throw new IOException(L.Get("项目名不能重复，同一作用域的环境变量只能绑定一个版本项目。"));
            foreach(var version in project.Versions) {ValidateIdentifier(version.Key);ValidateValue(version.Value);}
        }
    }
    public static VersionProject Find(EnvironmentWorkspace state,string name)=>state.VersionProjects.FirstOrDefault(project=>project.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) ?? throw new IOException(L.Format("找不到版本项目：{0}",name));
    public static void Create(EnvironmentWorkspace state,string name,string variable,VariableScope type)
    {
        VersionTargetPolicy.EnsureAllowed(variable,state);
        var project=new VersionProject {Name=name,VariableName=variable,Type=type};
        Validate([..state.VersionProjects,project]);state.VersionProjects.Add(project);
    }
    public static void CreateUnbound(EnvironmentWorkspace state,string name)
    {
        var project=new VersionProject {Name=name};
        Validate([..state.VersionProjects,project]);state.VersionProjects.Add(project);
    }
    public static void Rename(EnvironmentWorkspace state,string name,string newName)
    {
        ValidateIdentifier(newName);var project=Find(state,name);
        if(state.VersionProjects.Any(item=>item!=project && item.Name.Equals(newName,StringComparison.OrdinalIgnoreCase))) throw new IOException(L.Get("项目名已经存在。"));
        project.Name=newName;
    }
    public static void Bind(EnvironmentWorkspace state,string name,string variable,VariableScope type,string expectedVariable,VariableScope expectedType)
    {
        var project=Find(state,name);
        if(project.VariableName!=expectedVariable || project.Type!=expectedType) throw new IOException(L.Get("目标绑定已被其他窗口修改，请重新选择。"));
        if(type is not (VariableScope.User or VariableScope.Machine)) throw new ArgumentException(L.Get("作用范围仅支持用户或系统。"));
        if(variable.Length>0) VersionTargetPolicy.EnsureAllowed(variable,state);
        if(variable.Length>0 && state.VersionProjects.Any(item=>item!=project && item.Type==type && item.VariableName.Equals(variable,StringComparison.OrdinalIgnoreCase))) throw new IOException(L.Get("此环境变量已绑定其他版本项目。"));
        project.VariableName=variable;project.Type=type;
    }
    public static void Add(EnvironmentWorkspace state,string name,string version,string value,string? variable=null,VariableScope? type=null,bool replace=false)
    {
        ValidateIdentifier(name);ValidateIdentifier(version);ValidateValue(value);
        var project=state.VersionProjects.FirstOrDefault(item=>item.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
        if(project is null)
        {
            if(variable is null) throw new ArgumentException(L.Get("首次登记项目需要 --variable <环境变量名>；--type 默认为 User。"));
            Create(state,name,variable,type ?? VariableScope.User);project=Find(state,name);
        }
        else
        {
            if(project.VariableName.Length==0 && variable is not null) Bind(state,name,variable,type ?? project.Type,"",project.Type);
            if(variable is not null && !project.VariableName.Equals(variable,StringComparison.OrdinalIgnoreCase) || type is not null && type!=project.Type)
                throw new IOException(L.Get("项目已绑定其他变量或作用域，请使用现有绑定。"));
        }
        if(project.VariableName.Length>0) VersionTargetPolicy.EnsureAllowed(project.VariableName,state);
        if(project.Versions.ContainsKey(version) && !replace) throw new IOException(L.Get("该版本已存在；使用 --replace 更新其配置。"));
        project.Versions[version]=value;
    }
    public static void Edit(EnvironmentWorkspace state,string name,string oldVersion,string version,string value,string expectedValue)
    {
        ValidateIdentifier(version);ValidateValue(value);
        var project=Find(state,name);
        if(!project.Versions.TryGetValue(oldVersion,out var previous) || previous!=expectedValue) throw new IOException(L.Get("版本配置已被其他窗口修改，请重新选择。"));
        if(!oldVersion.Equals(version,StringComparison.OrdinalIgnoreCase) && project.Versions.ContainsKey(version)) throw new IOException(L.Get("新版本名已经存在。"));
        var entries=project.Versions.Select(pair=>pair.Key.Equals(oldVersion,StringComparison.OrdinalIgnoreCase) ? new KeyValuePair<string,string>(version,value) : pair);
        project.Versions=new(entries,StringComparer.OrdinalIgnoreCase);
    }
    // Removing metadata never removes or changes the actual environment variable.
    public static void Delete(EnvironmentWorkspace state,string name,string? version=null)
    {
        var project=Find(state,name);
        if(version is null) state.VersionProjects.Remove(project);
        else if(!project.Versions.Remove(version)) throw new IOException(L.Format("找不到版本：{0}",version));
    }
    public static bool SameValue(string left,string right)
    {
        if(left==right) return true;
        return EnvironmentPathResolver.Normalize(left) is {} a && EnvironmentPathResolver.Normalize(right) is {} b && a.Equals(b,StringComparison.OrdinalIgnoreCase);
    }
    public static IReadOnlyList<VersionProjectStatus> Describe(EnvironmentWorkspace state,IReadOnlyList<EnvironmentVariableInfo> variables)
    {
        return state.VersionProjects.Select(project=>
        {
            var current=variables.FirstOrDefault(item=>item.Scope==project.Type && item.Name.Equals(project.VariableName,StringComparison.OrdinalIgnoreCase));
            if(project.VariableName.Length==0) return new VersionProjectStatus(project,null,false,[]);
            // An explicit PATH reference still exists when its target is undefined.
            var context=current is null ? variables.Append(new(project.VariableName,"",project.Type)).ToArray() : variables;
            var resolver=new EnvironmentPathResolver(context);
            var referenced=context.Where(item=>item.Name.Equals("Path",StringComparison.OrdinalIgnoreCase)).Any(path=>
                resolver.ReferencedVariables(path.RawValue,path.Scope).Any(item=>item.Scope==project.Type && item.Name.Equals(project.VariableName,StringComparison.OrdinalIgnoreCase)));
            return new VersionProjectStatus(project,current?.RawValue,referenced,current is null ? [] : project.Versions.Where(pair=>SameValue(pair.Value,current.RawValue)).Select(pair=>pair.Key).ToArray());
        }).ToArray();
    }
    public static EnvironmentMutation PrepareSwitch(EnvironmentWorkspace state,string name,string version,IReadOnlyList<EnvironmentVariableInfo> variables)
    {
        var project=Find(state,name);
        VersionTargetPolicy.EnsureAllowed(project.VariableName,state);
        if(!project.Versions.TryGetValue(version,out var value)) throw new IOException(L.Format("项目 {0} 没有版本 {1}。",project.Name,version));
        ValidateValue(value);
        var current=variables.FirstOrDefault(item=>item.Scope==project.Type && item.Name.Equals(project.VariableName,StringComparison.OrdinalIgnoreCase));
        return new(current is null ? "create" : "set",project.Type,project.VariableName,value,ExpectedValue:current?.RawValue,ExpectedKind:current?.Kind);
    }
}
