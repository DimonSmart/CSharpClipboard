using System.Net;
using System.Text;

namespace CSharpClipboard.App.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task Single_instance_signals_existing_owner_without_second_lease()
    {
        var path = Path.Combine(Path.GetTempPath(), "CSharpClipboard.InstanceTest", Guid.NewGuid().ToString("N"));
        using var first = await SingleInstanceLease.AcquireAsync(path);
        Assert.NotNull(first);
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Activated += () => signal.TrySetResult();
        Assert.Null(await SingleInstanceLease.AcquireAsync(path));
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        first.Dispose(); Directory.Delete(path, true);
    }

    [Theory]
    [InlineData("v1.1.0", false, false, true)]
    [InlineData("v1.0.0", false, false, false)]
    [InlineData("v2.0.0", true, false, false)]
    [InlineData("v2.0.0", false, true, false)]
    public async Task Release_check_accepts_only_new_stable_release(string tag, bool draft, bool prerelease, bool expected)
    {
        using var client = new HttpClient(new Handler($"{{\"tag_name\":\"{tag}\",\"draft\":{draft.ToString().ToLowerInvariant()},\"prerelease\":{prerelease.ToString().ToLowerInvariant()}}}"));
        var result = await new ReleaseChecker(client, new Version(1, 0, 0)).CheckAsync(default);
        Assert.Equal(expected, result.UpdateAvailable);
    }
    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
