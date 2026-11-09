using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace FreeAll.Installer;

internal static partial class Native
{
    public const int IdOk = 1;
    public const int IdCancel = 2;
    public const int IdRetry = 4;
    public const int IdYes = 6;
    public const int IdNo = 7;

    public const int ButtonOk = 0x1;
    public const int ButtonYes = 0x2;
    public const int ButtonNo = 0x4;
    public const int ButtonCancel = 0x8;
    public const int ButtonRetry = 0x10;

    public static readonly IntPtr IconWarning = new(0xFFFF);
    public static readonly IntPtr IconError = new(0xFFFE);
    public static readonly IntPtr IconInformation = new(0xFFFD);

    private const uint MbYesNo = 0x4;
    private const uint MbYesNoCancel = 0x3;
    private const uint MbOk = 0x0;
    private const uint MbRetryCancel = 0x5;
    private const uint MbIconWarning = 0x30;
    private const uint MbIconInformation = 0x40;
    private const uint MbIconError = 0x10;

    /// <summary>A task dialog, or a message box if task dialogs aren't available. Returns the button ID.</summary>
    public static int Ask(string title, string instruction, string content, int buttons, IntPtr icon)
    {
        try
        {
            var hr = TaskDialog(IntPtr.Zero, IntPtr.Zero, title, instruction, content, buttons, icon, out var pressed);
            if (hr >= 0)
            {
                return pressed;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Common controls 6 not activated; fall back to a message box below.
        }

        var type = buttons switch
        {
            ButtonYes | ButtonNo => MbYesNo,
            ButtonYes | ButtonNo | ButtonCancel => MbYesNoCancel,
            ButtonRetry | ButtonCancel => MbRetryCancel,
            _ => MbOk,
        };
        type |= icon == IconError ? MbIconError : icon == IconWarning ? MbIconWarning : MbIconInformation;
        return MessageBoxW(IntPtr.Zero, instruction + "\n\n" + content, title, type);
    }

    [LibraryImport("comctl32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int TaskDialog(IntPtr owner, IntPtr instance, string title, string mainInstruction, string content, int buttons, IntPtr icon, out int pressed);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    /// <summary>Creates a shell link (.lnk) through the documented ShellLink COM object.</summary>
    public static void CreateShortcut(string shortcutPath, string target, string workingDirectory, string description)
    {
        var clsid = new Guid("00021401-0000-0000-C000-000000000046");
        var iid = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, iid, out var pointer));
        try
        {
            var wrappers = new StrategyBasedComWrappers();
            var link = (IShellLinkW)wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(IntPtr file, int length, IntPtr findData, uint flags);

    void GetIDList(out IntPtr list);

    void SetIDList(IntPtr list);

    void GetDescription(IntPtr name, int length);

    void SetDescription(string name);

    void GetWorkingDirectory(IntPtr directory, int length);

    void SetWorkingDirectory(string directory);

    void GetArguments(IntPtr arguments, int length);

    void SetArguments(string arguments);

    void GetHotkey(out short hotkey);

    void SetHotkey(short hotkey);

    void GetShowCmd(out int showCommand);

    void SetShowCmd(int showCommand);

    void GetIconLocation(IntPtr iconPath, int length, out int index);

    void SetIconLocation(string iconPath, int index);

    void SetRelativePath(string path, uint reserved);

    void Resolve(IntPtr window, uint flags);

    void SetPath(string file);
}

/// <summary>IPersistFile, with IPersist's one method first, in vtable order.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    void Load(string fileName, uint mode);

    void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

    void SaveCompleted(string fileName);

    void GetCurFile(out IntPtr fileName);
}
