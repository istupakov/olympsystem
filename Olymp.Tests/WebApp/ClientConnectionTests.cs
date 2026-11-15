using System.Net;

using Microsoft.AspNetCore.Http;

using Olymp.WebApp.Services;

using Xunit;

namespace Olymp.Tests.WebApp;

public class ClientConnectionTests
{
    [Theory]
    [InlineData("203.0.113.42", "2001:db8::42")]
    [InlineData("2001:db8::42", "203.0.113.42")]
    public async Task AddressSurvivesMissingHttpContextAndUpdatesOnReconnect(string first, string next)
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(first);
        http.HttpContext.Request.Headers["X-Forwarded-For"] = "198.51.100.250";
        var connection = new ClientConnection(http);
        await connection.OnConnectionUpAsync(null!, TestContext.Current.CancellationToken);

        http.HttpContext = null;
        Assert.Equal(first, connection.RemoteIpAddress);
        await connection.OnConnectionUpAsync(null!, TestContext.Current.CancellationToken);
        Assert.Equal(first, connection.RemoteIpAddress);

        http.HttpContext = new DefaultHttpContext();
        http.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(next);
        Assert.Equal(first, connection.RemoteIpAddress);
        await connection.OnConnectionUpAsync(null!, TestContext.Current.CancellationToken);
        Assert.Equal(next, connection.RemoteIpAddress);
    }

    [Fact]
    public async Task MissingAddressIsAllowedAndAnotherCircuitDoesNotInheritIt()
    {
        var http = new HttpContextAccessor();
        var connection = new ClientConnection(http);
        await connection.OnConnectionUpAsync(null!, TestContext.Current.CancellationToken);
        Assert.Null(connection.RemoteIpAddress);

        http.HttpContext = new DefaultHttpContext();
        http.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
        await connection.OnConnectionUpAsync(null!, TestContext.Current.CancellationToken);
        var other = new ClientConnection(http);
        http.HttpContext = null;
        await other.OnConnectionUpAsync(null!, TestContext.Current.CancellationToken);
        Assert.Null(other.RemoteIpAddress);
        Assert.Equal("127.0.0.1", connection.RemoteIpAddress);
    }
}
