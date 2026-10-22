using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HDMICaptain.App;

/// <summary>
/// User-visible text from <c>Strings/en-US/Resources.resw</c> through MRT Core (the same file x:Uid in XAML reads).
/// A missing resource or loader failure returns the key, so it never crashes the UI.
/// </summary>
internal static class Strings
{
    private static readonly Lazy<ResourceLoader?> Loader = new(() =>
    {
        try { return new ResourceLoader(); }
        catch (Exception) { return null; }
    });

    public static string Get(string key)
    {
        try
        {
            var value = Loader.Value?.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }

    /// <summary>The resource as a composite format string ({0}, {1:0.##}) filled in the current culture.</summary>
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
