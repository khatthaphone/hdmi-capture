using System.Text.Json;

namespace HDMICaptain.Core.Tests;

/// <summary>Reads the shared JSON vectors in docs/fixtures/ (also read by the Swift and Kotlin suites).</summary>
public static class Fixtures
{
    public static readonly string Directory = Find();

    public static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, name)));

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "fixtures");
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("docs/fixtures not found above " + AppContext.BaseDirectory);
    }
}
