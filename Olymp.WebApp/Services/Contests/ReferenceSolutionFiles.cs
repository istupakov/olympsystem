using System.Text;

namespace Olymp.WebApp.Services.Contests;

internal static class ReferenceSolutionFiles
{
    internal const string ValidatorFilename = "validator.cpp";
    internal static bool IsValidator(string? filename) => string.Equals(filename, ValidatorFilename, StringComparison.OrdinalIgnoreCase);

    internal static readonly Encoding SourceEncoding = Encoding.GetEncoding(1251,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    internal static bool IsValidPath(string? path) => !string.IsNullOrWhiteSpace(path)
        && path.Split('/').All(part => !string.IsNullOrWhiteSpace(part) && part is not "." and not ".."
            && !part.EndsWith('.') && !part.EndsWith(' ')
            && !part.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c))
            && !IsReserved(part.Split('.')[0]));

    private static bool IsReserved(string stem) => stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');

    internal static string Filename(int id, string? description, string extension) => IsValidPath(description)
        ? description! : $"submission-{id}{(IsValidPath("source" + extension) && !extension.Contains('/') ? extension : ".src")}";
}
