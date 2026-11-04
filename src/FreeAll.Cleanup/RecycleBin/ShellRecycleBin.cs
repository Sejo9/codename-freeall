using System.Runtime.InteropServices;
using FreeAll.Application.Cleanup;
using FreeAll.Core.Volumes;
using Microsoft.Win32;

namespace FreeAll.Cleanup.RecycleBin;

/// <summary>
/// Moves items to the Recycle Bin through the shell. Measured on Windows 11 (TD-042): with the usual
/// flags, the shell silently and permanently deletes what it can't recycle and still reports success. So
/// each delete is watched: the shell says, just before acting, whether it will recycle, and when it says
/// no the item is refused and stays where it is. Success needs the shell to name the item it created.
/// </summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private const string BitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    // FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI | FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE
    private const uint OperationFlags = 0x4 | 0x10 | 0x40 | 0x400 | 0x80000 | 0x100000;

    private const uint RecycleIfPossible = 0x80; // TSF_DELETE_RECYCLE_IF_POSSIBLE
    private const int Cancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    private static readonly Guid FileOperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    public RecycleAvailability Check(VolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (volume.PrimaryMountPoint is null)
        {
            return new RecycleAvailability(false, "This drive has no drive letter, so Windows can't use its Recycle Bin.");
        }

        if (volume.Kind != DriveKind.Fixed)
        {
            return new RecycleAvailability(false, "Removable drives usually have no Recycle Bin. Choose quarantine or permanent deletion instead.");
        }

        return NukesOnDelete(volume.Id) ? new RecycleAvailability(false, "The Recycle Bin is turned off for this drive in Windows.") : RecycleAvailability.Yes;
    }

    public Task<RecycleResult> RecycleAsync(string displayPath, Func<bool> stillValid)
    {
        ArgumentNullException.ThrowIfNull(displayPath);
        ArgumentNullException.ThrowIfNull(stillValid);

        // The shell's file operations are apartment-threaded.
        var completion = new TaskCompletionSource<RecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Recycle(displayPath, stillValid));
            }
#pragma warning disable CA1031 // Crossing a thread: the caller records the failure against the item.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                completion.SetResult(new RecycleResult(RecycleOutcome.Failed, ex.HResult));
            }
        })
        { IsBackground = true, Name = "FreeAll Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static bool NukesOnDelete(VolumeId volume)
    {
        using var perVolume = Registry.CurrentUser.OpenSubKey($@"{BitBucket}\Volume\{{{volume.VolumeGuid:D}}}");
        if (perVolume?.GetValue("NukeOnDelete") is int perVolumeValue)
        {
            return perVolumeValue != 0;
        }

        using var all = Registry.CurrentUser.OpenSubKey(BitBucket);
        return all?.GetValue("NukeOnDelete") is int value && value != 0;
    }

    private static RecycleResult Recycle(string path, Func<bool> stillValid)
    {
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
        if (hr < 0)
        {
            return new RecycleResult(RecycleOutcome.Failed, hr);
        }

        var type = Type.GetTypeFromCLSID(FileOperationClass) ?? throw new InvalidOperationException("The Windows file operation component isn't available.");
        var operation = (IFileOperation)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("The Windows file operation component couldn't start."));
        var sink = new Sink(stillValid);
        try
        {
            operation.SetOperationFlags(OperationFlags);
            operation.DeleteItem(item, sink);
            operation.PerformOperations();
        }
        catch (COMException ex) when (sink.Refusal is null)
        {
            return new RecycleResult(RecycleOutcome.Failed, ex.HResult);
        }
        catch (COMException) when (sink.Refusal is not null)
        {
            // The sink stopped it. Its reason is what matters, not the shell's cancelled error.
        }

        if (sink.Refusal is { } refusal)
        {
            return new RecycleResult(refusal);
        }

        if (sink.BinPath is not null)
        {
            return new RecycleResult(RecycleOutcome.Recycled, 0, sink.BinPath);
        }

        var stillThere = File.Exists(path) || Directory.Exists(path);
        return new RecycleResult(stillThere ? RecycleOutcome.Failed : RecycleOutcome.GoneWithoutProof, sink.Result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
#pragma warning disable SYSLIB1054 // The out parameter is a COM interface, which source-generated marshalling doesn't take here.
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
#pragma warning restore SYSLIB1054

    /// <summary>Called by the shell around the delete. Returning a failure stops the item.</summary>
    [ComVisible(true)]
    private sealed class Sink(Func<bool> stillValid) : IFileOperationProgressSink
    {
        public RecycleOutcome? Refusal { get; private set; }

        public string? BinPath { get; private set; }

        public int Result { get; private set; }

        public int PreDeleteItem(uint flags, IShellItem item)
        {
            if ((flags & RecycleIfPossible) == 0)
            {
                Refusal = RecycleOutcome.WouldBePermanent;
                return Cancelled;
            }

            if (!stillValid())
            {
                Refusal = RecycleOutcome.NoLongerValid;
                return Cancelled;
            }

            return 0;
        }

        public int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem? created)
        {
            Result = hr;
            if (hr >= 0 && created is not null)
            {
                created.GetDisplayName(0x80058000, out var name); // SIGDN_FILESYSPATH
                BinPath = Marshal.PtrToStringUni(name);
                Marshal.FreeCoTaskMem(name);
            }

            return 0;
        }

        public int StartOperations() => 0;

        public int FinishOperations(int hr) => 0;

        public int PreRenameItem(uint flags, IShellItem item, string name) => 0;

        public int PostRenameItem(uint flags, IShellItem item, string name, int hr, IShellItem created) => 0;

        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name) => 0;

        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int hr, IShellItem created) => 0;

        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) => 0;

        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int hr, IShellItem created) => 0;

        public int PreNewItem(uint flags, IShellItem destination, string name) => 0;

        public int PostNewItem(uint flags, IShellItem destination, string name, string template, uint attributes, int hr, IShellItem created) => 0;

        public int UpdateProgress(uint total, uint done) => 0;

        public int ResetTimer() => 0;

        public int PauseTimer() => 0;

        public int ResumeTimer() => 0;
    }
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler();

    void GetParent();

    void GetDisplayName(uint form, out IntPtr name);

    void GetAttributes();

    void Compare();
}

[ComImport]
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
    [PreserveSig]
    int StartOperations();

    [PreserveSig]
    int FinishOperations(int hr);

    [PreserveSig]
    int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig]
    int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem created);

    [PreserveSig]
    int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig]
    int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem created);

    [PreserveSig]
    int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig]
    int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem created);

    [PreserveSig]
    int PreDeleteItem(uint flags, IShellItem item);

    [PreserveSig]
    int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem? created);

    [PreserveSig]
    int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig]
    int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int hr, IShellItem created);

    [PreserveSig]
    int UpdateProgress(uint total, uint done);

    [PreserveSig]
    int ResetTimer();

    [PreserveSig]
    int PauseTimer();

    [PreserveSig]
    int ResumeTimer();
}

[ComImport]
[Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    uint Advise(IFileOperationProgressSink sink);

    void Unadvise(uint cookie);

    void SetOperationFlags(uint flags);

    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

    void SetProgressDialog(IntPtr dialog);

    void SetProperties(IntPtr properties);

    void SetOwnerWindow(uint window);

    void ApplyPropertiesToItem(IShellItem item);

    void ApplyPropertiesToItems(IntPtr items);

    void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);

    void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);

    void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);

    void MoveItems(IntPtr items, IShellItem destination);

    void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);

    void CopyItems(IntPtr items, IShellItem destination);

    void DeleteItem(IShellItem item, IFileOperationProgressSink sink);

    void DeleteItems(IntPtr items, IFileOperationProgressSink sink);

    uint NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);

    void PerformOperations();

    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}
