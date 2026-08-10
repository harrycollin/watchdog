using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Logging;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly object _writeLock = new();

    public RollingFileLoggerProvider(string directory, string filePrefix)
    {
        _directory = directory;
        _filePrefix = filePrefix;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, this));

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        var path = Path.Combine(_directory, $"{_filePrefix}-{DateTime.Now:yyyyMMdd}.log");

        lock (_writeLock)
        {
            File.AppendAllText(path, line + Environment.NewLine);

            foreach (var file in Directory.EnumerateFiles(_directory, $"{_filePrefix}-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-14))
                        File.Delete(file);
                }
                catch
                {
                    // ignore retention failures
                }
            }
        }
    }

    public void Dispose() => _loggers.Clear();
}

internal sealed class RollingFileLogger : ILogger
{
    private readonly string _category;
    private readonly RollingFileLoggerProvider _provider;

    public RollingFileLogger(string category, RollingFileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        _provider.Write(_category, logLevel, formatter(state, exception), exception);
    }
}
