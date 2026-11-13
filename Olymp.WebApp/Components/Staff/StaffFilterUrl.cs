using System.Globalization;

using Microsoft.AspNetCore.WebUtilities;

namespace Olymp.WebApp.Components.Staff;

public static class StaffFilterUrl
{
    public static string Set(string uri, string name, object? value, params string[] clear)
    {
        var address = new Uri(new Uri("https://staff.invalid/"), uri);
        var query = QueryHelpers.ParseQuery(address.Query).ToDictionary(item => item.Key, item => (string?)item.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        foreach (string key in clear.Concat(["row", "page", "SubmissionPage"])) query.Remove(key);
        if (value is null) query.Remove(name);
        else query[name] = Convert.ToString(value, CultureInfo.InvariantCulture);
        bool absolute = Uri.TryCreate(uri, UriKind.Absolute, out var original) && original.Scheme is "http" or "https";
        string path = absolute ? address.GetLeftPart(UriPartial.Path) : address.AbsolutePath;
        return QueryHelpers.AddQueryString(path, query) + address.Fragment;
    }
}
