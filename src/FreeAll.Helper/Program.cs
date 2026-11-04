using System.Globalization;

namespace FreeAll.Helper;

/// <summary>
/// The elevated helper's entry point. Started by the app through UAC for one session, it serves one
/// client on one pipe, then exits. It is never a service and does nothing unless a verified client asks.
/// </summary>
internal static class Program
{
    private const int BadArguments = 2;

    private static async Task<int> Main(string[] args)
    {
        if (HelperArguments.Parse(args) is not { } arguments)
        {
            return BadArguments;
        }

        return await new HelperServer(arguments, ClientVerifier.Production(arguments)).RunAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <param name="PipeName">The session's pipe, a name the client made up for this session.</param>
/// <param name="ClientProcessId">The only process allowed to connect.</param>
/// <param name="ClientSid">The user the pipe is opened to.</param>
internal sealed record HelperArguments(string PipeName, int ClientProcessId, string ClientSid)
{
    public const string PipePrefix = "FreeAll.Helper.";

    /// <returns>The arguments, or null unless they are exactly what the app passes.</returns>
    public static HelperArguments? Parse(string[] args)
    {
        if (args.Length != 6 || args[0] != "--pipe" || args[2] != "--client" || args[4] != "--sid")
        {
            return null;
        }

        if (!args[1].StartsWith(PipePrefix, StringComparison.Ordinal) || args[1].Length != PipePrefix.Length + 32 || !args[1][PipePrefix.Length..].All(char.IsAsciiHexDigit))
        {
            return null;
        }

        return int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 && args[5].StartsWith("S-1-", StringComparison.Ordinal)
            ? new HelperArguments(args[1], pid, args[5])
            : null;
    }
}
