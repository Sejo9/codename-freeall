using System.Text;
using FreeAll.Core.Cleanup;

namespace FreeAll.Security.Tests;

public sealed class ProtectedPathPolicyTests
{
    [Theory]
    [InlineData(@"\")]
    [InlineData(@"\Windows")]
    [InlineData(@"\windows\system32\kernel32.dll")]
    [InlineData(@"\Windows\Temp\a.tmp")]
    [InlineData(@"\Windows.old\x")]
    [InlineData(@"\Boot\BCD")]
    [InlineData(@"\EFI\Microsoft\Boot\bootmgfw.efi")]
    [InlineData(@"\Recovery\WindowsRE\Winre.wim")]
    [InlineData(@"\$Recycle.Bin\S-1-5\$R1.bin")]
    [InlineData(@"\$WinREAgent\x")]
    [InlineData(@"\$Windows.~BT\x")]
    [InlineData(@"\System Volume Information\x")]
    [InlineData(@"\Config.Msi\x")]
    [InlineData(@"\pagefile.sys")]
    [InlineData(@"\hiberfil.sys")]
    [InlineData(@"\swapfile.sys")]
    [InlineData(@"\bootmgr")]
    [InlineData(@"\Program Files")]
    [InlineData(@"\Program Files (x86)")]
    [InlineData(@"\Program Files\WindowsApps\app\a.exe")]
    [InlineData(@"\Program Files\Windows Defender\a.exe")]
    [InlineData(@"\Program Files\Common Files\a.dll")]
    [InlineData(@"\ProgramData")]
    [InlineData(@"\ProgramData\Microsoft\Windows\a")]
    [InlineData(@"\Users")]
    [InlineData(@"\Users\Ana")]
    [InlineData(@"\Users\Ana\Documents")]
    [InlineData(@"\Users\Ana\Desktop")]
    [InlineData(@"\Users\Ana\OneDrive")]
    [InlineData(@"\Users\Ana\NTUSER.DAT")]
    [InlineData(@"\Users\Ana\AppData")]
    [InlineData(@"\Users\Ana\AppData\Local")]
    [InlineData(@"\Users\Ana\AppData\Roaming")]
    [InlineData(@"\Users\Ana\AppData\Local\Microsoft")]
    [InlineData(@"\Users\Ana\AppData\Local\Packages")]
    [InlineData(@"\FreeAll Quarantine")]
    [InlineData(@"\FreeAll Quarantine\abc\1\a.bin")]
    [InlineData(@"\Data\Backup\FreeAll Quarantine\abc\1\a.bin")]
    public void Critical_paths_are_blocked(string path) => Assert.Equal(PolicyLevel.Blocked, ProtectedPathPolicy.Evaluate(path).Level);

    [Theory]
    [InlineData("")]
    [InlineData("relative\\a.bin")]
    [InlineData(@"\a\..\Windows\b")]
    [InlineData(@"\a\.\b")]
    [InlineData(@"\a\\b")]
    [InlineData(@"\a:b")]
    [InlineData("\\a\0b")]
    public void A_path_that_isnt_clean_is_blocked_rather_than_interpreted(string path) => Assert.Equal(PolicyLevel.Blocked, ProtectedPathPolicy.Evaluate(path).Level);

    [Theory]
    [InlineData(@"\Program Files\OldApp\app.exe", PolicyReason.ProgramFolder)]
    [InlineData(@"\ProgramData\OldApp\data.bin", PolicyReason.ApplicationData)]
    [InlineData(@"\Users\Ana\AppData\Local\OldApp\x", PolicyReason.ApplicationData)]
    [InlineData(@"\Users\Ana\Documents\report.docx", PolicyReason.UserLibrary)]
    [InlineData(@"\Users\Ana\Downloads\setup.exe", PolicyReason.UserLibrary)]
    public void Things_worth_a_second_look_say_so(string path, PolicyReason reason)
    {
        var decision = ProtectedPathPolicy.Evaluate(path);

        Assert.Equal(PolicyLevel.Caution, decision.Level);
        Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData(@"\Data\old.iso")]
    [InlineData(@"\Users\Ana\scratch.bin")]
    [InlineData(@"\WindowsBackup\a.bin")]
    public void Ordinary_files_are_allowed(string path) => Assert.Equal(PolicyLevel.Allowed, ProtectedPathPolicy.Evaluate(path).Level);

    [Fact]
    public void Freealls_own_folders_and_anything_holding_them_are_blocked()
    {
        string[] own = [@"\Users\Ana\AppData\Local\FreeAll"];

        Assert.Equal(PolicyReason.FreeAllData, ProtectedPathPolicy.Evaluate(@"\Users\Ana\AppData\Local\FreeAll\freeall.db", own).Reason);
        Assert.Equal(PolicyLevel.Blocked, ProtectedPathPolicy.Evaluate(@"\Users\Ana\AppData\Local\FreeAll", own).Level);
        Assert.NotEqual(PolicyReason.FreeAllData, ProtectedPathPolicy.Evaluate(@"\Users\Ana\AppData\Local\Other\a", own).Reason);
    }
}

public sealed class HelperRequestValidatorTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static readonly Guid Volume = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly HelperTarget File = new(Volume, @"\Program Files\OldApp\a.dll", false, Id, 100, 10);
    private static readonly string Slot = $@"\FreeAll Quarantine\{Id}\3\a.dll";

    private static HelperRequest Operation(HelperTarget? target, ApprovedAction? action, string? destination = null) =>
        new(HelperProtocol.Version, 1, HelperCommand.ExecuteApprovedOperation, target, action, destination);

    [Fact]
    public void A_well_formed_operation_passes()
    {
        Assert.Null(HelperRequestValidator.Validate(Operation(File, ApprovedAction.Delete)));
        Assert.Null(HelperRequestValidator.Validate(Operation(File, ApprovedAction.MoveToQuarantine, Slot)));
        Assert.Null(HelperRequestValidator.Validate(Operation(File with { RelativePath = Slot }, ApprovedAction.RestoreFromQuarantine, @"\Data\a.dll")));
        Assert.Null(HelperRequestValidator.Validate(new HelperRequest(HelperProtocol.Version, 1, HelperCommand.Hello)));
    }

    [Fact]
    public void Nothing_passes_without_being_read() => Assert.NotNull(HelperRequestValidator.Validate(null));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Another_protocol_version_is_refused(int version) =>
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File, ApprovedAction.Delete) with { Version = version }));

    [Fact]
    public void An_unknown_command_is_refused() =>
        Assert.NotNull(HelperRequestValidator.Validate(new HelperRequest(HelperProtocol.Version, 1, (HelperCommand)99)));

    [Fact]
    public void An_unknown_action_is_refused() =>
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File, (ApprovedAction)99)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("zz23456789abcdef0123456789abcdef")]
    public void A_path_alone_is_never_enough(string? fileId) =>
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File with { FileId = fileId }, ApprovedAction.Delete)));

    [Theory]
    [InlineData(@"\Windows\System32\a.dll")]
    [InlineData(@"\Users\Ana")]
    [InlineData(@"\pagefile.sys")]
    [InlineData(@"\$Recycle.Bin\x")]
    [InlineData(@"\FreeAll Quarantine\x")]
    public void Protected_targets_are_refused_for_any_change(string path)
    {
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File with { RelativePath = path }, ApprovedAction.Delete)));
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File with { RelativePath = path }, ApprovedAction.MoveToQuarantine, Slot)));
    }

    [Theory]
    [InlineData(@"\")]
    [InlineData(@"relative\a")]
    [InlineData(@"\a\..\Windows\b")]
    [InlineData(@"\a\")]
    [InlineData(@"\a\\b")]
    [InlineData(@"C:\Windows\a")]
    [InlineData(@"\\server\share\a")]
    [InlineData(@"\\?\C:\Windows\a")]
    public void Paths_that_arent_exact_are_refused_rather_than_cleaned_up(string path) =>
        Assert.NotNull(HelperRequestValidator.Validate(new HelperRequest(HelperProtocol.Version, 1, HelperCommand.InspectPath, File with { RelativePath = path })));

    [Fact]
    public void A_missing_volume_is_refused() =>
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File with { VolumeGuid = Guid.Empty }, ApprovedAction.Delete)));

    [Theory]
    [InlineData(null)]
    [InlineData(@"\Windows\a.dll")]
    [InlineData(@"\Temp\a.dll")]
    [InlineData(@"\FreeAll Quarantine\a.dll")]
    [InlineData(@"\FreeAll Quarantine\notahexvalue\3\a.dll")]
    [InlineData(@"\Data\FreeAll Quarantine\0123456789abcdef0123456789abcdef\3\a.dll")]
    [InlineData(@"\FreeAll Quarantine\0123456789abcdef0123456789abcdef\x\a.dll")]
    [InlineData(@"\FreeAll Quarantine\0123456789abcdef0123456789abcdef\3\sub\a.dll")]
    public void Quarantine_moves_can_only_go_to_a_quarantine_slot(string? destination) =>
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File, ApprovedAction.MoveToQuarantine, destination)));

    [Fact]
    public void Deleting_takes_no_destination() =>
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File, ApprovedAction.Delete, Slot)));

    [Fact]
    public void Only_items_in_quarantine_can_be_restored() =>
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File, ApprovedAction.RestoreFromQuarantine, @"\Data\a.dll")));

    [Theory]
    [InlineData(@"\Windows\a.dll")]
    [InlineData(@"\Program Files")]
    [InlineData(null)]
    public void Nothing_is_restored_into_a_protected_place(string? destination) =>
        Assert.NotNull(HelperRequestValidator.Validate(Operation(File with { RelativePath = Slot }, ApprovedAction.RestoreFromQuarantine, destination)));

    [Fact]
    public void Extra_arguments_are_refused()
    {
        Assert.NotNull(HelperRequestValidator.Validate(new HelperRequest(HelperProtocol.Version, 1, HelperCommand.Hello, File)));
        Assert.NotNull(HelperRequestValidator.Validate(new HelperRequest(HelperProtocol.Version, 1, HelperCommand.Shutdown, null, ApprovedAction.Delete)));
        Assert.NotNull(HelperRequestValidator.Validate(new HelperRequest(HelperProtocol.Version, 1, HelperCommand.InspectPath, File, ApprovedAction.Delete)));
    }
}

public sealed class HelperFramingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_round_trips()
    {
        using var stream = new MemoryStream();
        var request = new HelperRequest(HelperProtocol.Version, 7, HelperCommand.Hello);

        await HelperProtocol.WriteAsync(stream, HelperProtocol.Serialize(request), Ct);
        stream.Position = 0;
        var bytes = await HelperProtocol.ReadAsync(stream, Ct);

        Assert.Equal(request, HelperProtocol.Deserialize<HelperRequest>(bytes!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(HelperProtocol.MaxMessageBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task A_length_out_of_bounds_is_refused_before_anything_is_allocated(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));

        Assert.Null(await HelperProtocol.ReadAsync(stream, Ct));
    }

    [Fact]
    public async Task A_message_cut_short_is_refused()
    {
        using var stream = new MemoryStream([.. BitConverter.GetBytes(100), 1, 2, 3]);

        Assert.Null(await HelperProtocol.ReadAsync(stream, Ct));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}{}")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"Version":1,"RequestId":1,"Command":0,"Shell":"cmd.exe"}""")]
    public void Anything_but_this_protocol_is_refused(string text) =>
        Assert.Null(HelperProtocol.Deserialize<HelperRequest>(Encoding.UTF8.GetBytes(text)));
}
