namespace FreeAll.Helper;

/// <summary>
/// Elevated helper entry point. No commands are implemented yet; until the allowlisted IPC
/// protocol exists (F-033), the helper performs no work and exits with a failure code so that
/// callers fail closed.
/// </summary>
internal static class Program
{
    private const int NotImplementedExitCode = 1;

    private static int Main() => NotImplementedExitCode;
}
