namespace Olymp.WebApp;

public static class PolicyNames
{
    public const string Runner = nameof(Runner);
    public const string PermanentAccount = nameof(PermanentAccount);
    public const string OlympUser = nameof(OlympUser);
    public const string Admin = nameof(Admin);
    public const string JuryOrAdmin = nameof(JuryOrAdmin);
    public const string ManageContest = nameof(ManageContest);
    public const string CancelRegistration = nameof(CancelRegistration);
    public const string Coach = nameof(Coach);
}

public static class RoleNames
{
    public const string Admin = nameof(Admin);
    public const string Jury = nameof(Jury);
}

public static class ClaimNames
{
    public const string CoachOrg = "coach-org";
    public const string CompetitorContest = "competitor-contest";
    public const string ContestManage = "contest-manage";
    public const string IcpcTeamId = "icpc-team-id";
}
