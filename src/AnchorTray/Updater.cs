using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Anchor;

/// <summary>Latest GitHub release compared against the running version.</summary>
public sealed record UpdateInfo(Version Latest, Version Current, string DownloadUrl, string ReleasePageUrl)
{
    public bool IsNewer => Latest > Current;
}

/// <summary>
/// Checks the GitHub releases API for a newer version and downloads/launches
/// the installer. All failures are logged and surfaced as null/false; nothing
/// here throws to callers.
/// </summary>
public static class Updater
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Telesphoreo/Anchor/releases/latest";
    private const string InstallerSuffix = "-setup.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Per-operation timeouts are applied via CancellationTokenSource so a
        // slow installer download is not cut off by a client-wide timeout.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Anchor-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Query GitHub for the latest release. Returns null on any failure
    /// (no network, 404, unparseable tag); failures are logged, never thrown.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(FileLog log, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            using var response = await Http.GetAsync(LatestReleaseUrl, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log.Info($"Update check: GitHub returned {(int)response.StatusCode} {response.StatusCode}.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var root = json.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (ParseVersion(tag) is not { } latest)
            {
                log.Info($"Update check: could not parse release tag '{tag}'.");
                return null;
            }

            var releasePage = root.TryGetProperty("html_url", out var htmlElement)
                ? htmlElement.GetString() ?? ""
                : "";
            var downloadUrl = FindInstallerUrl(root) ?? releasePage;
            if (downloadUrl.Length == 0)
            {
                log.Info("Update check: release has no installer asset and no release page URL.");
                return null;
            }

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            log.Debug($"Update check: latest {latest}, current {current}.");
            return new UpdateInfo(latest, current, downloadUrl, releasePage.Length == 0 ? downloadUrl : releasePage);
        }
        catch (Exception ex)
        {
            log.Info($"Update check failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Download the installer to %TEMP%\Anchor-update and launch it via the
    /// shell (so the admin installer's UAC prompt appears). Returns true when
    /// the installer was launched — the caller should then exit so the
    /// installer can replace the running executable. When the release had no
    /// installer asset, opens the release page in the browser and returns
    /// false. Failures are logged and return false.
    /// </summary>
    public static async Task<bool> DownloadAndLaunchInstallerAsync(UpdateInfo info, FileLog log, CancellationToken ct = default)
    {
        try
        {
            if (!info.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                log.Info($"No installer asset in release; opening release page {info.ReleasePageUrl}.");
                Process.Start(new ProcessStartInfo(info.ReleasePageUrl) { UseShellExecute = true });
                return false;
            }

            var directory = Path.Combine(Path.GetTempPath(), "Anchor-update");
            Directory.CreateDirectory(directory);
            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
            if (fileName.Length == 0)
                fileName = $"Anchor-{info.Latest}{InstallerSuffix}";
            var filePath = Path.Combine(directory, fileName);

            log.Info($"Downloading installer {info.DownloadUrl} to {filePath}.");
            using (var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = File.Create(filePath);
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            log.Info($"Launching installer {filePath}.");
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"Update download/launch failed: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private static string? FindInstallerUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (name is null || !name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (asset.TryGetProperty("browser_download_url", out var urlElement) &&
                urlElement.GetString() is { Length: > 0 } url)
                return url;
        }

        return null;
    }

    /// <summary>
    /// Parse a release tag such as "v1.2.3", "1.2" or "v2" into a Version.
    /// Strips a leading 'v' and any pre-release/build suffix, and pads a bare
    /// major to major.0 so Version.TryParse accepts it.
    /// </summary>
    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var suffix = text.IndexOfAny(['-', '+', ' ']);
        if (suffix >= 0)
            text = text[..suffix];

        if (!text.Contains('.'))
            text += ".0";

        return Version.TryParse(text, out var version) ? version : null;
    }
}
