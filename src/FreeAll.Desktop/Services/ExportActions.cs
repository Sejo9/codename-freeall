using System.IO;
using System.Windows;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Export;
using Microsoft.Extensions.Logging;

namespace FreeAll.Desktop.Services;

/// <summary>Asks where to save a file. Returns null when the user cancels.</summary>
internal interface IFileSaveDialog
{
    /// <param name="title">Dialog title.</param>
    /// <param name="suggestedName">File name without extension.</param>
    /// <param name="formats">Formats offered, first one preselected.</param>
    (string Path, ExportFormat Format)? Ask(string title, string suggestedName, IReadOnlyList<ExportFormat> formats);
}

internal sealed class WpfFileSaveDialog : IFileSaveDialog
{
    public (string Path, ExportFormat Format)? Ask(string title, string suggestedName, IReadOnlyList<ExportFormat> formats)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = string.Join('|', formats.Select(f => $"{ExportActions.Describe(f)} (*.{ExportActions.Extension(f)})|*.{ExportActions.Extension(f)}")),
            AddExtension = true,
            OverwritePrompt = true,
        };
        var owner = System.Windows.Application.Current?.MainWindow;
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true)
        {
            return null;
        }

        var index = Math.Clamp(dialog.FilterIndex - 1, 0, formats.Count - 1);
        return (dialog.FileName, formats[index]);
    }
}

/// <summary>Asks a yes/no question. Only for decisions that lose something (TD: no over-confirmation).</summary>
internal interface IConfirmation
{
    bool Confirm(string title, string message, string action);
}

internal sealed class MessageBoxConfirmation : IConfirmation
{
    public bool Confirm(string title, string message, string action)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var text = $"{message}\n\nChoose Yes to {action}.";
        var result = owner is { IsVisible: true }
            ? MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}

/// <summary>
/// Runs an export chosen by the user (F-035): asks for a file, writes to a temporary file next to it,
/// then moves it into place so a failed export never leaves a half-written file under the chosen name.
/// </summary>
internal sealed partial class ExportActions
{
    public static readonly IReadOnlyList<ExportFormat> AllFormats = [ExportFormat.Csv, ExportFormat.Json, ExportFormat.Html, ExportFormat.Pdf];

    private readonly IReportExporter _exporter;
    private readonly IFileSaveDialog _dialog;
    private readonly IUserNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<ExportActions> _logger;

    public ExportActions(IReportExporter exporter, IFileSaveDialog dialog, IUserNotifier notifier, ILogger<ExportActions> logger, TimeProvider? time = null)
    {
        _exporter = exporter;
        _dialog = dialog;
        _notifier = notifier;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "csv",
        ExportFormat.Json => "json",
        ExportFormat.Html => "html",
        _ => "pdf",
    };

    public static string Describe(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "CSV spreadsheet",
        ExportFormat.Json => "JSON data",
        ExportFormat.Html => "HTML report",
        _ => "PDF report",
    };

    /// <summary>Asks for a file and writes the report the builder makes for the chosen format.</summary>
    /// <returns>A plain-language result for the page, or null when the user cancelled.</returns>
    public async Task<string?> ExportAsync(string title, string suggestedName, Func<ExportFormat, ExportReport> build, IReadOnlyList<ExportFormat>? formats = null)
    {
        ArgumentNullException.ThrowIfNull(build);

        if (_dialog.Ask(title, suggestedName, formats ?? AllFormats) is not { } choice)
        {
            return null;
        }

        var partial = choice.Path + ".partial";
        try
        {
            var report = build(choice.Format);
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await _exporter.ExportAsync(report, choice.Format, stream, CancellationToken.None);
            }

            File.Move(partial, choice.Path, overwrite: true);
            return $"Saved {Path.GetFileName(choice.Path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PersistenceException)
        {
            LogExportFailed(_logger, ex);
            TryDelete(partial);
            _notifier.ShowError("The export wasn't saved", ex is PersistenceException
                ? "Scan results couldn't be read from the FreeAll database."
                : "The file couldn't be written. Check that the folder exists and that you can save files there.");
            return "The export wasn't saved.";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left behind; harmless and visibly named.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An export failed")]
    private static partial void LogExportFailed(ILogger logger, Exception exception);
}
