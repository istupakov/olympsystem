using Microsoft.AspNetCore.WebUtilities;

using Olymp.WebApp.Components.Staff;

using Xunit;

namespace Olymp.Tests;

public class StaffGridStateTests
{
    [Theory]
    [InlineData("https://localhost:5001/admin/users?Search=test&sort=Name", "/admin/users?Search=test&sort=Name&row=201")]
    [InlineData("https://localhost:5001/admin/users?sort=Name&Search=John+Doe", "/admin/users?Search=John%20Doe&sort=Name&row=201")]
    public void CardReturnContextRestoresSelectionWhenBrowserGoesBack(string source, string returnTo)
    {
        var state = new StaffGridState();
        state.RememberRows(source, [42, 75], 180);
        state.RememberReturnTo("https://localhost:5001/admin/users/75/edit", returnTo);
        Assert.Equal(201, state.GetIndex(source));
    }

    [Fact]
    public void ExplicitReturnRowOverridesTheLastFetchedSlice()
    {
        var state = new StaffGridState();
        const string uri = "https://localhost:5001/admin/users?row=201";
        state.RememberRows(uri, [42, 75], 180);
        Assert.Equal(201, state.GetIndex(uri));
    }

    [Fact]
    public void ReturnLinkPreservesFiltersSortAndSelectedRowWithoutDuplicatingRowParameter()
    {
        var state = new StaffGridState();
        const string uri = "https://localhost:5001/admin/users?Search=test&sort=Name&direction=desc&row=10";
        state.RememberRows(uri, [42, 75], 200);
        string detail = state.DetailUrl(uri, "admin/users/75/edit", 75);
        string returnTo = QueryHelpers.ParseQuery(new Uri("https://localhost:5001/" + detail).Query)["returnTo"].ToString();
        var query = QueryHelpers.ParseQuery(new Uri("https://localhost:5001" + returnTo).Query);
        Assert.Equal("test", query["Search"].ToString());
        Assert.Equal("Name", query["sort"].ToString());
        Assert.Equal("desc", query["direction"].ToString());
        Assert.Equal("201", query["row"].ToString());
        Assert.Equal(201, new StaffGridState().GetIndex("https://localhost:5001" + returnTo));
    }

    [Theory]
    [InlineData("https://other.example/admin/users")]
    [InlineData("//other.example/admin/users")]
    [InlineData("/admin/\\other.example")]
    [InlineData(null)]
    public void InvalidReturnLinksUseLocalFallback(string? returnTo) => Assert.Equal("admin/users", StaffGridState.BackUrl(returnTo, "admin/users"));
}
