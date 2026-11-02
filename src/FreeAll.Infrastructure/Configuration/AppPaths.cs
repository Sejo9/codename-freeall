using FreeAll.Application.Abstractions;

namespace FreeAll.Infrastructure.Configuration;

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "freeall.db");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary><c>%LocalAppData%\FreeAll</c> for the signed-in user.</summary>
    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            "FreeAll"));
}
