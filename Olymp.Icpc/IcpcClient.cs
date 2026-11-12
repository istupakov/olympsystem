using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.Extensions.CognitoAuthentication;
using Amazon.Runtime;

namespace Olymp.Icpc;

/// <summary>Loads contest registration data without retaining credentials or tokens.</summary>
public sealed class IcpcClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public async Task<IReadOnlyList<IcpcRegion>> LoadRegionsAsync(int season, CancellationToken token = default)
    {
        if (season is < 1 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(season));
        var regions = await LoadPublicAsync<List<IcpcRegion>>(
            string.Create(CultureInfo.InvariantCulture, $"regionals/{season}"), IcpcStage.Regions, token);
        if (regions.Any(region => region is null || region.Id <= 0 || string.IsNullOrWhiteSpace(region.Label))
            || regions.Select(region => region.Id).Distinct().Count() != regions.Count)
            throw new IcpcException(IcpcStage.Regions, IcpcFailure.InvalidData, 200);
        return regions;
    }

    public async Task<IReadOnlyList<IcpcContest>> LoadContestsAsync(long regionId, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regionId);
        var contests = await LoadPublicAsync<List<IcpcContest>>(
            string.Create(CultureInfo.InvariantCulture, $"contests-under/{regionId}"), IcpcStage.Contests, token);
        if (contests.Any(contest => contest is null || contest.Id <= 0 || string.IsNullOrWhiteSpace(contest.Name))
            || contests.Select(contest => contest.Id).Distinct().Count() != contests.Count)
            throw new IcpcException(IcpcStage.Contests, IcpcFailure.InvalidData, 200);
        return contests;
    }

    private Task<T> LoadPublicAsync<T>(string path, IcpcStage stage, CancellationToken token)
        => SendJsonAsync<T>($"https://icpc.global/api/contest/public/{path}", stage, token, token);

    public async Task<IReadOnlyList<IcpcTeam>> LoadTeamsAsync(long contestId, string email, string password,
        CancellationToken token = default)
    {
        if (contestId <= 0 || string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new ArgumentException("Contest ID and credentials are required.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var provider = new AmazonCognitoIdentityProviderClient(new AnonymousAWSCredentials(), new AmazonCognitoIdentityProviderConfig
        {
            RegionEndpoint = RegionEndpoint.USEast1,
            Timeout = TimeSpan.FromSeconds(30)
        });
        string idToken = await AuthenticateAsync(provider, email, password, timeout.Token, token);
        return await LoadRegistrationAsync(contestId, idToken, timeout.Token, token);
    }

    internal static async Task<string> AuthenticateAsync(IAmazonCognitoIdentityProvider provider, string email, string password,
        CancellationToken requestToken, CancellationToken callerToken)
    {
        try
        {
            var pool = new CognitoUserPool("us-east-1_WaDOo4Gqm", "6q2fe6opm0m24eoebqf9vj4emd", provider);
            var user = new CognitoUser(email.Trim(), pool.ClientID, pool, provider);
            var auth = await user.StartWithSrpAuthAsync(new InitiateSrpAuthRequest
            {
                Password = password,
                IsCustomAuthFlow = true
            }, requestToken);
            if (string.IsNullOrEmpty(auth.AuthenticationResult?.IdToken))
                throw new IcpcException(IcpcStage.Authentication, IcpcFailure.AdditionalAuthentication, code: auth.ChallengeName?.Value);
            return auth.AuthenticationResult.IdToken;
        }
        catch (AmazonServiceException exception)
        {
            throw new IcpcException(IcpcStage.Authentication, IcpcFailure.Cognito, (int)exception.StatusCode, exception.ErrorCode);
        }
        catch (AmazonClientException)
        {
            throw new IcpcException(IcpcStage.Authentication, IcpcFailure.Transport);
        }
        catch (HttpRequestException exception)
        {
            throw new IcpcException(IcpcStage.Authentication, IcpcFailure.Transport, (int?)exception.StatusCode);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new IcpcException(IcpcStage.Authentication, IcpcFailure.Timeout);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // The SDK may reject an unexpected challenge before returning an authentication result.
            throw new IcpcException(IcpcStage.Authentication, IcpcFailure.InvalidResponse);
        }
    }

    internal Task<IReadOnlyList<IcpcTeam>> LoadRegistrationAsync(long contestId, string idToken, CancellationToken token)
        => LoadRegistrationAsync(contestId, idToken, token, token);

    private async Task<IReadOnlyList<IcpcTeam>> LoadRegistrationAsync(long contestId, string idToken,
        CancellationToken requestToken, CancellationToken callerToken)
    {
        bool hasAccess = await SendJsonAsync<bool>(string.Create(CultureInfo.InvariantCulture,
            $"https://icpc.global/api/contest/access/contest/{contestId}"), IcpcStage.Access, requestToken, callerToken, idToken);
        if (!hasAccess)
            throw new IcpcException(IcpcStage.Access, IcpcFailure.AccessDenied, 200);
        var teams = await LoadAsync<IcpcTeam>(contestId, "team", "id,name,status,instName,eligibilityStatus,eligibilityIssue,teamMembers",
            idToken, IcpcStage.Teams, requestToken, callerToken);
        var members = await LoadAsync<IcpcMember>(contestId, "teammember", "teamId,role,username,firstName,lastName,teamName",
            idToken, IcpcStage.Members, requestToken, callerToken);
        if (teams.Any(team => team is null) || members.Any(member => member is null))
            throw new IcpcException(IcpcStage.Roster, IcpcFailure.InvalidData);
        var ids = teams.Select(team => team.Id).ToHashSet();
        if (ids.Any(id => id <= 0) || ids.Count != teams.Count || members.Any(member => !ids.Contains(member.TeamId)))
            throw new IcpcException(IcpcStage.Roster, IcpcFailure.InvalidData);
        var byTeam = members.ToLookup(member => member.TeamId);
        return [.. teams.Select(team => team with { Members = [.. byTeam[team.Id]] })];
    }

    private async Task<List<T>> LoadAsync<T>(long contestId, string part, string fields, string idToken,
        IcpcStage stage, CancellationToken requestToken, CancellationToken callerToken)
    {
        var result = new List<T>();
        const int size = 1000;
        // Use actual pagination instead of assuming a single oversized request returns every row.
        for (int page = 1; page <= 1000; page++)
        {
            string query = Uri.EscapeDataString($"proj:{fields};");
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://icpc.global/api/contest/search/contest/{contestId}/{part}?q={query}&page={page}&size={size}");
            var items = await SendJsonAsync<List<T>>(url, stage, requestToken, callerToken, idToken);
            result.AddRange(items);
            if (items.Count < size)
                return result;
        }
        throw new IcpcException(stage, IcpcFailure.ResponseLimit);
    }

    private async Task<T> SendJsonAsync<T>(string url, IcpcStage stage, CancellationToken requestToken,
        CancellationToken callerToken, string? idToken = null)
    {
        int? status = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // ICPC serves its HTML application instead of API JSON when User-Agent is absent.
            request.Headers.UserAgent.ParseAdd("Olymp.Icpc/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (idToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            using var response = await http.SendAsync(request, requestToken);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new IcpcException(stage, IcpcFailure.Http, status);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, requestToken)
                ?? throw new IcpcException(stage, IcpcFailure.InvalidResponse, status);
        }
        catch (HttpRequestException exception)
        {
            throw new IcpcException(stage, IcpcFailure.Transport, status ?? (int?)exception.StatusCode);
        }
        catch (IOException)
        {
            throw new IcpcException(stage, IcpcFailure.Transport, status);
        }
        catch (JsonException)
        {
            throw new IcpcException(stage, IcpcFailure.InvalidResponse, status);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new IcpcException(stage, IcpcFailure.Timeout, status);
        }
    }
}
