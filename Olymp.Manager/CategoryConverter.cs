using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

using Olymp.Domain.Models;

namespace Olymp.Manager;

public class Category
{
    public required string Name { get; set; }
    public required object Values { get; set; }
}

[ValueConversion(typeof(object), typeof(Category[]))]
public class CategoryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            Problem problem => new[] {
                new Category { Name = nameof(problem.Submissions), Values = problem.Submissions },
                new Category { Name = nameof(problem.Messages), Values = problem.Messages },
                    new Category { Name = nameof(problem.Tests), Values = problem.Tests }
                },
            Competitor competitor => new[] {
                    new Category { Name = nameof(competitor.Submissions), Values = competitor.Submissions
                        .OrderBy(s => s.Problem.Number)
                        .GroupBy(s => s.Problem.NameWithNumber, (problem, sols) =>
                        new Category { Name = problem, Values = sols })
                    },
                    new Category { Name = nameof(competitor.Messages), Values = competitor.Messages },
                    new Category { Name = nameof(competitor.Members), Values = competitor.Members }
                },
            OlympUser olympUser => new[] {
                    new Category { Name = nameof(olympUser.Submissions), Values = olympUser.Submissions
                        .OrderBy(s => s.Problem.Number)
                        .GroupBy(s => s.Problem.NameWithNumber,
                        (problem, sols) => new Category { Name = problem, Values = sols })
                    },
                    new Category { Name = nameof(olympUser.Messages), Values = olympUser.Messages },
                    new Category { Name = nameof(olympUser.Memberships), Values = olympUser.Memberships }
                },
            Contest contest => new[] {
                    new Category { Name = nameof(contest.Problems), Values = contest.Problems },
                    new Category { Name = nameof(contest.Competitors), Values = contest.Competitors },
                    new Category { Name = nameof(contest.Messages), Values = contest.Messages }
                },
            _ => throw new NotSupportedException()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
