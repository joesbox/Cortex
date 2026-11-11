using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Cortex.Services;
using Xunit;

namespace Cortex.Tests;

public sealed class ProvisioningApiClientTests
{
    [Fact]
    public async Task TryResume_PreservesSessionWhenKeycloakIsTemporarilyUnavailable()
    {
        using var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"access_token":"access","refresh_token":"refresh-2","expires_in":300}"""),
            (HttpStatusCode.ServiceUnavailable, """{"error":"temporarily_unavailable"}"""));
        using var httpClient = new HttpClient(handler);
        using var api = new ProvisioningApiClient(httpClient, new Uri("https://keycloak.example/auth/"));

        Assert.True(await api.TryResumeAsync("realm", "user", "refresh-1"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.TryResumeAsync("realm", "user", api.RefreshToken));

        Assert.True(api.IsSignedIn);
        Assert.Equal("refresh-2", api.RefreshToken);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task TryResume_ClearsSessionWhenKeycloakRejectsRefreshToken(HttpStatusCode status)
    {
        using var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"access_token":"access","refresh_token":"refresh-2","expires_in":300}"""),
            (status, """{"error":"invalid_grant"}"""));
        using var httpClient = new HttpClient(handler);
        using var api = new ProvisioningApiClient(httpClient, new Uri("https://keycloak.example/auth/"));

        Assert.True(await api.TryResumeAsync("realm", "user", "refresh-1"));

        Assert.False(await api.TryResumeAsync("realm", "user", api.RefreshToken));

        Assert.False(api.IsSignedIn);
        Assert.Empty(api.RefreshToken);
    }

    [Fact]
    public async Task AccessTokenRefresh_PreservesSessionWhenKeycloakIsTemporarilyUnavailable()
    {
        using var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"access_token":"access","refresh_token":"refresh-2","expires_in":300}"""),
            (HttpStatusCode.ServiceUnavailable, """{"error":"temporarily_unavailable"}"""));
        using var httpClient = new HttpClient(handler);
        using var api = new ProvisioningApiClient(httpClient, new Uri("https://keycloak.example/auth/"));

        Assert.True(await api.TryResumeAsync("realm", "user", "refresh-1"));

        FieldInfo expiry = typeof(ProvisioningApiClient).GetField("_accessTokenExpiresAt", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Unable to find _accessTokenExpiresAt.");
        expiry.SetValue(api, DateTimeOffset.MinValue);

        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetDeviceStatusAsync("device"));

        Assert.True(api.IsSignedIn);
        Assert.Equal("refresh-2", api.RefreshToken);
    }

    [Fact]
    public async Task TryResume_PreservesSessionWhenRequestIsCancelled()
    {
        using var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"access_token":"access","refresh_token":"refresh-2","expires_in":300}"""));
        using var httpClient = new HttpClient(handler);
        using var api = new ProvisioningApiClient(httpClient, new Uri("https://keycloak.example/auth/"));

        Assert.True(await api.TryResumeAsync("realm", "user", "refresh-1"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.TryResumeAsync("realm", "user", api.RefreshToken, cancellation.Token));

        Assert.True(api.IsSignedIn);
        Assert.Equal("refresh-2", api.RefreshToken);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _responseIndex;

        public int RequestCount => _responseIndex;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responseIndex >= responses.Length)
            {
                throw new InvalidOperationException("Unexpected HTTP request.");
            }

            var response = responses[_responseIndex++];
            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
