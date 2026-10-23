using HDMICaptain.Linux.Media;

namespace HDMICaptain.Linux.Media.Tests;

public class LinuxLogTests
{
    [Fact]
    public void LogLivesUnderXdgState() =>
        Assert.EndsWith(Path.Combine("hdmicaptain", "logs", "capture.log"), LinuxLog.FilePath);
}
