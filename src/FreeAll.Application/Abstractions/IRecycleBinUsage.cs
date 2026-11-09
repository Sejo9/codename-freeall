namespace FreeAll.Application.Abstractions;

/// <summary>Windows' own figure for one drive's Recycle Bin, for the current user.</summary>
/// <param name="Root">The drive root asked about, for example <c>C:\</c>.</param>
/// <param name="Bytes">Size of the items in it, or null when Windows couldn't say.</param>
/// <param name="Items">Number of items, or null when Windows couldn't say.</param>
/// <param name="Problem">Why there is no figure, in plain language.</param>
public sealed record RecycleBinUsage(string Root, long? Bytes, long? Items, string? Problem = null);

/// <summary>Reads Recycle Bin sizes through Windows' supported query. Never changes the bin.</summary>
public interface IRecycleBinUsage
{
    /// <summary>One result per root, in the order given.</summary>
    Task<IReadOnlyList<RecycleBinUsage>> QueryAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken);
}
