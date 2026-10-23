namespace HDMICaptain.Linux.Media;

/// <summary>
/// Capture diagnostics: stderr plus, in Debug builds, $XDG_STATE_HOME/hdmicaptain/logs/capture.log
/// (same line format as Windows' AppLog). Safe from any thread.
/// </summary>
public static class LinuxLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(StateHome(), "hdmicaptain", "logs", "capture.log");

    public static void Write(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        Console.Error.WriteLine(stamped);
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

    public static string StateHome() =>
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
}
