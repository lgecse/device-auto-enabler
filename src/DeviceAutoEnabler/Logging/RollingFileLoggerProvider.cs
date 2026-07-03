using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DeviceAutoEnabler.Logging;

/// <summary>
/// Options for the size-capped rolling file logger.
/// </summary>
public sealed class RollingFileLoggerOptions
{
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Maximum size of the active file before it rolls over.</summary>
    public long MaxFileBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Number of rolled files to retain (excluding the active file).</summary>
    public int RetainedFileCount { get; init; } = 5;

    public LogLevel MinLevel { get; init; } = LogLevel.Information;
}

/// <summary>
/// A minimal, dependency-free rolling file logger provider. Writes are serialized and the file
/// is rolled when it exceeds the configured size cap, retaining a bounded number of old files so
/// logs can never fill the disk. Only device metadata is logged (never secrets).
/// </summary>
[ProviderAlias("RollingFile")]
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly RollingFileLoggerOptions _options;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly object _writeLock = new();
    private bool _disposed;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var dir = Path.GetDirectoryName(_options.FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, this));

    internal bool IsEnabled(LogLevel level) => level >= _options.MinLevel && level != LogLevel.None;

    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        var sb = new StringBuilder(256);
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        sb.Append(" [").Append(LevelLabel(level)).Append("] ");
        sb.Append(category);
        if (eventId.Id != 0)
        {
            sb.Append('(').Append(eventId.Id).Append(')');
        }

        sb.Append(": ").Append(message);
        if (exception is not null)
        {
            sb.Append(Environment.NewLine).Append(exception);
        }

        sb.Append(Environment.NewLine);
        var line = sb.ToString();

        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                RollIfNeeded(Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(_options.FilePath, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the service; drop the line if the file is unwritable.
            }
        }
    }

    private void RollIfNeeded(int incomingBytes)
    {
        try
        {
            var info = new FileInfo(_options.FilePath);
            if (!info.Exists || info.Length + incomingBytes <= _options.MaxFileBytes)
            {
                return;
            }

            // Shift existing rolled files: .N -> .N+1, dropping anything beyond the retention count.
            for (var i = _options.RetainedFileCount; i >= 1; i--)
            {
                var src = i == 1 ? _options.FilePath : $"{_options.FilePath}.{i - 1}";
                var dst = $"{_options.FilePath}.{i}";
                if (!File.Exists(src))
                {
                    continue;
                }

                if (i == _options.RetainedFileCount && File.Exists(dst))
                {
                    File.Delete(dst);
                }

                if (File.Exists(dst))
                {
                    File.Delete(dst);
                }

                File.Move(src, dst);
            }

            if (_options.RetainedFileCount == 0 && File.Exists(_options.FilePath))
            {
                // No retention requested: just truncate.
                File.Delete(_options.FilePath);
            }
        }
        catch
        {
            // If rolling fails, fall through and keep appending; better than losing the service.
        }
    }

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "     ",
    };

    public void Dispose()
    {
        _disposed = true;
        _loggers.Clear();
    }
}
