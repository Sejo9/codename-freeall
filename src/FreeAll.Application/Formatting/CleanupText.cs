using FreeAll.Application.Cleanup;
using FreeAll.Core.Cleanup;

namespace FreeAll.Application.Formatting;

/// <summary>Plain-language text for cleanup. Says what actually happened, and never more.</summary>
public static class CleanupText
{
    public const string DatabaseUnavailable = "Cleanup is off because the FreeAll database isn't available, so what happens couldn't be recorded.";

    public static string Mode(CleanupMode mode) => mode switch
    {
        CleanupMode.RecycleBin => "Move to Recycle Bin",
        CleanupMode.QuarantineSameVolume => "Quarantine on the same drive",
        CleanupMode.QuarantineCentral => "Quarantine in a folder you choose",
        CleanupMode.PermanentDelete => "Permanently delete",
        _ => "Unknown",
    };

    public static string Kind(CleanupOperationKind kind) => kind switch
    {
        CleanupOperationKind.Cleanup => "Cleanup",
        CleanupOperationKind.Restore => "Restore",
        CleanupOperationKind.QuarantineDelete => "Deleted from quarantine",
        _ => "Unknown",
    };

    public static string State(CleanupOperationState state) => state switch
    {
        CleanupOperationState.Running => "Running",
        CleanupOperationState.Completed => "Completed",
        CleanupOperationState.CompletedWithProblems => "Finished with problems",
        CleanupOperationState.Cancelled => "Stopped",
        CleanupOperationState.Interrupted => "Interrupted",
        _ => "Unknown",
    };

    public static string ItemState(CleanupItemState state) => state switch
    {
        CleanupItemState.Pending => "Not started",
        CleanupItemState.InProgress => "In progress",
        CleanupItemState.Succeeded => "Done",
        CleanupItemState.Failed => "Failed",
        CleanupItemState.Skipped => "Skipped",
        CleanupItemState.Interrupted => "Interrupted: check this item",
        CleanupItemState.CopiedOriginalKept => "Copied, original kept",
        _ => "Unknown",
    };

    public static string Status(TargetStatus status) => status switch
    {
        TargetStatus.Ready => "Ready",
        TargetStatus.Blocked => "Protected",
        TargetStatus.Changed => "Changed since review",
        TargetStatus.Missing => "Gone",
        TargetStatus.Unreadable => "Can't be read",
        TargetStatus.InUse => "In use",
        TargetStatus.Redirected => "Path leads elsewhere",
        TargetStatus.DriveUnavailable => "Drive not connected",
        TargetStatus.Covered => "Included with its folder",
        TargetStatus.NoOtherCopy => "No other copy confirmed",
        _ => "Unknown",
    };

    public static string Integrity(QuarantineIntegrity integrity) => integrity switch
    {
        QuarantineIntegrity.Intact => "Intact",
        QuarantineIntegrity.DriveUnavailable => "Drive not connected",
        QuarantineIntegrity.Missing => "Missing from quarantine",
        QuarantineIntegrity.Corrupt => "Damaged: contents don't match",
        _ => "Unknown",
    };

    public static string Policy(PolicyReason reason) => reason switch
    {
        PolicyReason.VolumeRoot => "This is the top of a drive.",
        PolicyReason.WindowsFolder => "This is part of Windows. Use Windows' own cleanup tools for it.",
        PolicyReason.BootOrRecovery => "Windows needs this to start or recover.",
        PolicyReason.SystemFile => "This is a Windows system file.",
        PolicyReason.WindowsManaged => "Windows manages this location.",
        PolicyReason.WholeProgramsFolder => "This folder holds every installed program.",
        PolicyReason.WholeUserFolder => "This is a whole user or library folder. Choose what's inside it instead.",
        PolicyReason.SharedApplicationData => "Many programs share this folder.",
        PolicyReason.Quarantine => "This is FreeAll's quarantine. Use Activity to restore or delete what it holds.",
        PolicyReason.FreeAllData => "FreeAll needs this folder.",
        PolicyReason.ProgramFolder => "This is in a programs folder: removing it can stop a program working.",
        PolicyReason.ApplicationData => "A program may still need this data or its settings.",
        PolicyReason.UserLibrary => "This is in one of your personal folders.",
        _ => string.Empty,
    };
}
