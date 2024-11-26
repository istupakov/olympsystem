using System.Text.Encodings.Web;
using System.Text.Unicode;

using LettuceEncrypt;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;

using Olymp.Domain;
using Olymp.Domain.Models;
using Olymp.Site;
using Olymp.Site.IdentityUI;
using Olymp.Site.Services.Authentication;
using Olymp.Site.Services.Authorization;
using Olymp.Site.Services.Checker;
using Olymp.Site.Services.Identity;
using Olymp.Site.Services.Mail;
using Olymp.Site.Services.Plagiarism;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10);
});

// Add services to the container.

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<OlympContext>(options => options.UseSqlServer(connectionString)
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution)
    .EnableSensitiveDataLogging(builder.Environment.IsDevelopment()));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

if (builder.Environment.IsDevelopment() && !builder.Configuration.GetSection(nameof(MailService)).Exists())
    builder.Services.AddTransient<IEmailSender, DebugEmailSender>();
else
{
    builder.Services.Configure<MailServiceConfiguration>(
        builder.Configuration.GetSection(nameof(MailService)));
    builder.Services.AddTransient<IEmailSender, MailService>();
}

builder.Services.AddTransient<IPasswordHasher<User>, OlympPasswordHasher>();
builder.Services.AddTransient<IPasswordValidator<User>, OlympPasswordValidator>();
builder.Services.AddTransient<IUserValidator<User>, OlympUserValidator>();

builder.Services.AddIdentity<User, IdentityRole<int>>()
    .AddEntityFrameworkStores<OlympContext>()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<LocalizedIdentityErrorDescriber>()
    .AddUserConfirmation<OlympUserConfirmation>();

builder.Services.Configure<IdentityOptions>(options =>
{
    // Legacy: for old users 
    options.User.RequireUniqueEmail = false;
    options.User.AllowedUserNameCharacters = string.Empty;

    options.SignIn.RequireConfirmedAccount = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 8;
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
    options.LoginPath = "/Identity/Account/Login";
});

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddRequestLocalization(options =>
{
    options.SetDefaultCulture("ru");
    options.AddSupportedCultures("ru", "en");
    options.AddSupportedUICultures("ru", "en");
    options.ApplyCurrentCultureToResponseHeaders = true;
});
builder.Services.AddWebEncoders(options =>
{
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic);
});

builder.Services.AddRouting(options => options.LowercaseUrls = true);

builder.Services.AddAuthentication()
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("ApiKey", options =>
    {
        options.ApiKeys = builder.Configuration.GetSection("ApiKeys").Get<string[]>() ?? [];
    });

builder.Services.AddScoped<IAuthorizationHandler, ContestAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, SubmissionAuthorizationHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(PolicyNames.Runner, policy => policy.AddAuthenticationSchemes("ApiKey")
                                                          .RequireAuthenticatedUser());
    options.AddPolicy(PolicyNames.OlympUser, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context => !context.User.HasClaim(x => x.Type == ClaimNames.CompetitorContest));
    });
    options.AddPolicy(PolicyNames.Admin, policy => policy.RequireRole(RoleNames.Admin));
    options.AddPolicy(PolicyNames.Coach, policy => policy.RequireClaim(ClaimNames.CoachOrg));
    options.AddPolicy(PolicyNames.JuryOrAdmin, policy => policy.RequireRole(RoleNames.Jury, RoleNames.Admin));
});

builder.Services.AddSimpleCheckers();
builder.Services.AddCheckerTests();
builder.Services.AddSingleton<ICheckerManager, CheckerManager>();

builder.Services.AddTransient<ISubmissionSimilarityService, SimpleSubmissionSimilarityService>();

builder.Services.AddGrpc();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeAreaFolder("Identity", "/Account/Manage", PolicyNames.OlympUser);
})
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization<SharedResource>()
    .AddDataAnnotationsLocalization<IdentityUIResources>();

var dataProtectionPath = builder.Configuration["DataProtection:Path"];
if (builder.Environment.IsProduction() && dataProtectionPath is not null)
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}

var lettuceEncryptPath = builder.Configuration["LettuceEncrypt:Path"];
if (builder.Environment.IsProduction() && lettuceEncryptPath is not null)
{
    var password = builder.Configuration.GetValue<string>("LettuceEncrypt:Password");
    builder.Services.AddLettuceEncrypt()
        .PersistDataToDirectory(new DirectoryInfo(lettuceEncryptPath), password);
}

builder.Services.AddHealthChecks()
    .AddDbContextCheck<OlympContext>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRewriter(new RewriteOptions()
    .AddRedirectToNonWwwPermanent()
    // Legacy: for old links
    .AddRedirect(@"olympiad/standing/(\w+)", "contests/standing/$1", StatusCodes.Status301MovedPermanently));

app.UseRequestLocalization();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapHealthChecks("/healthz");
app.MapGrpcService<RunnerService>().RequireAuthorization(PolicyNames.Runner);
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
