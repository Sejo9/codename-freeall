using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using FreeAll.Core.Cleanup;
using FreeAll.Core.Scanning;
using FreeAll.Core.Volumes;

namespace FreeAll.Cleanup.Files;

/// <summary>What a folder holds now.</summary>
/// <param name="Files">Names of files.</param>
/// <param name="Folders">Subfolders.</param>
/// <param name="Bytes">Logical size. Each file counts once when walked by identity.</param>
/// <param name="HasReparsePoints">A link or other reparse point is inside. Such a folder is never changed.</param>
/// <param name="HasCloudOnlyFiles">Something inside is only in the cloud.</param>
/// <param name="HasUnreadableParts">Part of it couldn't be listed, so nothing about it can be promised.</param>
internal sealed record TreeSummary(long Files, long Folders, long Bytes, bool HasReparsePoints, bool HasCloudOnlyFiles, bool HasUnreadableParts);

/// <summary>Walks, hashes and removes folder trees. Never follows a reparse point. Compiled into the helper too.</summary>
internal static class TargetTree
{
    /// <summary>Changes when the digest does; a stored digest of another version can't be checked.</summary>
    public const int DigestVersion = 1;

    private static readonly EnumerationOptions Listing = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>Lists everything below a folder.</summary>
    /// <param name="volume">Its volume.</param>
    /// <param name="relativePath">Its volume-relative path.</param>
    /// <param name="countEachFileOnce">Open each file to count hard links once, as the scan's totals do. Slower.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    public static TreeSummary Summarize(VolumeId volume, string relativePath, bool countEachFileOnce, CancellationToken cancellationToken)
    {
        long files = 0, folders = 0, bytes = 0;
        bool reparse = false, cloud = false, unreadable = false;
        var seen = new HashSet<UInt128>();
        var pending = new Stack<string>();
        pending.Push(relativePath);
        while (pending.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(SafeTarget.NativePath(volume, folder)).EnumerateFileSystemInfos("*", Listing))
                {
                    var child = VolumePath.Combine(folder, entry.Name);
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && !IsListedReparsePoint(volume, child))
                    {
                        reparse = true;
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        folders++;
                        pending.Push(child);
                        continue;
                    }

                    files++;
                    cloud |= CloudFiles.IsCloudOnly(entry.Attributes);
                    if (!countEachFileOnce || IsFirstName(volume, child, seen))
                    {
                        bytes += ((FileInfo)entry).Length;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable = true;
            }
        }

        return new TreeSummary(files, folders, bytes, reparse, cloud, unreadable);
    }

    /// <summary>
    /// A digest of contents. For a file, the SHA-256 of its bytes. For a folder, the SHA-256 of a sorted
    /// list of every path, size and file digest in it, so one value proves the whole tree came back intact.
    /// </summary>
    public static byte[] Digest(string nativePath, bool isFolder, CancellationToken cancellationToken)
    {
        if (!isFolder)
        {
            return DigestFile(nativePath, cancellationToken);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        DigestFolder(nativePath, string.Empty, hash, cancellationToken);
        return hash.GetHashAndReset();
    }

    /// <summary>Deletes an item, bottom-up, each part through its own checked handle. Stops at the first problem, leaving the rest.</summary>
    /// <returns>0, or the Win32 error that stopped it.</returns>
    public static int Remove(VolumeId volume, string relativePath, CancellationToken cancellationToken)
    {
        var status = SafeTarget.Open(volume, relativePath, forChange: true, out var target);
        if (status != TargetOpenStatus.Opened)
        {
            return ErrorFor(status);
        }

        using (target)
        {
            // Nothing that became a link is ever walked into or removed in passing.
            if (target!.State.ReparseTag != 0 && !IsLocalTag(target.State))
            {
                return ErrorLinkFound;
            }

            if (target.State.IsDirectory)
            {
                foreach (var entry in new DirectoryInfo(SafeTarget.NativePath(volume, relativePath)).EnumerateFileSystemInfos("*", Listing).Select(e => e.Name).ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var error = Remove(volume, VolumePath.Combine(relativePath, entry), cancellationToken);
                    if (error != 0)
                    {
                        return error;
                    }
                }
            }

            return target.DeleteNow();
        }
    }

    /// <summary>Creates a quarantine folder that only this user, SYSTEM and Administrators can open (docs/10-cleanup-quarantine.md §6).</summary>
    public static void EnsureQuarantineFolder(string nativePath)
    {
        var directory = new DirectoryInfo(nativePath);
        if (directory.Exists)
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        directory.Create(security);
    }

    /// <summary>The volume-relative slot for one quarantined item: <c>\…\FreeAll Quarantine\{operation}\{number}\{name}</c>.</summary>
    public static string QuarantineSlot(string rootRelativePath, Guid operation, int number, string name)
    {
        var quarantine = VolumePath.Combine(rootRelativePath, ProtectedPathPolicy.QuarantineFolderName);
        return VolumePath.Combine(VolumePath.Combine(VolumePath.Combine(quarantine, operation.ToString("N")), number.ToString(System.Globalization.CultureInfo.InvariantCulture)), name);
    }

    public const int ErrorLinkFound = 4390; // ERROR_NOT_A_REPARSE_POINT: reused to say "a link was found where none should be".

    private static int ErrorFor(TargetOpenStatus status) => status switch
    {
        TargetOpenStatus.Missing => 2,
        TargetOpenStatus.AccessDenied => 5,
        TargetOpenStatus.InUse => 32,
        TargetOpenStatus.Redirected => ErrorLinkFound,
        _ => 1,
    };

    /// <summary>An allow list: only cloud and Windows-compressed items are ordinary contents. Anything else that is a reparse point stops the walk.</summary>
    private static bool IsListedReparsePoint(VolumeId volume, string relativePath)
    {
        if (SafeTarget.Open(volume, relativePath, forChange: false, out var target) != TargetOpenStatus.Opened)
        {
            return false;
        }

        using (target)
        {
            return IsLocalTag(target!.State);
        }
    }

    private static bool IsLocalTag(in TargetState state) =>
        ReparsePoints.Classify(state.ReparseTag) is ReparsePointKind.WofCompressed or ReparsePointKind.CloudPlaceholder && !ReparsePoints.IsNameSurrogate(state.ReparseTag);

    private static bool IsFirstName(VolumeId volume, string relativePath, HashSet<UInt128> seen)
    {
        if (SafeTarget.Open(volume, relativePath, forChange: false, out var target) != TargetOpenStatus.Opened)
        {
            return true;
        }

        using (target)
        {
            return target!.State.LinkCount <= 1 || target.State.FileId is not { } id || seen.Add(id);
        }
    }

    private static byte[] DigestFile(string nativePath, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(nativePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 0,
        });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return hash.GetHashAndReset();
    }

    private static void DigestFolder(string nativePath, string prefix, IncrementalHash hash, CancellationToken cancellationToken)
    {
        // Sorted one folder at a time: the same tree always gives the same digest, whatever order Windows lists it in.
        var entries = new DirectoryInfo(nativePath).EnumerateFileSystemInfos("*", Listing).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = prefix + "/" + entry.Name;
            if (entry is DirectoryInfo)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(name + "/\n"));
                DigestFolder(entry.FullName, name, hash, cancellationToken);
            }
            else
            {
                var digest = Convert.ToHexString(DigestFile(entry.FullName, cancellationToken));
                hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{name}|{((FileInfo)entry).Length}|{digest}\n")));
            }
        }
    }
}
