using System.Net;
using System.Text;
using System.Text.Json;
using Cortex.Services;
using Xunit;

namespace Cortex.Tests;

public class AppUpdateServiceTests
{
    [Fact]
    public async Task LatestRelease_UsesUpdateServerAndPreservesVersionAndDownloadLink()
    {
        using var handler = new StubHandler(HttpStatusCode.OK,
            """{"version":" v0.2.0+build123 ","github_url":"https://github.com/joesbox/Cortex/releases/tag/v0.2.0"}""");
        using var client = new HttpClient(handler);

        var release = await new AppUpdateService(client).GetLatestReleaseAsync(CancellationToken.None);

        Assert.Equal("https://updates.mannelectronics.uk/cortex/latest", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("v0.2.0", release?.Version);
        Assert.Equal("https://github.com/joesbox/Cortex/releases/tag/v0.2.0", release?.GitHubUrl);
        Assert.True(AppUpdateService.IsVersionNewerThanCurrent(release?.Version, "0.1.8"));
    }

    [Fact]
    public async Task LatestRelease_UsesReleasesPageWhenDownloadLinkIsMissing()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, """{"version":"v0.2.0"}""");
        using var client = new HttpClient(handler);

        var release = await new AppUpdateService(client).GetLatestReleaseAsync(CancellationToken.None);

        Assert.Equal(AppUpdateService.CortexGitHubReleasesUrl, release?.GitHubUrl);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"version":" "}""")]
    public async Task LatestRelease_InvalidMetadataIsAnExplicitFailure(string json)
    {
        using var handler = new StubHandler(HttpStatusCode.OK, json);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<JsonException>(() =>
            new AppUpdateService(client).GetLatestReleaseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LatestRelease_ServerFailureDoesNotFallBackToGitHub()
    {
        using var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, """{"error":"unavailable"}""");
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new AppUpdateService(client).GetLatestReleaseAsync(CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task LatestRelease_HonorsCancellation()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, """{"version":"v0.2.0"}""");
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AppUpdateService(client).GetLatestReleaseAsync(cancellation.Token));
    }

    private sealed class StubHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
