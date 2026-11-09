using System.Runtime.InteropServices;
using FreeAll.Application.Abstractions;

namespace FreeAll.Infrastructure.Windows;

/// <summary>
/// Recycle Bin sizes from <c>SHQueryRecycleBinW</c>, the documented shell query (F-034). It reads the
/// current user's bin on each drive and changes nothing. A drive without a bin, or one that isn't ready,
/// gives no figure and a reason.
/// </summary>
public sealed partial class ShellRecycleBinUsage : IRecycleBinUsage
{
    public Task<IReadOnlyList<RecycleBinUsage>> QueryAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return Task.Run<IReadOnlyList<RecycleBinUsage>>(
            () =>
            {
                var results = new List<RecycleBinUsage>(roots.Count);
                foreach (var root in roots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(Query(root));
                }

                return results;
            },
            cancellationToken);
    }

    internal static RecycleBinUsage Query(string root)
    {
        // Only a plain drive root is asked about; the shell would otherwise interpret the string.
        if (root is not [var letter, ':', '\\'] || !char.IsAsciiLetter(letter))
        {
            return new RecycleBinUsage(root, null, null, "Only drives with a letter have a Recycle Bin figure.");
        }

        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        var result = SHQueryRecycleBinW(root, ref info);
        return result == 0
            ? new RecycleBinUsage(root, info.TotalBytes, info.ItemCount)
            : new RecycleBinUsage(root, null, null, "Windows didn't report a Recycle Bin size for this drive.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long TotalBytes;
        public long ItemCount;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string rootPath, ref ShQueryRbInfo info);
}
