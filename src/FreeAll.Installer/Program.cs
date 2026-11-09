namespace FreeAll.Installer;

/// <summary>
/// <c>FreeAll-Setup-x.y.z-x64.exe</c> installs or updates; the installed <c>FreeAll.Installer.exe</c> uninstalls.
/// <list type="bullet">
/// <item><c>/uninstall</c> uninstall (asks about data; keeping it is the default).</item>
/// <item><c>/silent</c> no questions: install, or uninstall keeping data unless <c>/remove-data</c> is also given.</item>
/// <item><c>/update</c> started by FreeAll's update: wait for FreeAll to close, install without asking again, start it.</item>
/// <item><c>/pack &lt;app folder&gt; &lt;version&gt; &lt;output&gt;</c> build a setup file (release pipeline).</item>
/// <item><c>/root</c>, <c>/start-menu</c>, <c>/data</c>, <c>/registry-key</c> and <c>/log</c> redirect every location, for tests.
/// A redirected registry key must be under <c>HKCU\Software\FreeAll.Tests\</c>.</item>
/// </list>
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            // Never a dialog for scripted runs.
            if (args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/pack", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine(ex.Message);
            }
            else
            {
                new DialogUi(null).Fail("Setup couldn't start", ex.Message);
            }

            return (int)ExitCode.Failed;
        }

        TextWriter? log = null;
        try
        {
            if (options.LogPath is not null)
            {
                log = new StreamWriter(options.LogPath, append: true) { AutoFlush = true };
            }

            return (int)Run(options, log);
        }
#pragma warning disable CA1031 // Last resort: record why, and report failure instead of crashing without a word.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log?.WriteLine("Unexpected " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine(ex.ToString());
            if (!options.Silent && options.Pack is null)
            {
                new DialogUi(null).Fail("Setup stopped unexpectedly", "Nothing that was already installed was removed. Run setup again.");
            }

            return (int)ExitCode.Failed;
        }
        finally
        {
            log?.Dispose();
        }
    }

    private static ExitCode Run(Options options, TextWriter? log)
    {
        IInstallerUi ui = options.Silent || options.Pack is not null ? new SilentUi(log, options.RemoveData) : new DialogUi(log);
        var own = Environment.ProcessPath ?? throw new InvalidOperationException("The installer's own path is unknown.");

        if (options.Pack is { } pack)
        {
            Payload.Pack(own, pack.AppDirectory, pack.Version, pack.Output);
            return ExitCode.Success;
        }

        _ = Native.CoInitializeEx(IntPtr.Zero, 0x2 /* COINIT_APARTMENTTHREADED */);
        var engine = new InstallEngine(options.Locations, ui, new WindowsRegistration());

        try
        {
            using var setup = new FileStream(own, FileMode.Open, FileAccess.Read, FileShare.Read);
            var location = Payload.Find(setup);
            if (options.Uninstall || location is null)
            {
                setup.Dispose();
                return engine.Uninstall(own);
            }

            return engine.Install(setup, location, options.Update);
        }
        catch (InvalidDataException ex)
        {
            ui.Fail("FreeAll couldn't be installed", ex.Message);
            return ExitCode.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ui.Log(ex.GetType().Name + ": " + ex.Message);
            ui.Fail("Setup didn't finish", "A file couldn't be read or written. Nothing that was already installed was removed.");
            return ExitCode.Failed;
        }
    }
}

internal sealed record PackRequest(string AppDirectory, Version Version, string Output);

internal sealed record Options(bool Uninstall, bool Silent, bool Update, bool RemoveData, InstallLocations Locations, string? LogPath, PackRequest? Pack)
{
    public static Options Parse(IReadOnlyList<string> args)
    {
        var defaults = InstallLocations.ForCurrentUser();
        string root = defaults.Root, startMenu = defaults.StartMenuFolder, data = defaults.DataDirectory, key = defaults.RegistryKey;
        string? log = null;
        PackRequest? pack = null;
        bool uninstall = false, silent = false, update = false, removeData = false;

        for (var i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException("A value is missing after " + args[i] + ".");

            switch (args[i].ToUpperInvariant())
            {
                case "/UNINSTALL": uninstall = true; break;
                case "/SILENT": silent = true; break;
                case "/UPDATE": update = true; break;
                case "/REMOVE-DATA": removeData = true; break;
                case "/ROOT": root = FullPath(Next()); break;
                case "/START-MENU": startMenu = FullPath(Next()); break;
                case "/DATA": data = FullPath(Next()); break;
                case "/LOG": log = FullPath(Next()); break;
                case "/REGISTRY-KEY":
                    key = Next();
                    if (!key.StartsWith(InstallLocations.TestRegistryPrefix, StringComparison.Ordinal) || key.Length <= InstallLocations.TestRegistryPrefix.Length || key.Contains("..", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Only keys under HKCU\\" + InstallLocations.TestRegistryPrefix + " can replace the uninstall entry.");
                    }

                    break;
                case "/PACK":
                    var folder = FullPath(Next());
                    var version = Version.TryParse(Next(), out var parsed) ? parsed : throw new ArgumentException("The version isn't valid.");
                    pack = new PackRequest(folder, version, FullPath(Next()));
                    break;
                default:
                    throw new ArgumentException("Unknown option " + args[i] + ".");
            }
        }

        if (removeData && !(silent && uninstall))
        {
            throw new ArgumentException("/remove-data applies only to /uninstall /silent.");
        }

        if (Path.GetPathRoot(root) is not { } top || string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(top), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("FreeAll can't be installed at the top of a drive.");
        }

        return new Options(uninstall, silent, update, removeData, new InstallLocations(root, startMenu, data, key), log, pack);
    }

    private static string FullPath(string value) => Path.GetFullPath(value);
}
