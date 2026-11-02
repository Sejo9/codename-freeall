using System.Globalization;

namespace FreeAll.Infrastructure.Persistence.Migrations;

/// <summary>
/// Loads migration scripts embedded from <c>Persistence/Migrations/Scripts/NNNN_name.sql</c>.
/// Applied scripts must never be edited; add a new script instead.
/// </summary>
public static class EmbeddedMigrations
{
    private const string ResourcePrefix = "FreeAll.Migrations.";

    public static IReadOnlyList<Migration> Load()
    {
        var assembly = typeof(EmbeddedMigrations).Assembly;
        var migrations = new List<Migration>();

        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var fileName = Path.GetFileNameWithoutExtension(resource[ResourcePrefix.Length..]);
            var separator = fileName.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0 || !int.TryParse(fileName.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException($"Migration resource '{resource}' must be named NNNN_name.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration resource '{resource}' could not be read.");
            using var reader = new StreamReader(stream);

            migrations.Add(new Migration(version, fileName[(separator + 1)..], reader.ReadToEnd()));
        }

        return migrations.OrderBy(m => m.Version).ToList();
    }
}
