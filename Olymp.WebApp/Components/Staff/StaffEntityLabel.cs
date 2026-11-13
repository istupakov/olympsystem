using System.Globalization;

namespace Olymp.WebApp.Components.Staff;

public static class StaffEntityLabel
{
    public static string? Format(int? id, string? name)
    {
        if (id is not int value) return null;
        string reference = "ID " + value.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(name) ? reference : $"{name} · {reference}";
    }
}
