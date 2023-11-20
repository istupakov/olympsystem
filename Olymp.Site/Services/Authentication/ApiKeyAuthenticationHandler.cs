using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Olymp.Site.Services.Authentication;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public string ApiKeyHeaderName { get; set; } = "Authorization";
    public IEnumerable<string> ApiKeys { get; set; } = [];
}

public class ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Options.ApiKeyHeaderName, out var headerValues)
            || headerValues is not [string token])
            return Task.FromResult(AuthenticateResult.Fail("No API key"));

        if (!Options.ApiKeys.Contains(token))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var principal = new ClaimsPrincipal(new ClaimsIdentity("ApiKey"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
