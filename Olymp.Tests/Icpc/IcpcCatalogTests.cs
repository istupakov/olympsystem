using System.Net;
using System.Text;

using Olymp.Icpc;

using Xunit;

namespace Olymp.Tests.Icpc;

public class IcpcCatalogTests
{
    [Fact]
    public async Task LoadsRegionsAndFlattenedContestsWithoutAuthentication()
    {
        using var handler = new CatalogHandler(path => path switch
        {
            "/api/contest/public/regionals/2027" => "[{\"id\":\"9778\",\"label\":\"Northern Eurasia\"}]",
            "/api/contest/public/contests-under/9778" => "[{\"id\":9789,\"contest\":\"West Siberian Regional\",\"date\":\"2026-11-21\"},{\"id\":9807,\"contest\":\"Qualification\"}]",
            _ => throw new InvalidOperationException("Unexpected request path.")
        });
        using var http = new HttpClient(handler);
        var client = new IcpcClient(http);
        var region = Assert.Single(await client.LoadRegionsAsync(2027, TestContext.Current.CancellationToken));
        Assert.Equal(9778, region.Id);
        Assert.Equal("Northern Eurasia", region.Label);
        var contests = await client.LoadContestsAsync(region.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, contests.Count);
        Assert.Equal(9789, contests[0].Id);
        Assert.Equal("West Siberian Regional", contests[0].Name);
        Assert.Equal("2026-11-21", contests[0].Date);
        Assert.Null(contests[1].Date);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task EmptySeasonAndRegionAreValid()
    {
        using var handler = new CatalogHandler(_ => "[]");
        using var http = new HttpClient(handler);
        var client = new IcpcClient(http);
        Assert.Empty(await client.LoadRegionsAsync(2027, TestContext.Current.CancellationToken));
        Assert.Empty(await client.LoadContestsAsync(9778, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("{\"message\":\"not a list\"}")]
    [InlineData("[{\"id\":0,\"label\":\"Region\",\"contest\":\"Contest\"}]")]
    [InlineData("[{\"id\":1}]")]
    [InlineData("[{\"id\":1,\"label\":\"Region\",\"contest\":\"Contest\"},{\"id\":1,\"label\":\"Other\",\"contest\":\"Other\"}]")]
    public async Task RejectsMalformedCatalogResponses(string json)
    {
        using var handler = new CatalogHandler(_ => json);
        using var http = new HttpClient(handler);
        var client = new IcpcClient(http);
        await Assert.ThrowsAsync<IcpcException>(() => client.LoadRegionsAsync(2027, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IcpcException>(() => client.LoadContestsAsync(9778, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CatalogHttpErrorsDoNotExposeUpstreamResponse()
    {
        using var handler = new CatalogHandler(_ => "private-upstream-diagnostic", HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<IcpcException>(() => new IcpcClient(http)
            .LoadRegionsAsync(2027, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private-upstream-diagnostic", exception.ToString());
    }

    [Fact]
    public async Task InvalidSelectionDoesNotSendRequestsAndCancellationIsPreserved()
    {
        using var handler = new CatalogHandler(_ => "[]");
        using var http = new HttpClient(handler);
        var client = new IcpcClient(http);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.LoadRegionsAsync(0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.LoadRegionsAsync(10000, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.LoadContestsAsync(0, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Requests);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LoadRegionsAsync(2027, cancellation.Token));
    }

    private sealed class CatalogHandler(Func<string, string> response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("icpc.global", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("Olymp.Icpc/1.0", request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(response(request.RequestUri.AbsolutePath), Encoding.UTF8, "application/json")
            });
        }
    }
}
