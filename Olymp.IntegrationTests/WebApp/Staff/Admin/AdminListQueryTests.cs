using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Components.Submissions;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.AntiCheat;

using Xunit;

using JuryDetails = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionDetails;
using JuryProblems = Olymp.WebApp.Components.Pages.Staff.Jury.Problems;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;
using Organizations = Olymp.WebApp.Components.Pages.Staff.Admin.OrganizationList;
using Users = Olymp.WebApp.Components.Pages.Staff.Admin.UserList;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

[Trait("Category", "Integration")]
public partial class AdminListQueryTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task OrganizationSearchIncludesFullNameAndPrefixWithoutPagination()
    {
        string prefix = Guid.NewGuid().ToString("N");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        db.Organizations.AddRange(Enumerable.Range(0, 21).Select(index => new Organization
        {
            Name = $"Organization {index:00}",
            FullName = index % 2 == 0 ? prefix : null,
            ACMName = index % 2 != 0 ? prefix : null,
            Users = []
        }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var first = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Organizations>($"/admin/organizations?Search={prefix}", []));
        Assert.Null(first.QuerySelector(".pagination"));
        Assert.Contains($"{SiteResources.StaffFound}: 21", first.Body!.TextContent);
        var organizations = await new Organizations { Search = prefix }.Query(db).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(21, organizations.Count);
        Assert.All(organizations, item => Assert.True(item.FullName == prefix || item.ACMName == prefix));
    }

    [Fact]
    public async Task UserTypeAndRoleFiltersCombineAndShowAllRoles()
    {
        var jury = await _factory.AddUserAsync();
        var ordinary = await _factory.AddUserAsync();
        string name = Guid.NewGuid().ToString("N");
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        await db.Users.Where(user => user.Id == jury.Id || user.Id == ordinary.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.Name, name), TestContext.Current.CancellationToken);
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        if (!await roles.RoleExistsAsync(RoleNames.Jury)) Assert.True((await roles.CreateAsync(new(RoleNames.Jury))).Succeeded);
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await manager.FindByIdAsync(jury.Id.ToString()))!;
        Assert.True((await manager.AddToRoleAsync(user, RoleNames.Jury)).Succeeded);
        Assert.True((await manager.AddClaimAsync(user, new Claim(ClaimNames.CoachOrg, "1"))).Succeeded);
        using var document = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Users>(
            $"/admin/users?Search={name}&AccountType=permanent&Role=jury", []));
        var filtered = await new Users { Search = name, AccountType = "permanent", Role = "jury" }.Query(db).ToListAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(filtered);
        Assert.Equal(jury.Id, row.Id);
        Assert.True(row.Jury);
        Assert.True(row.Coach);
        Assert.False(row.Admin);
        Assert.Null(document.QuerySelector(".pagination"));
        using var participants = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Users>(
            $"/admin/users?Search={name}&AccountType=participant&Role=jury", []));
        Assert.Empty(await new Users { Search = name, AccountType = "participant", Role = "jury" }.Query(db).ToListAsync(TestContext.Current.CancellationToken));
    }

}
