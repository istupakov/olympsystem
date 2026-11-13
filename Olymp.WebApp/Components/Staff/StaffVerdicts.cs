using Olymp.Domain.Models;
using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Staff;

public static class StaffVerdicts
{
    public static string Filter(int code) => code switch
    {
        0 => "queued",
        1 => "checking",
        2 => "accepted",
        -1 => "compilation",
        < -1000 and > -2000 => "wrong-answer",
        < -2000 and > -3000 => "time-limit",
        < -3000 and > -4000 => "runtime-error",
        < -4000 and > -5000 => "presentation-error",
        < -5000 and > -6000 => "idleness-limit",
        < -6000 and > -7000 => "memory-limit",
        _ => "internal-error"
    };

    public static bool HasTest(int code) => Filter(code) is "wrong-answer" or "time-limit" or "runtime-error"
        or "presentation-error" or "idleness-limit" or "memory-limit";

    public static string? Label(string? filter) => filter switch
    {
        "accepted" => SiteResources.StaffVerdictAccepted,
        "rejected" => SiteResources.StaffAnyError,
        "compilation" => SiteResources.StaffVerdictCompilation,
        "wrong-answer" => SiteResources.StaffVerdictWrongAnswer,
        "time-limit" => SiteResources.StaffVerdictTimeLimit,
        "runtime-error" => SiteResources.StaffVerdictRuntimeError,
        "presentation-error" => SiteResources.StaffVerdictPresentationError,
        "idleness-limit" => SiteResources.StaffVerdictIdlenessLimit,
        "memory-limit" => SiteResources.StaffVerdictMemoryLimit,
        "queued" => CheckStatusCodes.Template(0),
        "checking" => CheckStatusCodes.Template(1),
        "pending" => SiteResources.StaffPending,
        "internal-error" => CheckStatusCodes.Template(-10),
        _ => filter
    };
}
