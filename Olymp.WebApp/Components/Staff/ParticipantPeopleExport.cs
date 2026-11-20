using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Staff;

internal static class ParticipantPeopleExport
{
    public static IEnumerable<string?[]> Rows(IEnumerable<Competitor> participants)
    {
        var rows = new List<Person>();
        var accounts = new Dictionary<int, Person>();
        foreach (var participant in participants)
        {
            var members = (participant.GetMembers() ?? participant.Members.Select(item => (item.Name, (OlympUser?)item))).ToArray();
            foreach (var (name, user) in members)
                Add(user, name, participant.Organization?.Name, false);
            if (members.Length == 0 && !participant.Contest.IsAcm)
                Add(null, participant.Name, participant.Organization?.Name, false);
            if (participant.Coach is { } coach)
                Add(coach, coach.Name, participant.Organization?.Name, true);
        }
        return rows.Select(person => new string?[] { person.Name, person.Organization,
            person.IsParticipant && person.IsCoach ? SiteResources.StaffPersonParticipantAndCoach
                : person.IsCoach ? SiteResources.StaffCoach : SiteResources.StaffPersonParticipant });

        void Add(OlympUser? user, string name, string? organization, bool isCoach)
        {
            if (user is null || !accounts.TryGetValue(user.Id, out var person))
            {
                string fullName = user is null ? name : string.Join(' ', new[] { user.LastName, user.FirstName, user.Patronymic }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                person = new(fullName.Length == 0 ? name : fullName, user?.Organization?.Name ?? organization);
                rows.Add(person);
                if (user is not null) accounts.Add(user.Id, person);
            }
            if (isCoach) person.IsCoach = true;
            else person.IsParticipant = true;
        }
    }

    private sealed class Person(string name, string? organization)
    {
        public string Name { get; } = name;
        public string? Organization { get; } = organization;
        public bool IsParticipant { get; set; }
        public bool IsCoach { get; set; }
    }
}
