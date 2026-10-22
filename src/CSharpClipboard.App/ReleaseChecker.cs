using System.Net.Http.Json;
using System.Text.Json;
using CSharpClipboard.Core;

namespace CSharpClipboard.App;

public sealed class ReleaseChecker(HttpClient client, Version current) : IAppUpdater
{
    public const string ReleasePage = "https://github.com/DimonSmart/CSharpClipboard/releases";
    public async ValueTask<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/DimonSmart/CSharpClipboard/releases/latest");
        request.Headers.UserAgent.ParseAdd("CSharpClipboard/1.0");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return new(false, null);
        var tag = release.GetProperty("tag_name").GetString();
        return Version.TryParse(tag?.TrimStart('v'), out var version) ? new(version > current, version.ToString()) : new(false, null);
    }
}
