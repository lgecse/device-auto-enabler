using Microsoft.Extensions.Logging;

namespace DeviceAutoEnabler.Logging;

/// <summary>
/// Per-category logger that forwards formatted messages to the owning provider.
/// </summary>
internal sealed class RollingFileLogger : ILogger
{
    private readonly string _category;
    private readonly RollingFileLoggerProvider _provider;

    public RollingFileLogger(string category, RollingFileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);
        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
        {
            return;
        }

        _provider.Write(_category, logLevel, eventId, message, exception);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        private NullScope()
        {
        }

        public void Dispose()
        {
        }
    }
}
