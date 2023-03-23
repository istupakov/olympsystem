using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site.Services.Checker;

namespace Olymp.Site.Pages.Checkers;

public class RunModel : PageModel
{
    private readonly OlympContext _context;
    private readonly ICheckerManager _checkerManager;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public IEnumerable<SelectListItem> CheckerList { get; private set; } = null!;
    public SelectList CompilatorList { get; private set; } = null!;
    public IEnumerable<Compilator> Compilators { get; private set; } = null!;

    public string? Output { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Checker")]
        public Guid? CheckerId { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Compilator")]
        public int? CompilatorId { get; set; }

        [Required(ErrorMessage = "The {0} field is required.")]
        [Display(Name = "Source code")]
        public string Code { get; set; } = null!;

        [Display(Name = "Input data")]
        public string? Input { get; set; } = null!;
    }

    public RunModel(OlympContext context, ICheckerManager checkerManager,
        IStringLocalizer<SharedResource> localizer)
    {
        _context = context;
        _checkerManager = checkerManager;
        _localizer = localizer;
    }

    public async Task LoadAsync()
    {
        Compilators = await _context.Compilators
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .ToListAsync();

        CompilatorList = new SelectList(Compilators, nameof(Compilator.Id), nameof(Compilator.NameWithDescription));

        CheckerList = _checkerManager.Checkers.Values.Select(x =>
            new SelectListItem { Text = $"{x.Name} ({x.Id})", Value = x.Id.ToString() });
    }

    public async Task OnGet()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        await LoadAsync();

        var compilator = Compilators.SingleOrDefault(x => x.Id == Input.CompilatorId);
        if (compilator is null)
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.CompilatorId)}", _localizer["Invalid value"]);

        if (!_checkerManager.Checkers.TryGetValue(Input.CheckerId!.Value, out var checker))
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.CheckerId)}", _localizer["Invalid value"]);

        if (ModelState.IsValid)
        {
            Output = await checker!.Run(Input.Code, compilator!, Input.Input ?? string.Empty,
                TimeSpan.FromSeconds(1), 128, token);
        }

        return Page();
    }
}
