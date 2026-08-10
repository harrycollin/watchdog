using KioskWatchdog.Core.Logging;

namespace KioskWatchdog.Core.Tests.Logging;

public class LogHistoryReaderTests
{
    [Fact]
    public void ReadTail_returns_last_lines_from_shared_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kw-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, $"watchdog-{DateTime.Now:yyyyMMdd}.log");
            File.WriteAllText(path, "line1\nline2\nline3\nline4\n");

            var tail = LogHistoryReader.ReadTail(path, maxLines: 2, directory: dir);
            Assert.Contains("line3", tail);
            Assert.Contains("line4", tail);
            Assert.DoesNotContain("line1", tail);

            var files = LogHistoryReader.ListFiles(dir);
            Assert.Single(files);
            Assert.Equal(path, files[0].Path);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
