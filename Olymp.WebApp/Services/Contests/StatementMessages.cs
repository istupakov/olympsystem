using Olymp.Statements;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Services.Contests;

public static class StatementMessages
{
    public static string Diagnostic(StatementDiagnostic diagnostic) => string.Format(SiteResources.StatementDiagnostic,
        diagnostic.Line, diagnostic.Code switch
        {
            "Title" => SiteResources.StatementTitleError,
            "Section" => SiteResources.StatementSectionError,
            "Sections" => SiteResources.StatementSectionsError,
            "Math" => SiteResources.StatementMathError,
            _ => SiteResources.StatementConstructError
        });
}
