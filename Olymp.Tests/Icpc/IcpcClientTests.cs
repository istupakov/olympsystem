using System.Net;
using System.Text;

using Olymp.Icpc;

using Xunit;

namespace Olymp.Tests.Icpc;

public class IcpcClientTests
{
    [Fact]
    public async Task LoadsRosterAndKeepsTokenOutOfDefaultHeaders()
    {
        using var handler = new RegistrationHandler();
        using var http = new HttpClient(handler);
        var teams = await new IcpcClient(http).LoadRegistrationAsync(42, "test-token", TestContext.Current.CancellationToken);
        var team = Assert.Single(teams);
        Assert.Equal(17, team.Id);
        Assert.Equal("Team", team.Name);
        var member = Assert.Single(team.Members);
        Assert.Equal("Alex Smith", member.Name);
        Assert.True(member.IsContestant);
        Assert.True(member.IsCoach);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal(3, handler.Requests);
    }

    [Theory]
    [InlineData("[{\"id\":17},{\"id\":17}]")]
    [InlineData("[{\"id\":0}]")]
    [InlineData("[{\"id\":18}]")]
    [InlineData("[null]")]
    public async Task RejectsInvalidIdsAndOrphanMembers(string teams)
    {
        using var handler = new RegistrationHandler(teams);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<IcpcException>(() => new IcpcClient(http)
            .LoadRegistrationAsync(42, "test-token", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadsAdditionalPages()
    {
        string first = "[" + string.Join(",", Enumerable.Range(1, 1000).Select(id => $"{{\"id\":{id}}}")) + "]";
        using var handler = new RegistrationHandler(first, "[{\"id\":1001}]");
        using var http = new HttpClient(handler);
        var teams = await new IcpcClient(http).LoadRegistrationAsync(42, "test-token", TestContext.Current.CancellationToken);
        Assert.Equal(1001, teams.Count);
        Assert.Equal(4, handler.Requests);
    }

    private sealed class RegistrationHandler(string teams = "[{\"id\":\"17\",\"name\":\"Team\",\"status\":\"ACCEPTED\"}]",
        string? secondPage = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("icpc.global", request.RequestUri.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("Olymp.Icpc/1.0", request.Headers.UserAgent.ToString());
            Assert.Equal("test-token", request.Headers.Authorization.Parameter);
            if (request.RequestUri.AbsolutePath.StartsWith("/api/contest/access/contest/", StringComparison.Ordinal))
            {
                Assert.Equal("/api/contest/access/contest/42", request.RequestUri.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("true", Encoding.UTF8, "application/json")
                });
            }
            Assert.Contains("%3B", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(";", request.RequestUri.Query);
            string projection = Uri.UnescapeDataString(request.RequestUri.Query.Split('&')[0]);
            Assert.StartsWith("?q=proj:", projection);
            Assert.EndsWith(";", projection);
            if (request.RequestUri.AbsolutePath.EndsWith("/teammember", StringComparison.Ordinal))
                Assert.Contains("teamId", projection["?q=proj:".Length..^1].Split(','));
            string json = request.RequestUri.AbsolutePath.EndsWith("/teammember", StringComparison.Ordinal)
                ? "[{\"teamId\":17,\"role\":\"CONTESTANT_COACH\",\"firstName\":\"Alex\",\"lastName\":\"Smith\"}]"
                : request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal) ? secondPage! : teams;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
