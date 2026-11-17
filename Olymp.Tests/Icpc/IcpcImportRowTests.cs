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
        Assert.True(row.NeedsOrganization);
        Assert.False(row.HasNoRemarks);
    }

    [Fact]
    public void CoachChangesPreserveOnlyAccessibleOrganizationsAndDefaultOnlySingleOption()
    {
        var row = Row(new() { Status = "ACCEPTED" });
        row.CoachId = 3;
        Assert.Equal(10, row.OrganizationId);
        row.OrganizationId = 20;
        row.CoachId = 1;
        Assert.Equal(10, row.OrganizationId);
        row.CoachId = null;
        Assert.Null(row.OrganizationId);
        Assert.True(row.NeedsCoach);
        row.CoachId = 3;
        Assert.Null(row.OrganizationId);
        Assert.True(row.NeedsOrganization);
        row.OrganizationId = 20;
        Assert.True(row.HasNoRemarks);
        row.CoachId = 999;
        Assert.True(row.NeedsCoach);
        Assert.Empty(row.Organizations);
        Assert.Null(row.OrganizationId);
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
        Coaches = [new(1, "Coach", "coach@example.test", [new(10, "First")]), new(3, "Other", "other@example.test", [new(10, "First"), new(20, "Second")])],
        CoachId = 1,
        OrganizationId = 10,
        Errors = errors ?? [],
        Warnings = warnings ?? []
    };
}
