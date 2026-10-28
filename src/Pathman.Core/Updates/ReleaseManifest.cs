using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pathman.Core.Updates;

public readonly record struct BuildVersion(int X,int Y,int Z) : IComparable<BuildVersion>
{
    public static bool TryParse(string? value,out BuildVersion version)
    {
        version=default;var parts=value?.Split('.');
        if(parts is not {Length:3}) return false;
        var numbers=new int[3];
        for(var index=0;index<3;index++)
            if(parts[index].Length==0 || parts[index].Length>1 && parts[index][0]=='0' ||
               !parts[index].All(char.IsAsciiDigit) || !int.TryParse(parts[index],NumberStyles.None,CultureInfo.InvariantCulture,out numbers[index]) || numbers[index]>65535) return false;
        version=new(numbers[0],numbers[1],numbers[2]);return true;
    }
    public int CompareTo(BuildVersion other) {var x=X.CompareTo(other.X);if(x!=0)return x;var y=Y.CompareTo(other.Y);return y!=0?y:Z.CompareTo(other.Z);}
    public override string ToString()=>$"{X}.{Y}.{Z}";
}

public sealed record ReleasePackage(string FileName,string Architecture,bool SelfContained,long Size,string Sha256);

public sealed record ReleaseManifest(string VersionName,string VersionNumber,string Type)
{
    public IReadOnlyList<ReleasePackage> Installers {get;init;}=[];
    [JsonIgnore] public BuildVersion Number=>BuildVersion.TryParse(VersionNumber,out var value) ? value : throw new InvalidDataException("Invalid build version.");
    [JsonIgnore] public bool Prerelease=>Number.Z==0 && (Type is "beta" or "development");
    public static ReleaseManifest Parse(string json)
    {
        var value=JsonSerializer.Deserialize<ReleaseManifest>(json,new JsonSerializerOptions {PropertyNameCaseInsensitive=true}) ?? throw new InvalidDataException("Missing release manifest.");
        if(string.IsNullOrWhiteSpace(value.VersionName) || value.VersionName.Length>200 || value.VersionName.IndexOfAny(Path.GetInvalidFileNameChars())>=0 ||
           !BuildVersion.TryParse(value.VersionNumber,out var number) ||
           ((number.Y>0 || number.Z>0) ? value.Type!="development" : value.Type is not ("official" or "beta")) || value.Installers is null)
            throw new InvalidDataException("Invalid release manifest.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets=new HashSet<(string,bool)>();
        foreach(var package in value.Installers)
            if(package is null || string.IsNullOrWhiteSpace(package.FileName) || package.FileName.Length>255 ||
               package.FileName.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || !package.FileName.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) ||
               package.Architecture is not ("x86" or "x64" or "arm64") || package.Size<=0 || package.Size>1024L*1024*1024 ||
               package.Sha256 is not {Length:64} || !package.Sha256.All(char.IsAsciiHexDigit) ||
               !names.Add(package.FileName) || !targets.Add((package.Architecture,package.SelfContained)))
                throw new InvalidDataException("Invalid release installer.");
        return value;
    }
}
