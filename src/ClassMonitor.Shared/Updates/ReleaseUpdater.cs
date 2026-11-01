using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClassMonitor.Shared.Updates;

public enum UpdateComponent { Teacher, Agent }
public enum UpdateState { Current, Available, Unavailable }
public sealed record ReleaseUpdate(Version Version, Uri DownloadUri, string Sha256, long Size);
public sealed record UpdateCheck(UpdateState State, ReleaseUpdate? Release, string Message);
public sealed record StagedUpdate(string Directory, string Application, string Worker, string Sha256);

/// <summary>Only stable releases and the exact Windows package in our public repository are accepted.</summary>
public sealed class ReleaseUpdater : IDisposable
{
    public const string Repository = "aamergamal318-source/ClassMonitor";
    public const string AssetName = "ClassMonitor-windows-x64.zip";
    public const long MaximumPackageSize = 512L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _stagingRoot;
    public static Version CurrentVersion => typeof(ReleaseUpdater).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public static string UpdateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassMonitor", "updates");
    public static string FileName(UpdateComponent component) => component == UpdateComponent.Teacher ? "ClassMonitorTeacher.exe" : "ClassMonitorAgent.exe";

    public ReleaseUpdater(HttpMessageHandler? handler = null, string? stagingRoot = null)
    {
        _stagingRoot = stagingRoot ?? UpdateRoot;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClassMonitor", CurrentVersion.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateCheck> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
            response.EnsureSuccessStatusCode();
            return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token), current);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException
            or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            return new UpdateCheck(UpdateState.Unavailable, null, "تعذّر فحص التحديثات. تحقق من الاتصال وأعد المحاولة. / Update check unavailable.");
        }
    }

    public static UpdateCheck ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var release = doc.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("A stable release is required.");
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version.Build < 0)
            throw new InvalidDataException("Invalid release version.");
        var normalized = new Version(version.Major, version.Minor, version.Build);
        var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        if (normalized <= installed)
            return new UpdateCheck(UpdateState.Current, null, $"البرنامج محدّث / Up to date ({installed})");
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == AssetName).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("Expected one Windows update package.");
        var asset = assets[0];
        string address = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal) ||
            !uri.AbsolutePath.EndsWith('/' + AssetName, StringComparison.Ordinal) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Untrusted update URL.");
        string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("A GitHub SHA-256 digest is required.");
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumPackageSize) throw new InvalidDataException("Invalid update size.");
        return new UpdateCheck(UpdateState.Available, new ReleaseUpdate(normalized, uri, digest[7..], size),
            $"تحديث متاح / Update available: {normalized}");
    }

    public async Task<StagedUpdate> StageAsync(ReleaseUpdate update, UpdateComponent component,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        string directory = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "package.zip");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await _http.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
            {
                received += read;
                if (received > update.Size || received > MaximumPackageSize) throw new InvalidDataException("Update download exceeded its declared size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                progress?.Report((int)(100 * received / update.Size));
            }
            if (received != update.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update checksum verification failed. The installed application was not changed.");
        }
        return await Task.Run(() => ExtractVerifiedPackage(archive, directory, component, update.Version), timeout.Token).ConfigureAwait(false);
    }

    public static StagedUpdate ExtractVerifiedPackage(string archive, string directory, UpdateComponent component, Version expectedVersion)
    {
        using var zip = ZipFile.OpenRead(archive);
        string role = component == UpdateComponent.Teacher ? "teacher" : "agent";
        string application = Extract($"ClassMonitor/{role}/{FileName(component)}", FileName(component));
        string worker = Extract("ClassMonitor/updater/ClassMonitorUpdater.exe", "ClassMonitorUpdater.exe");
        var info = FileVersionInfo.GetVersionInfo(application);
        if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != expectedVersion)
            throw new InvalidDataException("The downloaded application version does not match the release tag.");
        using var applicationFile = File.OpenRead(application);
        return new StagedUpdate(directory, application, worker, Convert.ToHexString(SHA256.HashData(applicationFile)));

        string Extract(string entryName, string fileName)
        {
            var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/') == entryName).ToArray();
            if (entries.Length != 1 || entries[0].Length < 2 || entries[0].Length > MaximumPackageSize)
                throw new InvalidDataException($"Invalid update entry: {entryName}");
            string target = Path.Combine(directory, fileName);
            entries[0].ExtractToFile(target, overwrite: false);
            using var file = File.OpenRead(target);
            if (file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new InvalidDataException("Invalid Windows executable.");
            return target;
        }
    }

    public void Dispose() => _http.Dispose();
}
