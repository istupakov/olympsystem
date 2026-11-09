using System.ComponentModel.DataAnnotations;

using Olymp.WebApp.Resources;

namespace Olymp.WebApp.Components.Submissions;

public sealed class SubmissionInput
{
    [Required(ErrorMessageResourceType = typeof(SiteResources), ErrorMessageResourceName = nameof(SiteResources.CompilerRequired))]
    [Display(Name = nameof(SiteResources.Compiler), ResourceType = typeof(SiteResources))]
    public int? CompilatorId { get; set; }

    [Required(ErrorMessageResourceType = typeof(SiteResources), ErrorMessageResourceName = nameof(SiteResources.SourceCodeRequired))]
    [StringLength(32 * 1024, MinimumLength = 10,
        ErrorMessageResourceType = typeof(SiteResources), ErrorMessageResourceName = nameof(SiteResources.SourceCodeLength))]
    [Display(Name = nameof(SiteResources.SourceCode), ResourceType = typeof(SiteResources))]
    public string Code { get; set; } = "";
}
