using FreeAll.Core.Residue;

namespace FreeAll.Residue.Temporary;

/// <summary>
/// A documented location of temporary or cache data (F-024, docs/03-feature-catalogue.md §H).
/// </summary>
/// <param name="Id">Stable ID, used for decisions. Never reuse an ID for a different location.</param>
/// <param name="Title">Name shown in the list.</param>
/// <param name="Patterns">
/// Volume-relative folder patterns. <c>{profile}</c> is the current user's profile folder; a segment of
/// <c>*</c> matches any one folder name (for example browser profiles).
/// </param>
/// <param name="Purpose">What the data is for.</param>
/// <param name="Consequence">What happens if it is removed.</param>
/// <param name="Risk">Risk of removing it.</param>
/// <param name="AgeDays">Items unchanged for at least this many days are counted as "not used recently"; 0 when age says nothing.</param>
/// <param name="ManagedByWindows">Windows manages it; FreeAll only reports it and points to Windows' own tools (PR-011).</param>
public sealed record TemporaryRule(
    string Id,
    string Title,
    IReadOnlyList<string> Patterns,
    string Purpose,
    string Consequence,
    RemovalRisk Risk,
    int AgeDays,
    bool ManagedByWindows = false);

/// <summary>The rule catalogue. Versioned: increase <see cref="Version"/> whenever a rule changes.</summary>
public static class TemporaryRules
{
    public const int Version = 1;

    private const string CloseFirst = " Close the program first; files in use can't be removed.";

    public static IReadOnlyList<TemporaryRule> All { get; } =
    [
        new(
            "user-temp",
            "Your temporary files",
            [@"{profile}\AppData\Local\Temp"],
            "Programs and installers put working files here while they run. Most are never cleaned up.",
            "Files older than a week are rarely needed. A program that is running, or an installation that was interrupted, may still need newer ones." + CloseFirst,
            RemovalRisk.Low,
            7),
        new(
            "windows-temp",
            "Windows temporary files",
            [@"\Windows\Temp"],
            "Windows and services put working files here.",
            "Usually safe once older than a week. Your account normally can't read this folder; Windows' Storage Sense cleans it.",
            RemovalRisk.Low,
            7,
            ManagedByWindows: true),
        new(
            "chrome-cache",
            "Google Chrome cache",
            [@"{profile}\AppData\Local\Google\Chrome\User Data\*\Cache", @"{profile}\AppData\Local\Google\Chrome\User Data\*\Code Cache", @"{profile}\AppData\Local\Google\Chrome\User Data\*\GPUCache"],
            "Copies of web pages, images and scripts, so sites load faster.",
            "Chrome downloads them again; sites load more slowly at first. Sign-ins, history and bookmarks are kept elsewhere." + CloseFirst,
            RemovalRisk.Low,
            0),
        new(
            "edge-cache",
            "Microsoft Edge cache",
            [@"{profile}\AppData\Local\Microsoft\Edge\User Data\*\Cache", @"{profile}\AppData\Local\Microsoft\Edge\User Data\*\Code Cache", @"{profile}\AppData\Local\Microsoft\Edge\User Data\*\GPUCache"],
            "Copies of web pages, images and scripts, so sites load faster.",
            "Edge downloads them again; sites load more slowly at first. Sign-ins, history and favourites are kept elsewhere." + CloseFirst,
            RemovalRisk.Low,
            0),
        new(
            "firefox-cache",
            "Mozilla Firefox cache",
            [@"{profile}\AppData\Local\Mozilla\Firefox\Profiles\*\cache2"],
            "Copies of web pages and images, so sites load faster.",
            "Firefox downloads them again; sites load more slowly at first. Bookmarks and passwords are in the roaming profile, not here." + CloseFirst,
            RemovalRisk.Low,
            0),
        new(
            "app-caches",
            "Chat and editor app caches",
            [
                @"{profile}\AppData\Roaming\Code\Cache", @"{profile}\AppData\Roaming\Code\CachedData", @"{profile}\AppData\Roaming\Code\Code Cache",
                @"{profile}\AppData\Roaming\discord\Cache", @"{profile}\AppData\Roaming\discord\Code Cache",
                @"{profile}\AppData\Roaming\Slack\Cache", @"{profile}\AppData\Roaming\Slack\Code Cache",
            ],
            "Web content caches of apps built on Chromium (Visual Studio Code, Discord, Slack).",
            "The app downloads or rebuilds them; it may start more slowly once." + CloseFirst,
            RemovalRisk.Low,
            0),
        new(
            "shader-caches",
            "Graphics shader caches",
            [@"{profile}\AppData\Local\D3DSCache", @"{profile}\AppData\Local\NVIDIA\DXCache", @"{profile}\AppData\Local\NVIDIA\GLCache", @"{profile}\AppData\Local\AMD\DxCache", @"{profile}\AppData\Local\AMD\GLCache"],
            "Compiled graphics programs, kept so games and apps don't compile them again.",
            "They are rebuilt when needed; games may stutter or load more slowly the first time.",
            RemovalRisk.Low,
            30),
        new(
            "error-reports",
            "Error reports and crash dumps",
            [@"{profile}\AppData\Local\Microsoft\Windows\WER", @"{profile}\AppData\Local\CrashDumps"],
            "Reports and memory dumps written when programs crash.",
            "Only needed to investigate a crash or send a report to the developer.",
            RemovalRisk.Low,
            30),
        new(
            "package-caches",
            "Developer package caches",
            [@"{profile}\AppData\Local\npm-cache", @"{profile}\AppData\Local\pip\cache", @"{profile}\AppData\Local\NuGet\v3-cache", @"{profile}\AppData\Local\Yarn\Cache", @"{profile}\.gradle\caches"],
            "Downloaded packages kept by npm, pip, NuGet, Yarn and Gradle so builds don't download them again.",
            "They are downloaded again on the next build; builds without internet access may fail until then.",
            RemovalRisk.Medium,
            90),
        new(
            "thumbnails",
            "Thumbnail and icon caches",
            [@"{profile}\AppData\Local\Microsoft\Windows\Explorer"],
            "Previews of pictures and icons that File Explorer shows.",
            "Windows rebuilds them, so folders of pictures show slowly for a while. Use Windows' Disk Cleanup or Storage Sense to clear them.",
            RemovalRisk.Low,
            0,
            ManagedByWindows: true),
        new(
            "windows-update",
            "Windows Update downloads",
            [@"\Windows\SoftwareDistribution\Download"],
            "Update files Windows downloaded.",
            "Windows manages these; removing them by hand can break an update in progress. Use Settings > System > Storage.",
            RemovalRisk.Medium,
            0,
            ManagedByWindows: true),
        new(
            "recycle-bin",
            "Recycle Bin",
            [@"\$Recycle.Bin"],
            "Files you deleted, kept so you can restore them.",
            "Emptying the Recycle Bin removes them for good. Empty it from the Recycle Bin itself, after checking what's inside.",
            RemovalRisk.Medium,
            0,
            ManagedByWindows: true),
    ];
}
