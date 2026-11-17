using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SkillSync.Daemon.Desktop;

public sealed record UpdateStatus(
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("current_commit")] string? CurrentCommit,
    [property: JsonPropertyName("latest_version")] string? LatestVersion,
    [property: JsonPropertyName("latest_commit")] string? LatestCommit,
    [property: JsonPropertyName("update_available")] bool UpdateAvailable,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("release_url")] string? ReleaseUrl,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record UpdateApplyResult(
    [property: JsonPropertyName("started")] bool Started,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Runs a command and returns its exit code and standard output. Replaced by a fake in tests.</summary>
public interface ICommandRunner
{
    Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException(file + " did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, (await output.ConfigureAwait(false)).Trim());
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, string.Empty); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (-2, string.Empty); }
    }
}

/// <summary>
/// Finds and installs newer SkillHub builds. Two channels:
/// <list type="bullet">
/// <item><b>Source checkout</b> (a developer machine where <c>install.ps1</c> recorded its checkout): <c>git fetch</c> compares
/// <c>origin/main</c> with the commit baked into this build, and applying runs <c>git pull --ff-only</c> and <c>install.ps1</c>.</item>
/// <item><b>GitHub Release</b>: the public API (ETag-cached), falling back to the <c>gh</c> CLI so a private repository works for its
/// owner. Applying downloads <c>SkillHub-Setup.exe</c>, checks it against the release's <c>.sha256</c> file, runs it silently and
/// starts SkillHub again.</item>
/// </list>
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "KamilTahiri/SkillHub";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly HttpClient _http;
    private readonly ICommandRunner _runner;
    private readonly string _installDirectory;
    private readonly string _cacheDirectory;
    private readonly string _currentVersion;
    private readonly string? _currentCommit;
    private readonly Action<string> _launch;
    private UpdateStatus? _last;
    private ReleaseInfo? _release;

    private sealed record ReleaseInfo(string Version, string Tag, string? Commit, string? Url, string? SetupUrl, string? ChecksumUrl, string? Notes);
    private sealed record SourceCheckout([property: JsonPropertyName("path")] string Path);

    public UpdateService(HttpClient http, ICommandRunner runner, string installDirectory, string cacheDirectory,
        string? informationalVersion = null, Action<string>? launch = null)
    {
        _launch = launch ?? Launch;
        _http = http;
        _runner = runner;
        _installDirectory = installDirectory;
        _cacheDirectory = cacheDirectory;
        (_currentVersion, _currentCommit) = ParseInformationalVersion(informationalVersion ??
            typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
    }

    public UpdateStatus? LastStatus => _last;

    /// <summary>"1.4.0+abc123" → ("1.4.0", "abc123").</summary>
    public static (string Version, string? Commit) ParseInformationalVersion(string value)
    {
        var plus = value.IndexOf('+');
        return plus < 0 ? (value.Trim(), null) : (value[..plus].Trim(), value[(plus + 1)..].Trim() is { Length: > 0 } sha ? sha : null);
    }

    public static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value.Trim(), @"^v?(\d+)\.(\d+)(?:\.(\d+))?");
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0)
            : null;
    }

    private static bool SameCommit(string? a, string? b) =>
        a is { Length: >= 7 } && b is { Length: >= 7 } && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    /// <summary>The checkout recorded by install.ps1, when it still exists and still is a SkillHub checkout.</summary>
    public string? SourceCheckoutPath()
    {
        var file = Path.Combine(_installDirectory, "source-checkout.json");
        try
        {
            if (!File.Exists(file)) return null;
            var path = JsonSerializer.Deserialize<SourceCheckout>(File.ReadAllText(file))?.Path;
            return path is not null && Directory.Exists(Path.Combine(path, ".git")) && File.Exists(Path.Combine(path, "install.ps1")) ? path : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        var source = SourceCheckoutPath();
        var status = source is not null ? await CheckSourceAsync(source, ct).ConfigureAwait(false) : await CheckReleaseAsync(ct).ConfigureAwait(false);
        _last = status;
        return status;
    }

    private async Task<UpdateStatus> CheckSourceAsync(string checkout, CancellationToken ct)
    {
        var fetch = await _runner.RunAsync("git", ["-C", checkout, "fetch", "--quiet", "origin", "main"], checkout, ct).ConfigureAwait(false);
        var head = await _runner.RunAsync("git", ["-C", checkout, "rev-parse", "origin/main"], checkout, ct).ConfigureAwait(false);
        if (head.ExitCode != 0 || head.Output.Length < 7)
            return Status(null, null, false, "source", "source-checkout", null, "Could not read origin/main in " + checkout + ".",
                fetch.ExitCode != 0 ? "git fetch failed. Check your network or Git credentials." : "git rev-parse failed.");
        var latestCommit = head.Output.Split('\n')[0].Trim();
        var props = await _runner.RunAsync("git", ["-C", checkout, "show", "origin/main:Directory.Build.props"], checkout, ct).ConfigureAwait(false);
        var latestVersion = Regex.Match(props.Output, @"<Version>([^<]+)</Version>") is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        var newer = false;
        if (!SameCommit(_currentCommit, latestCommit))
        {
            // Only offer origin/main when it contains this build, so a local build ahead of the remote is never "updated" backwards.
            (int ExitCode, string Output) ancestor = _currentCommit is null ? (1, "") : await _runner.RunAsync("git", ["-C", checkout, "merge-base", "--is-ancestor", _currentCommit, latestCommit], checkout, ct).ConfigureAwait(false);
            newer = ancestor.ExitCode == 0 || ParseVersion(latestVersion) > ParseVersion(_currentVersion);
        }
        var summary = newer
            ? $"origin/main ({Short(latestCommit)}{(latestVersion is null ? "" : ", v" + latestVersion)}) is newer than this build ({Short(_currentCommit)})."
            : SameCommit(_currentCommit, latestCommit)
                ? "This build matches origin/main."
                : $"This build ({Short(_currentCommit)}) is ahead of origin/main ({Short(latestCommit)}). Push it to publish the change.";
        return Status(latestVersion, latestCommit, newer, "source", "source-checkout", $"https://github.com/{Repository}/commits/main", summary);
    }

    private async Task<UpdateStatus> CheckReleaseAsync(CancellationToken ct)
    {
        ReleaseInfo? release = null;
        string source = "github-api";
        string? error = null;
        try { release = await LatestFromApiAsync(ct).ConfigureAwait(false); }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException) { error = exception.Message; }
        if (release is null)
        {
            // Private repositories answer 404 to anonymous calls; the owner's gh CLI is signed in.
            release = await LatestFromGhAsync(ct).ConfigureAwait(false);
            source = "gh-cli";
        }
        if (release is null)
            return Status(null, null, false, "none", "none", null, "No release information was available.",
                error ?? "GitHub did not return a release. If the repository is private, install and sign in to the GitHub CLI (gh).");
        _release = release;
        var latest = ParseVersion(release.Version);
        var current = ParseVersion(_currentVersion);
        var newer = latest > current || (latest == current && release.Commit is not null && _currentCommit is not null && !SameCommit(release.Commit, _currentCommit));
        var summary = newer
            ? $"SkillHub {release.Version} is available (you have {_currentVersion})."
            : $"You have the latest release ({_currentVersion}).";
        return Status(release.Version, release.Commit, newer, newer && release.SetupUrl is not null ? "installer" : "none", source, release.Url, summary);
    }

    private UpdateStatus Status(string? latestVersion, string? latestCommit, bool available, string method, string source, string? url, string summary, string? error = null) =>
        new(_currentVersion, _currentCommit, latestVersion, latestCommit, available, available ? method : "none", source, url, summary, DateTimeOffset.UtcNow, error);

    private static string Short(string? sha) => sha is null ? "unknown" : sha[..Math.Min(7, sha.Length)];

    private async Task<ReleaseInfo?> LatestFromApiAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var cacheFile = Path.Combine(_cacheDirectory, "update-latest-release.json");
        CachedRelease? cached = null;
        try { if (File.Exists(cacheFile)) cached = JsonSerializer.Deserialize<CachedRelease>(await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false)); }
        catch (JsonException) { }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SkillHub", _currentVersion));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (cached?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string body;
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null) body = cached.Body;
        else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        else
        {
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(new CachedRelease(response.Headers.ETag?.ToString(), body), Json), ct).ConfigureAwait(false);
        }
        var release = ParseRelease(body);
        if (release is null) return null;
        string? commit = null;
        try
        {
            using var commitRequest = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/commits/{release.Tag}");
            commitRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("SkillHub", _currentVersion));
            commitRequest.Headers.Accept.ParseAdd("application/vnd.github.sha");
            using var commitResponse = await _http.SendAsync(commitRequest, ct).ConfigureAwait(false);
            if (commitResponse.IsSuccessStatusCode) commit = (await commitResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
        }
        catch (HttpRequestException) { }
        return release with { Commit = commit };
    }

    private sealed record CachedRelease(string? ETag, string Body);

    /// <summary>Reads a GitHub release JSON (REST API or gh --json) into the fields SkillHub needs.</summary>
    private static ReleaseInfo? ParseRelease(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : root.TryGetProperty("tagName", out var t2) ? t2.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || ParseVersion(tag) is null) return null;
        string? setup = null, checksum = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : asset.TryGetProperty("url", out var u2) ? u2.GetString() : null;
                if (string.Equals(name, "SkillHub-Setup.exe", StringComparison.OrdinalIgnoreCase)) setup = url ?? name;
                if (string.Equals(name, "SkillHub-Setup.exe.sha256", StringComparison.OrdinalIgnoreCase)) checksum = url ?? name;
            }
        }
        var html = root.TryGetProperty("html_url", out var h) ? h.GetString() : root.TryGetProperty("url", out var h2) ? h2.GetString() : null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        return new ReleaseInfo(ParseVersion(tag)!.ToString(3), tag!, null, html, setup, checksum, notes);
    }

    private async Task<ReleaseInfo?> LatestFromGhAsync(CancellationToken ct)
    {
        var view = await _runner.RunAsync("gh", ["release", "view", "--repo", Repository, "--json", "tagName,url,body,assets"], null, ct).ConfigureAwait(false);
        if (view.ExitCode != 0 || view.Output.Length == 0) return null;
        ReleaseInfo? release;
        try { release = ParseRelease(view.Output); }
        catch (JsonException) { return null; }
        if (release is null) return null;
        var commit = await _runner.RunAsync("gh", ["api", $"repos/{Repository}/commits/{release.Tag}", "--jq", ".sha"], null, ct).ConfigureAwait(false);
        return release with { Commit = commit.ExitCode == 0 && commit.Output.Length >= 7 ? commit.Output : null, SetupUrl = release.SetupUrl is null ? null : "gh" };
    }

    /// <summary>
    /// Prepares and starts the update, then the caller exits SkillHub. Source checkouts refuse to update over uncommitted changes.
    /// </summary>
    public async Task<UpdateApplyResult> PrepareAsync(CancellationToken ct = default)
    {
        var status = _last ?? await CheckAsync(ct).ConfigureAwait(false);
        if (!status.UpdateAvailable) return new UpdateApplyResult(false, "none", "SkillHub is already up to date.");
        if (status.Method == "source")
        {
            var checkout = SourceCheckoutPath() ?? throw new InvalidOperationException("The source checkout is no longer available.");
            var dirty = await _runner.RunAsync("git", ["-C", checkout, "status", "--porcelain", "--untracked-files=no"], checkout, ct).ConfigureAwait(false);
            if (dirty.ExitCode != 0 || dirty.Output.Length > 0)
                return new UpdateApplyResult(false, "source", "The checkout at " + checkout + " has uncommitted changes. Commit or stash them, then update again.");
            var script = Path.Combine(Path.GetTempPath(), "skillhub-update-source.ps1");
            var quotedCheckout = Quote(checkout);
            await File.WriteAllLinesAsync(script,
            [
                "$ErrorActionPreference = 'Stop'",
                "Start-Sleep -Seconds 2",
                $"git -C {quotedCheckout} pull --ff-only origin main",
                "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }",
                $"& powershell -NoProfile -ExecutionPolicy Bypass -File {Quote(Path.Combine(checkout, "install.ps1"))}"
            ], ct).ConfigureAwait(false);
            _launch(script);
            return new UpdateApplyResult(true, "source", "Pulling origin/main and running install.ps1. SkillHub restarts when the install finishes.");
        }
        var release = _release ?? throw new InvalidOperationException("Check for updates first.");
        var folder = Path.Combine(Path.GetTempPath(), "SkillHub-Update", release.Version);
        Directory.CreateDirectory(folder);
        var setup = Path.Combine(folder, "SkillHub-Setup.exe");
        var checksumFile = Path.Combine(folder, "SkillHub-Setup.exe.sha256");
        if (release.SetupUrl == "gh")
        {
            var download = await _runner.RunAsync("gh", ["release", "download", release.Tag, "--repo", Repository, "--pattern", "SkillHub-Setup.exe*", "--dir", folder, "--clobber"], folder, ct).ConfigureAwait(false);
            if (download.ExitCode != 0) throw new InvalidOperationException("gh could not download the release assets.");
        }
        else
        {
            await DownloadAsync(release.SetupUrl ?? throw new InvalidOperationException("The release has no SkillHub-Setup.exe."), setup, ct).ConfigureAwait(false);
            await DownloadAsync(release.ChecksumUrl ?? throw new InvalidOperationException("The release has no SkillHub-Setup.exe.sha256, so the download cannot be checked."), checksumFile, ct).ConfigureAwait(false);
        }
        VerifyChecksum(setup, checksumFile);
        var installed = Path.Combine(_installDirectory, "SkillHub.exe");
        var script2 = Path.Combine(folder, "apply-update.ps1");
        await File.WriteAllLinesAsync(script2,
        [
            "$ErrorActionPreference = 'Stop'",
            $"Wait-Process -Id {Environment.ProcessId} -Timeout 30 -ErrorAction SilentlyContinue",
            $"$setup = Start-Process -FilePath {Quote(setup)} -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru",
            $"if ($setup.ExitCode -eq 0) {{ Start-Process -FilePath {Quote(installed)} }}"
        ], ct).ConfigureAwait(false);
        _launch(script2);
        return new UpdateApplyResult(true, "installer", $"Installing SkillHub {release.Version}. SkillHub closes now and opens again when setup finishes.");
    }

    /// <summary>A PowerShell single-quoted literal.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    public static void VerifyChecksum(string setup, string checksumFile)
    {
        if (!File.Exists(setup) || !File.Exists(checksumFile)) throw new InvalidOperationException("The downloaded update is incomplete.");
        var expected = File.ReadAllText(checksumFile).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        using var stream = File.OpenRead(setup);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded installer does not match its published SHA-256 checksum, so it was not run.");
    }

    private async Task DownloadAsync(string url, string target, CancellationToken ct)
    {
        if (!url.StartsWith("https://github.com/" + Repository + "/", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Updates are only downloaded from the SkillHub GitHub repository.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SkillHub", _currentVersion));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var output = File.Create(target);
        await response.Content.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    /// <summary>Starts the update script detached so it outlives SkillHub (setup replaces SkillHub's files).</summary>
    private static void Launch(string script)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script }) start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }
}
