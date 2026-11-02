using System.Security.Cryptography;
using System.Text;

namespace FreeAll.Infrastructure.Persistence.Migrations;

/// <summary>A forward-only schema migration (docs/11-data-model.md §4).</summary>
public sealed record Migration
{
    public Migration(int version, string name, string sql)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        Version = version;
        Name = name;
        Sql = sql;
        Checksum = ComputeChecksum(sql);
    }

    public int Version { get; }

    public string Name { get; }

    public string Sql { get; }

    /// <summary>SHA-256 of the script with line endings normalized, so a Git checkout cannot change it.</summary>
    public string Checksum { get; }

    private static string ComputeChecksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n").Trim())));
}

/// <summary>The database records a migration this version of FreeAll does not know about.</summary>
public sealed class SchemaTooNewException : Exception
{
    public SchemaTooNewException()
    {
    }

    public SchemaTooNewException(string message)
        : base(message)
    {
    }

    public SchemaTooNewException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The recorded migration history does not match the migrations shipped with this build.</summary>
public sealed class MigrationIntegrityException : Exception
{
    public MigrationIntegrityException()
    {
    }

    public MigrationIntegrityException(string message)
        : base(message)
    {
    }

    public MigrationIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
