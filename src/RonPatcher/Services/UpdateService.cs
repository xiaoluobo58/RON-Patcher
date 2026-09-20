using System.Net.Http.Headers;
using System.Text.Json;

namespace RonPatcher.Services;

public sealed record UpdateInfo(string TagName, string Version, string ReleaseUrl, DateTimeOffset? PublishedAt, string? Notes);

public sealed class UpdateService
{
    private const string ReleasesUrl = "https://api.github.com/repos/xiaoluobo58/RON-Patcher/releases/latest";
    private static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
    private readonly HttpClient _httpClient = new();

    public UpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RON-Patcher", CurrentVersion.ToString(3)));
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagProperty) ? tagProperty.GetString() : null;
        var url = root.TryGetProperty("html_url", out var urlProperty) ? urlProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
            return null;
        var versionText = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var version) || version <= CurrentVersion)
            return null;
        DateTimeOffset? published = null;
        if (root.TryGetProperty("published_at", out var publishedProperty) &&
            publishedProperty.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(publishedProperty.GetString(), out var parsed))
            published = parsed;
        var notes = root.TryGetProperty("body", out var bodyProperty) ? bodyProperty.GetString() : null;
        return new UpdateInfo(tag, version.ToString(), url, published, notes);
    }
}
