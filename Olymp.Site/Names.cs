namespace Olymp.Site;

public static class PolicyNames
{
    public const string Runner = nameof(Runner);
    public const string OlympUser = nameof(OlympUser);
    public const string Admin = nameof(Admin);
    public const string JuryOrAdmin = nameof(JuryOrAdmin);
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
}

public static class OperationNames
{
    public const string SubmitSolution = nameof(SubmitSolution);
    public const string ViewSolution = nameof(ViewSolution);
    public const string SendMessage = nameof(SendMessage);
}
