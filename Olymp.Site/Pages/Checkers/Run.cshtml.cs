using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

using Olymp.Checker;
using Olymp.Site.Services.Checker;

namespace Olymp.Site.Pages.Checkers;

public class RunModel(ICheckerManager checkerManager,
    IEnumerable<ICompilatorConfiguration> compilatorConfigurations,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    public const int TimeLimitSeconds = 1;
    public const int MemoryLimitMb = 128;

    private readonly ICheckerManager _checkerManager = checkerManager;
    private readonly IReadOnlyList<ICompilatorConfiguration> _configurations = [.. compilatorConfigurations];
    private readonly IStringLocalizer<SharedResource> _localizer = localizer;

    public IReadOnlyList<SelectListItem> CheckerList { get; private set; } = [];
    public IReadOnlyList<SelectListItem> CompilatorList { get; private set; } = [];

    public string? Output { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Runner")]
        public Guid? CheckerId { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Compilator")]
        public string? CompilatorName { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Source code")]
        public string Code { get; set; } = null!;

        [Display(Name = "Input data")]
        public string? Input { get; set; }
    }

    private void Load()
    {
        var checkers = _checkerManager.Checkers.Values.ToArray();
        CheckerList = [.. checkers.Select(x =>
            new SelectListItem { Text = $"{x.Name} ({x.Id})", Value = x.Id.ToString() })];

        var checker = checkers.SingleOrDefault(x => x.Id == Input.CheckerId);
        CompilatorList = [.. _configurations
            .Where(c => checkers.Any(x => x.SupportedEnvs.Contains(c.ConfigName)))
            .Select(c => new SelectListItem
            {
                Value = c.Name,
                Text = checker?.Compilators?.SingleOrDefault(x => x.Configuration.Name == c.Name)?.DisplayName ?? c.Name
            })
            .OrderBy(x => x.Text, StringComparer.Ordinal)];
    }

    public void OnGet()
    {
        var checkers = _checkerManager.Checkers.Values.ToArray();
        if (checkers.Length == 1)
            Input.CheckerId = checkers[0].Id;

        Load();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        Load();

        if (!ModelState.IsValid)
            return Page();

        if (Input.CheckerId is not { } checkerId
            || !_checkerManager.Checkers.TryGetValue(checkerId, out var checker))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.CheckerId)}",
                _localizer["The selected runner is no longer connected."]);
            return Page();
        }

        var configuration = _configurations.SingleOrDefault(x => x.Name == Input.CompilatorName);
        if (configuration is null || !checker.SupportedEnvs.Contains(configuration.ConfigName))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.CompilatorName)}",
                _localizer["The selected compilator is unavailable on this runner."]);
            return Page();
        }

        try
        {
            Output = await checker.Run(Input.Code, configuration, Input.Input ?? string.Empty,
                TimeSpan.FromSeconds(TimeLimitSeconds), MemoryLimitMb, token);
        }
        catch (Exception) when (!token.IsCancellationRequested
            && (!_checkerManager.Checkers.TryGetValue(checkerId, out var current) || !ReferenceEquals(current, checker)))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.CheckerId)}",
                _localizer["The selected runner disconnected during execution. Select a connected runner and try again."]);
            Load();
        }
        catch (NotSupportedException) when (!token.IsCancellationRequested)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.CompilatorName)}",
                _localizer["The selected compilator is unavailable on this runner."]);
        }

        return Page();
    }
}
