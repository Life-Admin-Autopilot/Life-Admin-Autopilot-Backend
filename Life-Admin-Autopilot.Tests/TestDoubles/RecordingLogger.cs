using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.Tests.TestDoubles
{
    // "Failed delivery is logged rather than silently dropped" is an acceptance criterion,
    // so the log output is asserted on like any other observable behaviour.
    public class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IEnumerable<LogEntry> Warnings => Entries.Where(entry => entry.Level == LogLevel.Warning);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    public record LogEntry(LogLevel Level, string Message);
}
