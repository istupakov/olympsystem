using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;

namespace Olymp.WebApp.Components.Staff;

public static class TestQuery
{
    public static IQueryable<TestRow> Rows(IQueryable<Test> query) => query.Select(item => new TestRow
    {
        Id = item.Id,
        Number = item.Number,
        IsActive = item.IsActive,
        IsOpen = item.IsOpen,
        Score = item.Score,
        Comment = item.Comment,
        InputSize = item.Input.Length,
        OutputSize = item.Output.Length
    });
    public static IQueryable<Test> Filter(OlympContext db, int problemId, bool? active, bool? open)
    {
        var query = db.Tests.AsNoTracking().Where(item => item.ProblemId == problemId);
        if (active is bool isActive) query = query.Where(item => item.IsActive == isActive);
        if (open is bool isOpen) query = query.Where(item => item.IsOpen == isOpen);
        return query;
    }

    public static string FileName(int number, bool output) =>
        (output ? "output" : "input") + number.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".txt";
}

public sealed class TestRow
{
    public int Id { get; init; }
    public int Number { get; init; }
    public bool IsActive { get; init; }
    public bool IsOpen { get; init; }
    public int? Score { get; init; }
    public string? Comment { get; init; }
    public int InputSize { get; init; }
    public int OutputSize { get; init; }
}
