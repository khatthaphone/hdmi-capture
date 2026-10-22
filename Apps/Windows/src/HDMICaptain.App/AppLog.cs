using System.Diagnostics;

namespace HDMICaptain.App;

/// <summary>
/// Capture diagnostics: the debugger output plus, in Debug builds, %LOCALAPPDATA%\HDMICaptain\logs\capture.log
/// (the HRESULT research in task 089 reads it). Safe from any thread.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDMICaptain", "logs", "capture.log");

    public static void Write(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        Debug.WriteLine(stamped);
#if DEBUG
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, stamped + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take capture down.
            }
        }
#endif
    }
}
