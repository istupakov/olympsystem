namespace Olymp.WebApp.Services.Contests;

public sealed record ContestExportOptions(bool Tests = false, bool Participants = false, bool ResultsAndMessages = false,
    bool ReferenceSolutions = false, bool JurySolutions = false, bool TestZip = false, bool Pdf = false, bool Tex = false,
    bool Tables = false, bool Markdown = false)
{
    public static ContestExportOptions All { get; } = new(true, true, true, true, true, true, true, true, true, true);
}
