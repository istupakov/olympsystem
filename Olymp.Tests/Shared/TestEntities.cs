using Olymp.Domain.Models;

using DomainChecker = Olymp.Domain.Models.Checker;

namespace Olymp.Tests.Shared;

public static class TestEntities
{
    public static Contest CreateContest(string name, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        return new Contest
        {
            Name = name,
            Abbr = "acm-test",
            StartTime = startTime,
            EndTime = endTime,
            Competitors = [],
            Problems = [],
            Messages = []
        };
    }

    public static Competitor CreateCompetitor(Contest contest, string name)
    {
        return new Competitor
        {
            Name = name,
            IsApproved = true,
            ContestId = contest.Id,
            Contest = contest,
            Members = [],
            Messages = [],
            Submissions = []
        };
    }

    public static DomainChecker CreateChecker()
    {
        return new DomainChecker { Description = "Test checker", Problems = [] };
    }

    public static Problem CreateProblem(Contest contest, int number = 1, DomainChecker? checker = null)
    {
        return new Problem
        {
            Name = $"Problem {number}",
            Number = number,
            ContestId = contest.Id,
            Contest = contest,
            Checker = checker ?? CreateChecker(),
            IsActive = true,
            TimeLimit = 1,
            MemoryLimit = 128,
            Tests = [],
            Submissions = [],
            Messages = []
        };
    }

    public static Compilator CreateCompilator()
    {
        return new Compilator
        {
            Name = "Test compiler",
            Description = "Test compiler",
            Language = "C#",
            SourceExtension = ".cs",
            CommandLine = "unused",
            ConfigName = "test",
            IsActive = true,
            Submissions = []
        };
    }

    public static Submission CreateSubmission(User user, Problem problem, Compilator compilator,
        DateTimeOffset commitTime, byte[]? sourceCode = null)
    {
        return new Submission
        {
            UserId = user.Id,
            User = user,
            ProblemId = problem.Id,
            Problem = problem,
            CompilatorId = compilator.Id,
            Compilator = compilator,
            SourceCode = sourceCode ?? [],
            CommitTime = commitTime,
            CheckResults = []
        };
    }
}
