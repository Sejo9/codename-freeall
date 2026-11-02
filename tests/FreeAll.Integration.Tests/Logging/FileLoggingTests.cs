using FreeAll.Core.Diagnostics;
using FreeAll.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace FreeAll.Integration.Tests.Logging;

public sealed class FileLoggingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Writes_entries_at_or_above_the_minimum_level_to_a_daily_file()
    {
        var logs = _temp.Combine("logs");

        using (var factory = FileLogging.Create(logs, LogLevel.Information))
        {
            var logger = factory.CreateLogger("Test");
#pragma warning disable CA1848, CA1873 // Plain logging calls are fine in a test.
            logger.LogDebug("hidden debug entry");
            logger.LogWarning("visible warning {Number}", 42);
#pragma warning restore CA1848, CA1873
        }

        var file = Assert.Single(Directory.GetFiles(logs, "freeall-*.log"));
        var content = File.ReadAllText(file);
        Assert.Contains("[WRN] Test: visible warning 42", content, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden debug entry", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_paths_do_not_leak_names_into_the_log()
    {
        var logs = _temp.Combine("logs");
        var redactor = new PathRedactor();

        var redacted = redactor.Redact(@"C:\Users\alice\Private\taxes.pdf");
        using (var factory = FileLogging.Create(logs, LogLevel.Information))
        {
#pragma warning disable CA1848, CA1873
            factory.CreateLogger("Test").LogInformation("Skipped {Path}", redacted);
#pragma warning restore CA1848, CA1873
        }

        var content = File.ReadAllText(Assert.Single(Directory.GetFiles(logs)));
        Assert.DoesNotContain("alice", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taxes", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\[path:", content, StringComparison.Ordinal);
    }
}
