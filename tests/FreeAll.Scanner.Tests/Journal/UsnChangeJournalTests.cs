using System.Buffers.Binary;
using FreeAll.Application.Abstractions;
using FreeAll.Core.Scanning;
using FreeAll.Scanner.Journal;
using FreeAll.Scanner.Tests.Enumeration;

namespace FreeAll.Scanner.Tests.Journal;

/// <summary>The change journal as a standard user (TD-025), on the volume that holds the temp folder (NTFS on the development PC and CI).</summary>
public sealed class UsnChangeJournalTests
{
    private readonly UsnChangeJournal _journal = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scan_takes_a_checkpoint_and_later_changes_name_the_changed_folders()
    {
        using var tree = new ScanTree();
        tree.File(@"quiet\q.txt", 1);
        tree.Folder("busy");
        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);
        var checkpoint = Assert.IsType<JournalCheckpoint>(scan.Result.Checkpoint);

        tree.File(@"busy\new.txt", 10);

        var changes = await _journal.ReadChangesAsync(scan.Volume, checkpoint, 100_000, Token);

        Assert.Equal(JournalReadStatus.Read, changes.Status);
        Assert.Equal(checkpoint.JournalId, changes.Current!.JournalId);
        Assert.True(changes.Current.Usn > checkpoint.Usn);
        Assert.Contains(scan.Folder("busy").FileId!.Value, changes.Directories);
        Assert.DoesNotContain(scan.Folder("quiet").FileId!.Value, changes.Directories);
        Assert.True(changes.RecordCount > 0);
    }

    [Fact]
    public async Task Checkpoints_from_another_journal_or_the_future_are_not_trusted()
    {
        using var tree = new ScanTree();
        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);
        var checkpoint = scan.Result.Checkpoint!;

        Assert.Equal(JournalReadStatus.Recreated, (await _journal.ReadChangesAsync(scan.Volume, checkpoint with { JournalId = checkpoint.JournalId ^ 1 }, 100, Token)).Status);
        Assert.Equal(JournalReadStatus.InvalidCheckpoint, (await _journal.ReadChangesAsync(scan.Volume, checkpoint with { Usn = long.MaxValue / 2 }, 100, Token)).Status);

        // A journal that has wrapped no longer holds USN 0; a brand-new one still does.
        var fromStart = await _journal.ReadChangesAsync(scan.Volume, checkpoint with { Usn = 0 }, int.MaxValue, Token);
        Assert.True(fromStart.Status is JournalReadStatus.Truncated or JournalReadStatus.Read, fromStart.Status.ToString());
    }

    [Fact]
    public async Task Too_many_changed_folders_stop_the_read()
    {
        using var tree = new ScanTree();
        var scan = await ScanRunner.ScanAsync(tree.Root, cancellationToken: Token);
        for (var i = 0; i < 5; i++)
        {
            tree.File($@"f{i}\x.txt", 1);
        }

        var changes = await _journal.ReadChangesAsync(scan.Volume, scan.Result.Checkpoint!, 2, Token);

        Assert.Equal(JournalReadStatus.TooManyChanges, changes.Status);
        Assert.Null(changes.Current);
    }

    [Fact]
    public void Version_2_and_3_records_give_parent_and_folder_identities()
    {
        var data = new byte[60 + 76];
        var v2 = data.AsSpan(0, 60);
        BinaryPrimitives.WriteInt32LittleEndian(v2, 60);
        BinaryPrimitives.WriteUInt16LittleEndian(v2[4..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(v2[8..], 100);  // a file
        BinaryPrimitives.WriteUInt64LittleEndian(v2[16..], 5);   // in the root
        BinaryPrimitives.WriteInt64LittleEndian(v2[24..], 10);
        BinaryPrimitives.WriteUInt32LittleEndian(v2[52..], 0x20);

        var v3 = data.AsSpan(60, 76);
        BinaryPrimitives.WriteInt32LittleEndian(v3, 76);
        BinaryPrimitives.WriteUInt16LittleEndian(v3[4..], 3);
        BinaryPrimitives.WriteUInt64LittleEndian(v3[8..], 7);    // a folder, low half
        BinaryPrimitives.WriteUInt64LittleEndian(v3[16..], 1);   // high half
        BinaryPrimitives.WriteUInt64LittleEndian(v3[24..], 6);
        BinaryPrimitives.WriteInt64LittleEndian(v3[40..], 11);
        BinaryPrimitives.WriteUInt32LittleEndian(v3[68..], 0x10);

        var directories = new HashSet<UInt128>();
        long records = 0;
        Assert.True(UsnChangeJournal.ParseRecords(data, 1000, directories, ref records));

        Assert.Equal(2, records);
        Assert.Equal(new HashSet<UInt128> { 5, 6, new UInt128(1, 7) }, directories);

        directories.Clear();
        records = 0;
        Assert.False(UsnChangeJournal.ParseRecords(data, 11, directories, ref records)); // stops at the end position
        Assert.Equal(1, records);
    }

    [Fact]
    public void Malformed_records_are_rejected()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(data, 400);
        long records = 0;
        Assert.Throws<InvalidDataException>(() => UsnChangeJournal.ParseRecords(data, 1000, [], ref records));
    }
}
