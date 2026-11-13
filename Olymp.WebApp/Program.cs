using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;

using Olymp.Checker;
using Olymp.Checker.AntiCheat;
using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.WebApp;
using Olymp.WebApp.Components;
using Olymp.WebApp.Components.Account;
using Olymp.WebApp.Components.Contests;
using Olymp.WebApp.Components.Layout;
using Olymp.WebApp.Components.Staff;
using Olymp.WebApp.Components.Submissions;
using Olymp.WebApp.Services.AntiCheat;
using Olymp.WebApp.Services.Authentication;
using Olymp.WebApp.Services.Authorization;
using Olymp.WebApp.Services.Checker;
using Olymp.WebApp.Services.Contests;
using Olymp.WebApp.Services.Identity;
using Olymp.WebApp.Services.Mail;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (string address in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        options.KnownProxies.Add(IPAddress.Parse(address));
});

var dataProtection = builder.Services.AddDataProtection();
if (builder.Environment.IsProduction() && builder.Configuration["DataProtection:Path"] is string keyPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));

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
var juryOrAdminPolicy = new AuthorizationPolicyBuilder(defaultPolicy)
    .RequireRole(RoleNames.Jury, RoleNames.Admin)
    .Build();

builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(defaultPolicy)
    .AddPolicy(PolicyNames.Runner, policy => policy.AddAuthenticationSchemes("ApiKey").RequireAuthenticatedUser())
    .AddPolicy("SignedIn", signedInPolicy)
    .AddPolicy(PolicyNames.PermanentAccount, permanentAccountPolicy)
    .AddPolicy(PolicyNames.OlympUser, olympUserPolicy)
    .AddPolicy(PolicyNames.CancelRegistration, policy => policy
        .Combine(olympUserPolicy)
        .AddRequirements(new CancelRegistrationRequirement()))
    .AddPolicy(PolicyNames.JuryOrAdmin, juryOrAdminPolicy)
    .AddPolicy(PolicyNames.ManageContest, policy => policy
        .Combine(juryOrAdminPolicy)
        .AddRequirements(new ManageContestRequirement()))
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
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 256 * 1024);

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
builder.Services.AddWebEncoders(options =>
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic));

builder.Services.AddScoped<CompetitorManagementService>();
builder.Services.AddScoped<ContestProblemService>();
builder.Services.AddScoped<AdminParticipantService>();
builder.Services.AddScoped<UnconfirmedUserCleanupService>();
builder.Services.AddScoped<StaffGridState>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IcpcImportService>();
builder.Services.AddHttpClient<Olymp.Icpc.IcpcClient>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
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
builder.Services.AddScoped<IAuthorizationHandler, ManageContestHandler>();
builder.Services.AddScoped<IAuthorizationHandler, CancelRegistrationHandler>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options => options.DefaultScheme = IdentityConstants.ApplicationScheme)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("ApiKey", options =>
        options.ApiKeys = builder.Configuration.GetSection("ApiKeys").Get<string[]>() ?? [])
    .AddIdentityCookies();
builder.Services.AddGrpc();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContextFactory<OlympContext>(options => options.UseSqlServer(connectionString)
    .EnableSensitiveDataLogging(builder.Environment.IsDevelopment()));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();
builder.Services.AddHealthChecks().AddDbContextCheck<OlympContext>();

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

app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
    app.UseHttpsRedirection();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseRequestLocalization();
app.UseHealthChecks("/healthz");

app.UseRewriter(new RewriteOptions()
    // Legacy links point directly to the current standings route.
    .AddRedirect(@"^(?:olympiad|contests)/standing/(\d+)/?$", "contests/$1/standing", StatusCodes.Status301MovedPermanently));

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.MapLanguageEndpoint();
app.MapContestDownloads();
app.MapSubmissionDownloads();
app.MapStaffDownloads();
app.MapGrpcService<RunnerService>().RequireAuthorization(PolicyNames.Runner);

app.Run();
