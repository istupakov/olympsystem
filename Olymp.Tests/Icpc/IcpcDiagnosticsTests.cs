using System.Globalization;
using System.Net;
using System.Text;

using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;

using Olymp.Icpc;
using Olymp.WebApp.Components.Staff;

using Xunit;

namespace Olymp.Tests.Icpc;

public class IcpcDiagnosticsTests
{
    private const string Sensitive = "private-password-token-email-response";

    [Theory]
    [InlineData("false", IcpcFailure.AccessDenied)]
    [InlineData("null", IcpcFailure.InvalidResponse)]
    [InlineData("\"false\"", IcpcFailure.InvalidResponse)]
    [InlineData("{}", IcpcFailure.InvalidResponse)]
    public async Task RejectsDeniedOrMalformedAccessBeforeRequestingTeams(string body, IcpcFailure failure)
    {
        using var handler = new FailingHandler(false, 200, body, access: true);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<IcpcException>(() => new IcpcClient(http)
            .LoadRegistrationAsync(9154, "private-token", TestContext.Current.CancellationToken));
        Assert.Equal(IcpcStage.Access, error.Stage);
        Assert.Equal(failure, error.Failure);
        Assert.Equal(200, error.HttpStatus);
        Assert.Equal(1, handler.Requests);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 403)]
    [InlineData(false, 401)]
    [InlineData(false, 500)]
    [InlineData(false, 302)]
    public async Task HttpFailuresRetainStageAndStatusWithoutUpstreamBody(bool members, int status)
    {
        using var handler = new FailingHandler(members, status, Sensitive);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<IcpcException>(() => new IcpcClient(http)
            .LoadRegistrationAsync(9154, "private-token", TestContext.Current.CancellationToken));
        Assert.Equal(members ? IcpcStage.Members : IcpcStage.Teams, error.Stage);
        Assert.Equal(IcpcFailure.Http, error.Failure);
        Assert.Equal(status, error.HttpStatus);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidJsonIsNotReportedAsMissingPermission(bool members)
    {
        using var handler = new FailingHandler(members, 200, "<html>" + Sensitive + "</html>");
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<IcpcException>(() => new IcpcClient(http)
            .LoadRegistrationAsync(9154, "private-token", TestContext.Current.CancellationToken));
        Assert.Equal(members ? IcpcStage.Members : IcpcStage.Teams, error.Stage);
        Assert.Equal(IcpcFailure.InvalidResponse, error.Failure);
        Assert.Equal(200, error.HttpStatus);
        Assert.Null(error.Code);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("<html>private-password-token-email-response</html>", "text/html")]
    [InlineData("", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("{\"private-password-token-email-response\":[]}", "application/json")]
    [InlineData("[{\"id\":1,\"eligibilityStatus\":true}]", "application/json")]
    [InlineData("[{\"id\":\"private-password-token-email-response\"}]", "application/json")]
    public async Task UnexpectedResponsesUseSameErrorWithoutExposingValues(string body, string mediaType)
    {
        using var handler = new FailingHandler(false, 200, body, mediaType: mediaType);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<IcpcException>(() => new IcpcClient(http)
            .LoadRegistrationAsync(9154, "private-token", TestContext.Current.CancellationToken));
        Assert.Equal(IcpcFailure.InvalidResponse, error.Failure);
        Assert.Equal(IcpcStage.Teams, error.Stage);
        Assert.Equal(200, error.HttpStatus);
        Assert.Null(error.Code);
        AssertSafe(error);
    }

    [Fact]
    public async Task AcceptsJsonEvenWhenContentTypeIsHtml()
    {
        using var handler = new FailingHandler(false, 200, "[]", mediaType: "text/html");
        using var http = new HttpClient(handler);
        Assert.Empty(await new IcpcClient(http).LoadRegistrationAsync(9154, "private-token", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportAndTimeoutErrorsRetainStageWithoutOriginalException(bool timeout)
    {
        using var handler = new FailingHandler(true, 200, "[]", timeout ? new TaskCanceledException(Sensitive) : new HttpRequestException(Sensitive));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<IcpcException>(() => new IcpcClient(http)
            .LoadRegistrationAsync(9154, "private-token", TestContext.Current.CancellationToken));
        Assert.Equal(IcpcStage.Members, error.Stage);
        Assert.Equal(timeout ? IcpcFailure.Timeout : IcpcFailure.Transport, error.Failure);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("NotAuthorizedException", "NotAuthorizedException")]
    [InlineData("UserNotConfirmedException", "UserNotConfirmedException")]
    [InlineData(Sensitive, null)]
    public async Task CognitoErrorsRetainOnlyKnownCodes(string upstreamCode, string? expectedCode)
    {
        var upstream = new AmazonCognitoIdentityProviderException(Sensitive, null, ErrorType.Sender,
            upstreamCode, Sensitive, HttpStatusCode.BadRequest);
        using var provider = new FakeCognito(upstream);
        var token = TestContext.Current.CancellationToken;
        var error = await Assert.ThrowsAsync<IcpcException>(() => IcpcClient.AuthenticateAsync(provider, Sensitive, Sensitive, token, token));
        Assert.Equal(IcpcStage.Authentication, error.Stage);
        Assert.Equal(IcpcFailure.Cognito, error.Failure);
        Assert.Equal(400, error.HttpStatus);
        Assert.Equal(expectedCode, error.Code);
        AssertSafe(error);
    }

    [Fact]
    public async Task AdditionalAuthenticationRetainsChallengeWithoutSession()
    {
        using var provider = new FakeCognito();
        var token = TestContext.Current.CancellationToken;
        var error = await Assert.ThrowsAsync<IcpcException>(() => IcpcClient.AuthenticateAsync(provider, Sensitive, Sensitive, token, token));
        Assert.Equal(IcpcFailure.AdditionalAuthentication, error.Failure);
        Assert.Equal("SMS_MFA", error.Code);
        AssertSafe(error);
    }

    [Fact]
    public async Task CallerCancellationIsNotReplacedWithAuthenticationFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var provider = new FakeCognito(new OperationCanceledException(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IcpcClient.AuthenticateAsync(provider,
            Sensitive, Sensitive, cancellation.Token, cancellation.Token));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void LocalizedMessagesDistinguishAccessAuthenticationAndParsing(string culture)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            string forbidden = IcpcErrorMessages.Describe(new(IcpcStage.Teams, IcpcFailure.Http, 403));
            string authentication = IcpcErrorMessages.Describe(new(IcpcStage.Authentication, IcpcFailure.Cognito, 400, "NotAuthorizedException"));
            string parsing = IcpcErrorMessages.Describe(new(IcpcStage.Members, IcpcFailure.InvalidResponse, 200));
            string access = IcpcErrorMessages.Describe(new(IcpcStage.Access, IcpcFailure.AccessDenied, 200));
            Assert.Contains(culture == "ru" ? "нет доступа" : "does not have access", access);
            Assert.Contains(culture == "ru" ? "Проверка доступа" : "Checking ICPC contest access", access);
            Assert.Contains(culture == "ru" ? "запретил доступ" : "denied access", forbidden);
            Assert.Contains("HTTP 403", forbidden);
            Assert.Contains("NotAuthorizedException", authentication);
            Assert.Contains(culture == "ru" ? "Вход в ICPC" : "Signing in to ICPC", authentication);
            Assert.Contains(culture == "ru" ? "не смог обработать" : "could not process", parsing);
            Assert.Contains("HTTP 200", parsing);
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    private static void AssertSafe(IcpcException error)
    {
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Sensitive, error.ToString());
        Assert.DoesNotContain("private-token", error.ToString());
        Assert.DoesNotContain(Sensitive, IcpcErrorMessages.Describe(error));
    }

    private sealed class FailingHandler(bool members, int status, string body, Exception? exception = null,
        string mediaType = "application/json", bool access = false) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (!access && request.RequestUri!.AbsolutePath.StartsWith("/api/contest/access/contest/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("true", Encoding.UTF8, "application/json")
                });
            bool fail = !members || request.RequestUri!.AbsolutePath.EndsWith("/teammember", StringComparison.Ordinal);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            if (fail && exception is not null) return Task.FromException<HttpResponseMessage>(exception);
            return Task.FromResult(new HttpResponseMessage(fail ? (HttpStatusCode)status : HttpStatusCode.OK)
            {
                Content = new StringContent(fail ? body : "[]", Encoding.UTF8, fail ? mediaType : "application/json")
            });
        }
    }

    private sealed class FakeCognito(Exception? exception = null)
        : AmazonCognitoIdentityProviderClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public override Task<InitiateAuthResponse> InitiateAuthAsync(InitiateAuthRequest request, CancellationToken cancellationToken = default)
        {
            if (exception is not null) return Task.FromException<InitiateAuthResponse>(exception);
            return Task.FromResult(new InitiateAuthResponse
            {
                ChallengeName = ChallengeNameType.PASSWORD_VERIFIER,
                ChallengeParameters = new()
                {
                    ["USER_ID_FOR_SRP"] = "test-user",
                    ["USERNAME"] = "test-user",
                    ["SALT"] = "1",
                    ["SRP_B"] = "2",
                    ["SECRET_BLOCK"] = "AQ=="
                },
                Session = Sensitive
            });
        }

        public override Task<RespondToAuthChallengeResponse> RespondToAuthChallengeAsync(RespondToAuthChallengeRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new RespondToAuthChallengeResponse
            {
                ChallengeName = ChallengeNameType.SMS_MFA,
                Session = Sensitive,
                ChallengeParameters = [],
                ResponseMetadata = new()
            });
    }
}
