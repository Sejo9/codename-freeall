namespace FreeAll.Installer;

internal enum DataChoice
{
    Keep,
    Remove,
    Cancel,
}

/// <summary>Every question the installer asks. Silent runs answer with the safe choice.</summary>
internal interface IInstallerUi
{
    bool IsSilent { get; }

    bool Confirm(string instruction, string content);

    DataChoice AskAboutData(string instruction, string content);

    bool AskRetry(string instruction, string content);

    void Inform(string instruction, string content);

    void Fail(string instruction, string content);

    void Log(string line);
}

internal sealed class DialogUi : IInstallerUi
{
    private const string Title = "FreeAll Setup";
    private readonly TextWriter? _log;

    public DialogUi(TextWriter? log) => _log = log;

    public bool IsSilent => false;

    public bool Confirm(string instruction, string content) =>
        Native.Ask(Title, instruction, content, Native.ButtonYes | Native.ButtonNo, Native.IconInformation) == Native.IdYes;

    /// <summary>Phrased as "keep?", so the default first button is the one that deletes nothing.</summary>
    public DataChoice AskAboutData(string instruction, string content) =>
        Native.Ask(Title, instruction, content, Native.ButtonYes | Native.ButtonNo | Native.ButtonCancel, Native.IconWarning) switch
        {
            Native.IdYes => DataChoice.Keep,
            Native.IdNo => DataChoice.Remove,
            _ => DataChoice.Cancel,
        };

    public bool AskRetry(string instruction, string content) =>
        Native.Ask(Title, instruction, content, Native.ButtonRetry | Native.ButtonCancel, Native.IconWarning) == Native.IdRetry;

    public void Inform(string instruction, string content)
    {
        Log(instruction);
        Native.Ask(Title, instruction, content, Native.ButtonOk, Native.IconInformation);
    }

    public void Fail(string instruction, string content)
    {
        Log("FAILED: " + instruction + " " + content);
        Native.Ask(Title, instruction, content, Native.ButtonOk, Native.IconError);
    }

    public void Log(string line) => _log?.WriteLine(DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " " + line);
}

/// <summary>No questions: installs, refuses anything risky, and keeps data unless removal was asked for on the command line.</summary>
internal sealed class SilentUi : IInstallerUi
{
    private readonly TextWriter? _log;
    private readonly bool _removeData;

    public SilentUi(TextWriter? log, bool removeData)
    {
        _log = log;
        _removeData = removeData;
    }

    public bool IsSilent => true;

    public bool Confirm(string instruction, string content) => true;

    public DataChoice AskAboutData(string instruction, string content) => _removeData ? DataChoice.Remove : DataChoice.Keep;

    public bool AskRetry(string instruction, string content)
    {
        Log("Not retried: " + instruction);
        return false;
    }

    public void Inform(string instruction, string content) => Log(instruction);

    public void Fail(string instruction, string content) => Log("FAILED: " + instruction + " " + content);

    public void Log(string line) => _log?.WriteLine(DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " " + line);
}
