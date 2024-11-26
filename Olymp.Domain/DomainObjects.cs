using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace Olymp.Domain.Models;

public partial class Problem
{
    public string NameWithNumber => $"{Number}. {Name}";
}

public partial class Compilator
{
    public string NameWithDescription => Name + (!string.IsNullOrWhiteSpace(Description) ? $" ({Description})" : "");
}

public partial class Competitor
{
    public IEnumerable<(string name, OlympUser? user)>? GetMembers() =>
        MemberInfos?.Split(';') is [var name1, var id1s, var name2, var id2s, var name3, var id3s] ?
        [
            (name1, int.TryParse(id1s, out var id1)? Members.Single(x => x.Id == id1): null),
            (name2, int.TryParse(id2s, out var id2)? Members.Single(x => x.Id == id2): null),
            (name3, int.TryParse(id3s, out var id3)? Members.Single(x => x.Id == id3): null)
        ] : null;

    public IEnumerable<string>? MemberNames => GetMembers()?.Select(x => x.name);
}

public partial class OlympUser
{
    public const string PublicNameRegex = @"[А-ЯЁа-яёA-Za-z0-9- ]+";
    public const string NameRegex = @"[А-ЯЁа-яё\- ]+[а-яё]";
    public const string GroupRegex = @"[А-ЯЁA-Z0-9\-]+";
}

public partial class Contest
{
    [NotMapped]
    public DateTime Date
    {
        get => StartTime.Date;
        set
        {

            StartTime = value.Date + StartTime.TimeOfDay;
            EndTime = value.Date + EndTime.TimeOfDay;
            if (ProblemShowTime.HasValue)
                ProblemShowTime = value.Date + ProblemShowTime.Value.TimeOfDay;
            if (FreezeTime.HasValue)
                FreezeTime = value.Date + FreezeTime.Value.TimeOfDay;
        }
    }

    [NotMapped]
    public TimeSpan TimeOfStart
    {
        get => StartTime.TimeOfDay;
        set => StartTime = Date + value;
    }

    [NotMapped]
    public TimeSpan? TimeOfShowProblem
    {
        get => ProblemShowTime.HasValue ? ProblemShowTime.Value.TimeOfDay : null;
        set => ProblemShowTime = value.HasValue ? Date + value : null;
    }

    [NotMapped]
    public TimeSpan? TimeOfFreeze
    {
        get => FreezeTime.HasValue ? FreezeTime.Value.TimeOfDay : null;
        set => FreezeTime = value.HasValue ? Date + value : null;
    }

    [NotMapped]
    public TimeSpan Duration
    {
        get => EndTime - StartTime;
        set => EndTime = StartTime + value;
    }

    [NotMapped]
    public string? FinalTableText
    {
        get => FinalTable is null ? null : Encoding.GetEncoding(1251).GetString(FinalTable.Data);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                FinalTable = null;
            else if (FinalTable is null)
            {
                FinalTable = new Blob
                {
                    Data = Encoding.GetEncoding(1251).GetBytes(value)
                };
            }
            else
            {
                FinalTable.Data = Encoding.GetEncoding(1251).GetBytes(value);
            }
        }
    }

    [NotMapped]
    public string? OfficialTableText
    {
        get => OfficialTable is null ? null : Encoding.GetEncoding(1251).GetString(OfficialTable.Data);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                OfficialTable = null;
            else if (OfficialTable is null)
            {
                OfficialTable = new Blob
                {
                    Data = Encoding.GetEncoding(1251).GetBytes(value)
                };
            }
            else
            {
                OfficialTable.Data = Encoding.GetEncoding(1251).GetBytes(value);
            }
        }
    }

    public bool IsFreeze => FreezeTime.HasValue && DateTimeOffset.Now > FreezeTime;

    public bool AllowDownloadProblems => !IsHidden && (DateTimeOffset.Now >= StartTime || DateTimeOffset.Now >= ProblemShowTime);
    public bool AllowShowStanding => !IsHidden && DateTimeOffset.Now >= StartTime;
    public bool AllowDownloadTests => !IsHidden && IsOpen && DateTimeOffset.Now > EndTime;
    public bool AllowSubmitSolution => !IsHidden && DateTimeOffset.Now >= StartTime && (DateTimeOffset.Now <= EndTime || IsOpen);
    public bool AllowSendMessage => !IsHidden && DateTimeOffset.Now >= StartTime && DateTimeOffset.Now <= EndTime;

    public bool IsAcm => Abbr.StartsWith("acm");
    public bool IsSchool => Abbr.StartsWith("school");
}

public partial class Submission
{
    [NotMapped]
    public string Text
    {
        get => Encoding.GetEncoding(1251).GetString(SourceCode);
        set => SourceCode = Encoding.GetEncoding(1251).GetBytes(value);
    }

    [NotMapped]
    public string DescriptionOrId => Description ?? Id.ToString();

    public string StatusTemplate => CheckStatusCodes.Template(StatusCode);

    public int StatusTestNumber => CheckStatusCodes.TestNumber(StatusCode);

    public string StatusText => CheckStatusCodes.Description(StatusCode);

    public string? FormattedScoresByTest => ScoresByTest is null ? null :
        string.Concat(ScoresByTest.Where(c => char.IsUpper(c) || char.IsDigit(c) || c == ';'));
}

public static class CheckStatusCodes
{
    public static string Template(int statusCode) => statusCode switch
    {
        0 => "Queued...",
        1 => "Checking...",
        2 => "Accepted",
        -1 => "Compilation error",
        < -1000 and > -2000 => "Wrong answer on test {0}",
        < -2000 and > -3000 => "Time limit exceeded on test {0}",
        < -3000 and > -4000 => "Runtime error on test {0}",
        < -4000 and > -5000 => "Presentation error on test {0}",
        < -5000 and > -6000 => "Idleness limit exceeded on test {0}",
        < -6000 and > -7000 => "Memory limit exceeded on test {0}",
        _ => "Internal error"
    };

    public static int TestNumber(int statusCode) => (-statusCode) % 1000;

    public static string Description(int statusCode) =>
        string.Format(Template(statusCode), TestNumber(statusCode));
}
