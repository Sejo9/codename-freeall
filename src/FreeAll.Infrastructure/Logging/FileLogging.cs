using System.Globalization;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace FreeAll.Infrastructure.Logging;

/// <summary>
/// Local rolling file logs under the protected data folder. Nothing is sent off the machine (DEC-018).
/// Callers must pass paths through <see cref="Core.Diagnostics.PathRedactor"/> before logging them.
/// </summary>
public static class FileLogging
{
    public const int RetainedFileCount = 14;
    public const long FileSizeLimitBytes = 10 * 1024 * 1024;

    public static ILoggerFactory Create(string logsDirectory, LogLevel minimumLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        Directory.CreateDirectory(logsDirectory);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(minimumLevel))
            .WriteTo.File(
                path: Path.Combine(logsDirectory, "freeall-.log"),
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true)
            .CreateLogger();

        return new SerilogLoggerFactory(logger, dispose: true);
    }

    private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };
}
