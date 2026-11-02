namespace FreeAll.Application.Abstractions;

/// <summary>Per-user locations for FreeAll data (docs/05-system-architecture.md §7).</summary>
public interface IAppPaths
{
    /// <summary>Root data folder, normally <c>%LocalAppData%\FreeAll</c>.</summary>
    string DataDirectory { get; }

    string DatabasePath { get; }

    string LogsDirectory { get; }
}
