using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Checker.AntiCheat;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.IdentityUI;
using Olymp.Site.Services.AntiCheat;
using Olymp.Site.Services.Checker;
using Olymp.Site.Services.Contests;
using Olymp.Site.Services.Identity;
using Olymp.Site.Services.Mail;
using Olymp.WebApp.Components;
using Olymp.WebApp.Components.Account;
using Olymp.WebApp.Components.Contests;
using Olymp.WebApp.Components.Layout;
using Olymp.WebApp.Components.Submissions;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

// Keep authorization and UI consistent for the lifetime of the application.
var identityFeatures = builder.Configuration.GetSection(IdentityFeatures.SectionName)
    .Get<IdentityFeatures>() ?? new();
builder.Services.AddSingleton(identityFeatures);
builder.Services.AddScoped<IAuthorizationHandler, EmailAsLoginHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, EmailAsLoginRedirectHandler>();

var signedInPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .Build();
// Account management must remain accessible before email migration is complete.
var permanentAccountPolicy = new AuthorizationPolicyBuilder(signedInPolicy)
    .RequireAssertion(context => !context.User.HasClaim(claim => claim.Type == ClaimNames.CompetitorContest))
    .Build();
var defaultPolicy = new AuthorizationPolicyBuilder(signedInPolicy)
    .AddRequirements(new EmailAsLoginRequirement())
    .Build();
var olympUserPolicy = new AuthorizationPolicyBuilder(defaultPolicy)
    .Combine(permanentAccountPolicy)
    .Build();

builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(defaultPolicy)
    .AddPolicy("SignedIn", signedInPolicy)
    .AddPolicy(PolicyNames.PermanentAccount, permanentAccountPolicy)
    .AddPolicy(PolicyNames.OlympUser, olympUserPolicy)
    .AddPolicy(PolicyNames.JuryOrAdmin, policy => policy
        .Combine(defaultPolicy)
        .RequireRole(RoleNames.Jury, RoleNames.Admin))
    .AddPolicy(PolicyNames.Admin, policy => policy
        .Combine(defaultPolicy)
        .RequireRole(RoleNames.Admin))
    .AddPolicy(PolicyNames.Coach, policy => policy
        .Combine(olympUserPolicy)
        .RequireClaim(ClaimNames.CoachOrg))
    .AddPolicy(IdentityFeatures.PasskeysPolicy, policy => policy
        .RequireAssertion(_ => identityFeatures.PasskeysEnabled));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRouting(options => options.LowercaseUrls = true);

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddRequestLocalization(options =>
{
    options.SetDefaultCulture("en");
    options.AddSupportedCultures("ru", "en");
    options.AddSupportedUICultures("ru", "en");
    options.ApplyCurrentCultureToResponseHeaders = true;
    options.RequestCultureProviders = [new CookieRequestCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
});

builder.Services.AddScoped<CompetitorManagementService>();
builder.Services.AddSimpleCheckers();
builder.Services.AddCheckerTests();
builder.Services.AddCompilators();
builder.Services.AddSingleton<ICheckerManager, CheckerManager>();
builder.Services.AddScoped<AntiCheatReportService>();
builder.Services.AddTransient<ISubmissionSimilarityService, SimpleSubmissionSimilarityService>();
builder.Services.AddTransient<IAIDetectionService, SimpleAIDetectionService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuthorizationHandler, SubmitSolutionHandler>();
builder.Services.AddScoped<SubmissionService>();
builder.Services.AddScoped<IAuthorizationHandler, ViewSolutionHandler>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options => options.DefaultScheme = IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContextFactory<OlympContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

if (builder.Environment.IsDevelopment() && !builder.Configuration.GetSection(nameof(MailService)).Exists())
    builder.Services.AddTransient<IEmailSender, DebugEmailSender>();
else
{
    builder.Services.AddOptions<MailServiceConfiguration>()
        .Bind(builder.Configuration.GetSection(nameof(MailService)))
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddTransient<IEmailSender, MailService>();
}

builder.Services.AddTransient<IPasswordHasher<User>, OlympPasswordHasher>();
builder.Services.AddTransient<IPasswordValidator<User>, OlympPasswordValidator>();
builder.Services.AddTransient<IUserValidator<User>, OlympUserValidator>();

builder.Services.AddIdentityCore<User>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.User.AllowedUserNameCharacters = string.Empty;
        options.SignIn.RequireConfirmedAccount = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 8;
        // Preserve the existing database schema until passkeys are introduced.
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;
    })
    .AddRoles<IdentityRole<int>>()
    .AddEntityFrameworkStores<OlympContext>()
    .AddSignInManager()
    .AddErrorDescriber<AccountErrorDescriber>()
    .AddDefaultTokenProviders()
    .AddUserConfirmation<OlympUserConfirmation>();

builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(2));

builder.Services.AddTransient<IEmailSender<User>, IdentityEmailSender>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseRequestLocalization();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseRewriter(new RewriteOptions()
    .AddRedirectToNonWwwPermanent()
    // Legacy links point directly to the current standings route.
    .AddRedirect(@"^(?:olympiad|contests)/standing/(\d+)/?$", "contests/$1/standing", StatusCodes.Status301MovedPermanently));

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.MapLanguageEndpoint();
app.MapContestDownloads();
app.MapSubmissionDownloads();

app.Run();
