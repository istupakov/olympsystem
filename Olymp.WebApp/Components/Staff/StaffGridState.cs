using System.Globalization;

using Microsoft.AspNetCore.WebUtilities;

namespace Olymp.WebApp.Components.Staff;

public sealed class StaffGridState
{
    private readonly Dictionary<string, int> _indices = [];
    private readonly Dictionary<string, Dictionary<object, int>> _rows = [];
    private readonly Dictionary<string, int> _selections = [];

    public int GetIndex(string uri)
    {
        var query = QueryHelpers.ParseQuery(new Uri(uri).Query);
        if (query.TryGetValue("row", out var value) && int.TryParse(value, out int row)) return Math.Max(0, row);
        if (_selections.TryGetValue(ListKey(uri), out int selected)) return selected;
        return _indices.GetValueOrDefault(uri);
    }

    public void RememberRows(string uri, IEnumerable<object> keys, int startIndex)
    {
        _selections.Remove(ListKey(uri));
        _indices[uri] = startIndex;
        _rows[uri] = keys.Select((key, index) => (key, index)).ToDictionary(item => item.key, item => startIndex + item.index);
    }

    public void RememberReturnTo(string currentUri, string? returnTo)
    {
        if (string.IsNullOrEmpty(BackUrl(returnTo, ""))) return;
        var address = new Uri(new Uri(currentUri), returnTo);
        var query = QueryHelpers.ParseQuery(address.Query);
        if (query.TryGetValue("row", out var value) && int.TryParse(value, out int row))
            _selections[ListKey(address.AbsoluteUri)] = Math.Max(0, row);
    }

    private static string ListKey(string uri)
    {
        var address = new Uri(uri);
        var query = QueryHelpers.ParseQuery(address.Query).Where(item => !string.Equals(item.Key, "row", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(item => item.Key, item => (string?)item.Value.ToString());
        return QueryHelpers.AddQueryString(address.GetLeftPart(UriPartial.Path), query);
    }

    public string DetailUrl(string uri, string detail, object? itemKey = null)
    {
        int index = itemKey is not null && _rows.TryGetValue(uri, out var rows) && rows.TryGetValue(itemKey, out int row) ? row : GetIndex(uri);
        var address = new Uri(uri);
        var query = QueryHelpers.ParseQuery(address.Query).ToDictionary(item => item.Key, item => (string?)item.Value.ToString());
        query["row"] = index.ToString(CultureInfo.InvariantCulture);
        string returnTo = QueryHelpers.AddQueryString(address.AbsolutePath, query);
        return $"{detail}?returnTo={Uri.EscapeDataString(returnTo)}";
    }

    public static string BackUrl(string? returnTo, string fallback) => returnTo is not null
        && returnTo.StartsWith("/admin/", StringComparison.Ordinal) && !returnTo.Contains('\\') ? returnTo : fallback;
}
