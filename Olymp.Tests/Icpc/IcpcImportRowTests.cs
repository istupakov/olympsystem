using Olymp.Icpc;
using Olymp.WebApp.Services.Contests;

using Xunit;

namespace Olymp.Tests.Icpc;

public class IcpcImportRowTests
{
    [Theory]
    [InlineData("ACCEPTED", "NO_ISSUES_FOUND", null, true)]
    [InlineData("PENDING", "NO_ISSUES_FOUND", null, false)]
    [InlineData("CANCELED", "NO_ISSUES_FOUND", null, false)]
    [InlineData("ACCEPTED", "PREDICTED_INELIGIBLE", null, false)]
    [InlineData("ACCEPTED", "NO_ISSUES_FOUND", "Incomplete registration", false)]
    public void SelectionWithoutRemarksRequiresIcpcApprovalAndNoEligibilityIssues(
        string status, string eligibility, string? issue, bool expected)
    {
        var row = Row(new() { Status = status, EligibilityStatus = eligibility, EligibilityIssue = issue });
        Assert.Equal(expected, row.HasNoRemarks);
    }

    [Fact]
    public void ResolvingOrganizationAndCoachUpdatesSelectionEligibility()
    {
        var row = Row(new() { Status = "ACCEPTED" });
        row.OrganizationId = null;
        Assert.True(row.NeedsOrganization);
        Assert.False(row.HasNoRemarks);
        row.OrganizationId = 10;
        Assert.True(row.HasNoRemarks);
        row.CoachId = null;
        Assert.True(row.NeedsCoach);
        Assert.False(row.HasNoRemarks);
        row.CoachId = 1;
        row.OrganizationId = 20;
        Assert.True(row.CoachNeedsAccess);
        Assert.False(row.HasNoRemarks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ErrorsAndWarningsExcludeTeamFromSelectionWithoutRemarks(bool error)
    {
        var row = Row(new() { Status = "ACCEPTED" }, error ? ["Error"] : [], error ? [] : ["Warning"]);
        Assert.False(row.HasNoRemarks);
    }

    private static IcpcImportRow Row(IcpcTeam source, string[]? errors = null, string[]? warnings = null) => new()
    {
        Source = source,
        Snapshot = "",
        Members = [new("Member", "member@example.test", 2)],
        Coaches = [new("Coach", "coach@example.test", 1)],
        CoachId = 1,
        OrganizationId = 10,
        CoachOrganizations = new Dictionary<int, int[]> { [1] = [10] },
        Errors = errors ?? [],
        Warnings = warnings ?? []
    };
}
