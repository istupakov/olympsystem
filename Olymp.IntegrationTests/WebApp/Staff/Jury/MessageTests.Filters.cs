using System.Reflection;

using AngleSharp.Html.Parser;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Tests.Shared;
using Olymp.WebApp.Components.Pages.Staff.Jury;
using Olymp.WebApp.Resources;

using Xunit;

namespace Olymp.IntegrationTests.WebApp.Staff.Jury;

public partial class MessageTests
{
    [Fact]
    public async Task QuestionStatusDistinguishesAllUnansweredAndAnsweredAndCanReturnToAll()
    {
        var token = TestContext.Current.CancellationToken;
        var author = await _factory.AddUserAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OlympContext>();
        var contest = TestEntities.CreateContest("Question filters", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var messages = new string?[] { null, "", " \r\n\t ", "A reply" }.Select(reply => new Message
        {
            Contest = contest,
            UserId = author.Id,
            UserText = "Question",
            JuryText = reply,
            SendTime = DateTimeOffset.UtcNow
        }).ToArray();
        db.Messages.AddRange(messages);
        await db.SaveChangesAsync(token);
        string path = $"/jury/contests/{contest.Id}/messages";
        foreach (var (status, count) in new[] { ("", 4), ("unanswered", 3), ("answered", 1) })
        {
            string url = path + (status.Length == 0 ? "" : "?Status=" + status);
            using var html = await new HtmlParser().ParseDocumentAsync(await RenderAsync<Messages>(url, new() { ["ContestId"] = contest.Id }, Staff(author.Id)));
            Assert.Equal(count, html.QuerySelectorAll("article[id^='message-']").Length);
            var select = html.QuerySelector("#message-status")!;
            Assert.Equal(status, select.QuerySelector("option[selected]")?.GetAttribute("value") ?? "");
            if (status == "unanswered") Assert.DoesNotContain("A reply", html.Body!.TextContent);
        }
        await RenderAsync<Messages>(path + "?Status=unanswered", new() { ["ContestId"] = contest.Id }, Staff(author.Id), async page =>
        {
            var filters = typeof(Messages).GetField("_filters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            filters.GetType().GetProperty("Status")!.SetValue(filters, "");
            await ActAsync(page, "ApplyFilters");
            var navigation = (Microsoft.AspNetCore.Components.NavigationManager)typeof(Messages).GetProperty("Navigation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            Assert.DoesNotContain("Status=", navigation.Uri);
        });
    }
}
