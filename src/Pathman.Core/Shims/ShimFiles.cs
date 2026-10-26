namespace Pathman.Core.Shims;

internal static class ShimFiles
{
    private const string RetiredPrefix = ".pathman-retired-";

    public static void Replace(string source, string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Cleanup(directory);
        var retired = Path.Combine(directory, RetiredPrefix + Guid.NewGuid().ToString("N") + ".tmp");
        var moved = false;
        if (File.Exists(destination)) { File.Move(destination, retired); moved = true; }
        try { File.Move(source, destination); }
        catch
        {
            if (moved) File.Move(retired, destination);
            throw;
        }
        // Windows permits moving a mapped executable, but deletion may need to
        // wait until its process exits. A later refresh reclaims the retired link.
        if (moved) TryDelete(retired);
    }

    private static void Cleanup(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, RetiredPrefix + "*.tmp"))
            if (Guid.TryParseExact(Path.GetFileName(file)[RetiredPrefix.Length..^4], "N", out _)) TryDelete(file);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}