using FreeAll.Core.Classification;

namespace FreeAll.Desktop.Services;

/// <summary>Moves between pages, optionally with context (drill-down from one view into another).</summary>
internal interface INavigator
{
    void NavigateTo(string key);

    /// <summary>Opens Storage Explorer at a folder of a stored scan.</summary>
    Task ShowFolderAsync(long sessionId, int directoryKey);

    /// <summary>Opens Large Files filtered to one scan and, optionally, one category.</summary>
    Task ShowLargestFilesAsync(long sessionId, FileCategory? category);
}
