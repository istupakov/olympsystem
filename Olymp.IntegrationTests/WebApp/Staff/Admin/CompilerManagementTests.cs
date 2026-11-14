using System.Reflection;
using System.Security.Claims;

using AngleSharp.Html.Parser;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.IntegrationTests.WebApp.Shared;
using Olymp.Tests.Shared;
using Olymp.WebApp;
using Olymp.WebApp.Resources;
using Olymp.WebApp.Services.Checker;

using Xunit;

using JuryMessages = Olymp.WebApp.Components.Pages.Staff.Jury.Messages;
using JuryRunners = Olymp.WebApp.Components.Pages.Staff.Jury.Runners;
using JuryStanding = Olymp.WebApp.Components.Pages.Staff.Jury.Standing;
using JurySubmissions = Olymp.WebApp.Components.Pages.Staff.Jury.SubmissionList;

namespace Olymp.IntegrationTests.WebApp.Staff.Admin;

[Trait("Category", "Integration")]
public partial class CompilerManagementTests(WebAppFactory factory) : StaffComponentTests(factory)
{
    [Fact]
    public async Task CompilerAvailabilityRequiresPassedSelfTestsAndMatchingVersion()
    {
        var runner = new Olymp.IntegrationTests.Services.Checkers.CheckerTestData.TestRunner();
        var checker = await Olymp.IntegrationTests.Services.Checkers.CheckerTestData.CreateCheckerAsync(runner, "available-compiler");
        var compiler = TestEntities.CreateCompilator();
        compiler.Name = "available-compiler";
        compiler.Description = "Test compiler";
        compiler.Language = "C";
        compiler.SourceExtension = ".c";
        compiler.CommandLine = "compile";
        Assert.False(checker.CanCheck(compiler));
        Assert.True(await checker.RunSelfTests(TestContext.Current.CancellationToken));
        Assert.True(checker.CanCheck(compiler));
        compiler.Description = "Another version";
        Assert.False(checker.CanCheck(compiler));
        compiler.Description = "Test compiler";
        compiler.Name = "another-compiler";
        Assert.False(checker.CanCheck(compiler));
    }

}
