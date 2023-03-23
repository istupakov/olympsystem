using Olymp.Domain.Models;

namespace Olymp.Domain;

public class Standing
{
    public const int Penalty = 20;
    public const int MaxScore = 100;

    public required Contest Contest { get; set; }
    public required bool IsFrozen { get; set; }
    public required bool IsOfficial { get; set; }
    public required IEnumerable<UserResult> Results { get; set; }

    public class UserProblemResult
    {
        public required Problem Problem { get; set; }
        public bool IsSuccess { get; set; }
        public int CommitNumber { get; set; }
        public DateTimeOffset LastCommitTime { get; set; }
        public bool IsFirstSuccess { get; set; }
        public int Score { get; set; }
        public int MaxScore { get; set; }

        public double SummaryTime(DateTimeOffset startTime, int penalty) =>
            IsSuccess ? (LastCommitTime - startTime + TimeSpan.FromMinutes(penalty * (CommitNumber - 1))).TotalMilliseconds : 0;
    }

    public class UserResult
    {
        public required User User { get; set; }
        public int Score { get; set; }
        public TimeSpan SummaryTime { get; set; }
        public DateTimeOffset StartTime { get; set; }
        public required IEnumerable<UserProblemResult> ProblemResults { get; set; }
    }

    public static IEnumerable<UserProblemResult> UserProblemStanding(IEnumerable<Problem> problems, IEnumerable<Submission> submissions, IEnumerable<int>? firstSuccess = null)
    {
        return from problem in problems
               join submission in submissions
                   on problem.Id equals submission.ProblemId into problemSubmissions
               let success = problemSubmissions
                                .Where(submission => submission.StatusCode == 2 || submission.Score > 0)
                                .OrderBy(submission => submission.Score)
                                .ThenByDescending(submission => submission.CommitTime)
                                .LastOrDefault()
               let isSuccess = success != null
               let score = isSuccess ? success.Score : 0
               let first = firstSuccess != null && firstSuccess.Contains(problem.Id)
               let checkedSubmission = problemSubmissions.Where(submission => submission.StatusCode is not (0 or 1 or -1))
               let signSubmissions = isSuccess
                                   ? checkedSubmission.Where(submission => submission.CommitTime <= success.CommitTime)
                                   : checkedSubmission
               select new UserProblemResult
               {
                   Problem = problem,
                   IsSuccess = isSuccess,
                   CommitNumber = signSubmissions.Count(),
                   IsFirstSuccess = first,
                   Score = score ?? (isSuccess ? 1 : 0),
                   MaxScore = MaxScore,
                   LastCommitTime = signSubmissions.Select(submission => submission.CommitTime)
                                                   .OrderBy(x => x)
                                                   .LastOrDefault()
               };
    }

    public static IEnumerable<UserResult> NewStanding(Contest contest, bool includeAll, bool official, bool notFreeze, DateTimeOffset? startTime)
    {
        var penalty = contest.IsSchool ? 0 : Penalty;

        var users = Enumerable.Empty<User>();
        if (!startTime.HasValue)
        {
            startTime = contest.StartTime;
            users = contest.Competitors.Where(user => user.IsApproved && !user.IsHidden && (!official || !user.IsOutOfCompetition && !user.IsDisqualified));
        }

        var problems = contest.Problems.OrderBy(problem => problem.Number);
        var submissions = problems.SelectMany(problem => problem.Submissions)
                .OrderBy(submission => submission.CommitTime)
                .Where(submission => !submission.IsHidden)
                .Where(submission => submission.CommitTime >= startTime && submission.CommitTime <= startTime.Value + contest.Duration)
                .Where(submission => submission.StatusCode != 0 && submission.StatusCode != 1);

        if (contest.IsFreeze && !notFreeze)
            submissions = submissions.Where(submission => submission.CommitTime < contest.FreezeTime);

        if (includeAll)
            users = users.Union(submissions.Select(submission => submission.User).Where(user => !user.IsHidden));

        var firstSuccess = (from user in users
                            join submission in submissions on user.Id equals submission.UserId
                            where submission.StatusCode == 2
                            orderby submission.CommitTime
                            group submission by submission.ProblemId into probleminfo
                            select (problemId: probleminfo.Key, userId: probleminfo.First().UserId))
                            .ToLookup(x => x.userId, x => x.problemId);

        return from user in users
               join submission in submissions on user.Id equals submission.UserId into userSubmissions
               let problemResult = UserProblemStanding(problems, userSubmissions, firstSuccess[user.Id])
               let score = problemResult.Sum(res => res.Score)
               let summaryTime = TimeSpan.FromMilliseconds(problemResult.Sum(res => res.SummaryTime(startTime.Value, penalty)))
               orderby score descending, summaryTime ascending, score ascending, user.Name ascending
               select new UserResult
               {
                   User = user,
                   StartTime = startTime.Value,
                   ProblemResults = problemResult,
                   Score = score,
                   SummaryTime = summaryTime
               };
    }

    public static object StandingData(Contest contest, bool freeze, long? lastUpdate = null, DateTimeOffset? startTime = null)
    {
        var problemsInfo = from problem in contest.Problems
                           orderby problem.Number
                           select new
                           {
                               problem.Id,
                               problem.Number,
                               problem.Name
                           };
        var currentTime = DateTimeOffset.Now - (startTime ?? contest.StartTime);
        if (currentTime > contest.Duration)
            currentTime = contest.Duration;

        var submissions = from problem in contest.Problems
                          from submission in problem.Submissions
                          where !submission.User.IsHidden && !submission.IsHidden
                          let time = submission.CommitTime - (submission.User is Competitor ? contest.StartTime : startTime)
                          where time.HasValue && time >= TimeSpan.Zero && time <= currentTime
                          let intTime = (long)time.Value.TotalMilliseconds
                          where !lastUpdate.HasValue || intTime > lastUpdate
                          select new
                          {
                              submission.Id,
                              User = submission.UserId,
                              Problem = problem.Number,
                              Time = intTime,
                              Result = !freeze || !contest.IsFreeze || time < contest.TimeOfFreeze ? submission.StatusCode == 2 ? 1 : 0 : -1
                          };

        var users = from problem in contest.Problems
                    from user in problem.Submissions.Select(s => s.User)
                    where user is OlympUser
                    join submission in submissions on user.Id equals submission.User
                    select user;

        users = users.Distinct();
        if (!lastUpdate.HasValue)
            users = users.Concat(contest.Competitors);

        var usersInfo = from user in users
                        let competitor = user as Competitor
                        let members = competitor?.MemberNames ?? Array.Empty<string>()
                        select new
                        {
                            user.Id,
                            user.Name,
                            OOC = competitor?.IsOutOfCompetition ?? true,
                            Dsq = user.IsDisqualified,
                            Vrt = competitor == null,
                            Members = members
                        };

        submissions = from submission in submissions
                      group submission by new { submission.User, submission.Problem } into g
                      let fs = g.FirstOrDefault(s => s.Result == 1)
                      from submission in g
                      where fs == null || submission.Time <= fs.Time
                      orderby submission.Time
                      select submission;

        return new
        {
            contest.Name,
            EndTime = (startTime ?? contest.StartTime) + contest.Duration,
            Virtual = startTime.HasValue,
            Problems = problemsInfo,
            Users = usersInfo,
            Submits = submissions
        };
    }

    public static Standing Create(Contest contest, bool includeAll, bool official, bool notFreeze, DateTimeOffset? startTime)
    {
        return new Standing
        {
            Contest = contest,
            IsFrozen = contest.IsFreeze && !notFreeze,
            IsOfficial = official,
            Results = NewStanding(contest, includeAll, official, notFreeze, startTime)
        };
    }
}
