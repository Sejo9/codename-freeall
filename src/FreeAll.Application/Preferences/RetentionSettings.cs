using System.Globalization;
using FreeAll.Application.Abstractions;
using FreeAll.Application.Scanning;

namespace FreeAll.Application.Preferences;

/// <summary>The user's scan-history retention choice (F-018), stored as preferences.</summary>
public interface IRetentionSettings
{
    /// <summary>The stored policy, or <see cref="RetentionPolicy.Default"/> when unset, invalid or unreadable.</summary>
    Task<RetentionPolicy> GetAsync(CancellationToken cancellationToken);

    /// <exception cref="PersistenceException">The choice couldn't be saved.</exception>
    Task SetAsync(RetentionPolicy policy, CancellationToken cancellationToken);
}

public sealed class RetentionSettings : IRetentionSettings
{
    /// <summary>Choices offered in Settings. Anything else read from storage falls back to the default.</summary>
    public static readonly IReadOnlyList<int> DetailedScanChoices = [1, 2, 3, 5];

    public static readonly IReadOnlyList<int?> HistoryDayChoices = [null, 365, 180, 90, 30];

    private readonly IUserPreferences _preferences;

    public RetentionSettings(IUserPreferences preferences) => _preferences = preferences;

    public async Task<RetentionPolicy> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var detailed = await _preferences.GetAsync(PreferenceKeys.RetentionDetailedScans, cancellationToken).ConfigureAwait(false);
            var days = await _preferences.GetAsync(PreferenceKeys.RetentionHistoryDays, cancellationToken).ConfigureAwait(false);

            var detailedValue = int.TryParse(detailed, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && DetailedScanChoices.Contains(d)
                ? d
                : RetentionPolicy.Default.DetailedScansPerDrive;
            int? daysValue = int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && HistoryDayChoices.Contains(n)
                ? n
                : RetentionPolicy.Default.HistoryDays;
            return new RetentionPolicy(detailedValue, daysValue);
        }
        catch (PersistenceException)
        {
            // Keeping more history than asked for is the safe direction.
            return RetentionPolicy.Default;
        }
    }

    public async Task SetAsync(RetentionPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!DetailedScanChoices.Contains(policy.DetailedScansPerDrive) || !HistoryDayChoices.Contains(policy.HistoryDays))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Unsupported retention choice.");
        }

        await _preferences.SetAsync(PreferenceKeys.RetentionDetailedScans, policy.DetailedScansPerDrive.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await _preferences.SetAsync(PreferenceKeys.RetentionHistoryDays, policy.HistoryDays?.ToString(CultureInfo.InvariantCulture) ?? "forever", cancellationToken).ConfigureAwait(false);
    }
}
