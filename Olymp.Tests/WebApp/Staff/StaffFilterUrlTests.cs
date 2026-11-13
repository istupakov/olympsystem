using Microsoft.AspNetCore.WebUtilities;

using Olymp.WebApp.Components.Staff;

using Xunit;

namespace Olymp.Tests.WebApp.Staff;

public class StaffFilterUrlTests
{
    [Theory]
    [InlineData("/jury/submissions?Author=name&sort=Time&direction=desc&row=400&Page=3")]
    [InlineData("jury/submissions?Author=name&sort=Time&direction=desc&row=400&Page=3")]
    [InlineData("https://localhost/jury/submissions?Author=name&sort=Time&direction=desc&row=400&Page=3")]
    public void ExactFilterRetainsSortAndDropsFuzzySearchAndPosition(string source)
    {
        string result = StaffFilterUrl.Set(source, "AuthorId", 42, "Author");
        var address = new Uri(new Uri("https://localhost/"), result);
        Assert.Equal("localhost", address.Host);
        Assert.Equal("/jury/submissions", address.AbsolutePath);
        var query = QueryHelpers.ParseQuery(address.Query);
        Assert.Equal("42", query["AuthorId"].ToString());
        Assert.Equal("Time", query["sort"].ToString());
        Assert.Equal("desc", query["direction"].ToString());
        Assert.False(query.ContainsKey("Author"));
        Assert.False(query.ContainsKey("row"));
        Assert.False(query.ContainsKey("Page"));
    }

    [Fact]
    public void RemovingOneFilterKeepsOtherFiltersAndFragment()
    {
        string result = StaffFilterUrl.Set("/jury/submissions?AuthorId=42&Compiler=12&Language=C%2B%2B&row=50#results", "Compiler", null);
        var address = new Uri(new Uri("https://localhost/"), result);
        var query = QueryHelpers.ParseQuery(address.Query);
        Assert.False(query.ContainsKey("Compiler"));
        Assert.Equal("42", query["AuthorId"].ToString());
        Assert.Equal("C++", query["Language"].ToString());
        Assert.Equal("#results", address.Fragment);
    }

    [Theory]
    [InlineData(-1007, true)]
    [InlineData(-2003, true)]
    [InlineData(-6999, true)]
    [InlineData(-1, false)]
    [InlineData(-10, false)]
    [InlineData(-2000, false)]
    [InlineData(2, false)]
    public void OnlyTestSpecificVerdictsCanLinkToTests(int code, bool hasTest) => Assert.Equal(hasTest, StaffVerdicts.HasTest(code));
}
