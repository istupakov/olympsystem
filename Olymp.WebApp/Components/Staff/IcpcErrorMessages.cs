using System.Globalization;

using Olymp.Icpc;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Staff;

public static class IcpcErrorMessages
{
    public static string Describe(IcpcException error)
    {
        string stage = error.Stage switch
        {
            IcpcStage.Regions => SiteResources.IcpcStageRegions,
            IcpcStage.Contests => SiteResources.IcpcStageContests,
            IcpcStage.Authentication => SiteResources.IcpcStageAuthentication,
            IcpcStage.Access => SiteResources.IcpcStageAccess,
            IcpcStage.Teams => SiteResources.IcpcStageTeams,
            IcpcStage.Members => SiteResources.IcpcStageMembers,
            _ => SiteResources.IcpcStageRoster
        };
        string reason = error.Failure switch
        {
            IcpcFailure.Cognito => SiteResources.IcpcErrorAuthentication,
            IcpcFailure.AdditionalAuthentication => SiteResources.IcpcErrorAdditionalAuthentication,
            IcpcFailure.AccessDenied => SiteResources.IcpcErrorContestAccess,
            IcpcFailure.Transport => SiteResources.IcpcErrorNetwork,
            IcpcFailure.Timeout => SiteResources.IcpcErrorTimeout,
            IcpcFailure.InvalidResponse or IcpcFailure.InvalidData => SiteResources.IcpcErrorResponse,
            IcpcFailure.ResponseLimit => SiteResources.IcpcErrorLimit,
            IcpcFailure.Http when error.HttpStatus == 403 && error.Stage is IcpcStage.Access or IcpcStage.Teams or IcpcStage.Members
                => SiteResources.IcpcErrorAccessDenied,
            IcpcFailure.Http when error.HttpStatus == 401 => SiteResources.IcpcErrorUnauthorized,
            IcpcFailure.Http when error.HttpStatus >= 500 => SiteResources.IcpcErrorServer,
            _ => SiteResources.IcpcErrorRequest
        };
        var details = new List<string>();
        if (error.HttpStatus is int status) details.Add($"HTTP {status.ToString(CultureInfo.InvariantCulture)}");
        if (error.Code is not null) details.Add($"{SiteResources.IcpcErrorCode}: {error.Code}");
        return $"{stage}: {reason}" + (details.Count == 0 ? "" : $" ({string.Join("; ", details)})");
    }
}
