using FreeAll.Core.Residue;

namespace FreeAll.Application.Formatting;

/// <summary>Plain-language names for residue and temporary-file values. Never colour alone.</summary>
public static class ResidueText
{
    public static string Confidence(ResidueConfidence confidence) => confidence switch
    {
        ResidueConfidence.High => "High",
        ResidueConfidence.Medium => "Medium",
        ResidueConfidence.Low => "Low",
        _ => "Unknown",
    };

    public static string ConfidenceMeaning(ResidueConfidence confidence) => confidence switch
    {
        ResidueConfidence.High => "Strong evidence that the program it belongs to is no longer installed.",
        ResidueConfidence.Medium => "Some evidence that the program it belongs to is no longer installed.",
        ResidueConfidence.Low => "Weak evidence: no installed program matches it, but it may belong to a portable program, a tool or a component.",
        _ => "Not enough information to judge.",
    };

    public static string Risk(RemovalRisk risk) => risk switch
    {
        RemovalRisk.High => "High",
        RemovalRisk.Medium => "Medium",
        RemovalRisk.Low => "Low",
        _ => "Unknown",
    };

    public static string RiskMeaning(RemovalRisk risk) => risk switch
    {
        RemovalRisk.High => "Removing it could lose documents, settings or programs, or stop software working.",
        RemovalRisk.Medium => "It may hold settings, saved data or licences.",
        RemovalRisk.Low => "Usually caches or data that is recreated when needed.",
        _ => "It couldn't be read completely, so what it holds isn't known.",
    };

    public static string Decision(DecisionKind decision, string? owner) => decision switch
    {
        DecisionKind.Keep => "Kept",
        DecisionKind.Ignore => "Ignored",
        _ => owner is null ? "In use" : $"In use by {owner}",
    };

    public static string Source(ApplicationSource source) => source switch
    {
        ApplicationSource.Win32Machine => "Desktop app (all users)",
        ApplicationSource.Win32Machine32 => "Desktop app, 32-bit (all users)",
        ApplicationSource.Win32User => "Desktop app (your account)",
        ApplicationSource.CommandPath => "Program on your PATH",
        _ => "Store app",
    };
}
