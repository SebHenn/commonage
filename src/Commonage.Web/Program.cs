using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Commonage.Web;
using Commonage.Web.Audit;
using Commonage.Web.Contributions;
using Commonage.Web.Data;
using Commonage.Web.Demo;
using Commonage.Web.Domain;
using Commonage.Web.Export;
using Commonage.Web.Forms;
using Commonage.Web.Identity;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Commonage.Web.Search;
using Commonage.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;

var builder = WebApplication.CreateBuilder(args);
// Do not tell visitors which web server runs the archive.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// The connection string comes from user-secrets (local development) or the
// ConnectionStrings__Default environment variable (Docker). It is never stored in the repository.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Default is not configured. See docs/development.md.");

builder.Services.AddDbContext<CommonageDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHealthChecks().AddDbContextCheck<CommonageDbContext>();
builder.Services.AddDataProtection().PersistKeysToDbContext<CommonageDbContext>();

builder.Services
    .AddIdentity<IdentityUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Lockout.AllowedForNewUsers = true;
        // Length makes a password strong, not forced symbols (NIST SP 800-63B). A few words in a row are easy to
        // remember and hard to guess.
        options.Password.RequiredLength = Accounts.MinimumPasswordLength;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<CommonageDbContext>()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<LocalizedIdentityErrorDescriber>();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});
// Logins are checked against the account every minute, so a disabled account or a changed role takes effect quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));
// Setup links for new accounts are handed over in person or by the admin's own email, so they stay valid for a week.
builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromDays(7));
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.ManageArchive, policy => policy.RequireRole(Roles.Admin, Roles.Curator))
    .AddPolicy(Policies.Administer, policy => policy.RequireRole(Roles.Admin));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<ArchiveExporter>();

// Uploaded originals and their previews. In Docker this directory is a persistent volume.
var storageRoot = Path.GetFullPath(
    builder.Configuration["Storage:RootPath"] ?? Path.Combine("App_Data", "media"), builder.Environment.ContentRootPath);
builder.Services.AddSingleton<IFileStorage>(new LocalFileStorage(storageRoot));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Uploads"));
builder.Services.AddScoped<MediaUploadService>();
builder.Services.AddSingleton<MediaProcessingQueue>();
builder.Services.AddHostedService<MediaProcessor>();
builder.Services.AddScoped<ArchiveBin>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SupportedLanguages>();
builder.Services.AddSingleton<ArchiveProfile>();
builder.Services.AddSingleton<LogoImages>();
builder.Services.AddHostedService<ArchiveBinPurger>();

// Free disk space and the age of the last backup: admins see them, and a nearly full disk stops uploads.
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.Section));
builder.Services.AddSingleton<IDiskSpace, SystemDiskSpace>();
builder.Services.AddSingleton(services => new ArchiveHealth(
    storageRoot,
    services.GetRequiredService<IDiskSpace>(),
    services.GetRequiredService<IOptions<UploadOptions>>(),
    services.GetRequiredService<IOptions<BackupOptions>>(),
    services.GetRequiredService<TimeProvider>()));

// Anyone with a contribution link can send in material, so the form is protected against automated spam.
builder.Services.Configure<ContributionOptions>(builder.Configuration.GetSection(ContributionOptions.Section));
builder.Services.AddSingleton<FormTimer>();
builder.Services.Configure<LoginOptions>(builder.Configuration.GetSection(LoginOptions.Section));
builder.Services.AddRateLimits();

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
// Write letters such as ü and ß as-is instead of as HTML entities. Razor still escapes markup characters.
builder.Services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/Admin", Policies.ManageArchive);
        options.Conventions.AuthorizeFolder("/Admin/Users", Policies.Administer);
        options.Conventions.AuthorizePage("/Admin/Export", Policies.Administer);
        options.Conventions.AuthorizePage("/Admin/Activity", Policies.Administer);
        options.Conventions.AuthorizePage("/Admin/Settings", Policies.Administer);
        options.Conventions.AuthorizePage("/Account/Password");
    })
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(options =>
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // A self-hosted archive runs as a single instance, so applying migrations at startup is safe
    // and saves operators a manual upgrade step.
    var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
    await db.Database.MigrateAsync();
    await AccentFolding.TryUseUnaccentAsync(db, app.Logger);
    await InitialAdminSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, app.Logger);
    await DemoContent.SeedAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Empty error responses, such as "not found", get a page in the visitor's language. The status code stays.
app.UseStatusCodePagesWithReExecute("/Error/{0}");
app.UseHttpsRedirection();

app.UseSecurityHeaders();

var languages = app.Services.GetRequiredService<SupportedLanguages>();
var localization = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures = languages.Codes.Select(c => new CultureInfo(c)).ToList(),
    SupportedUICultures = languages.Codes.Select(c => new CultureInfo(c)).ToList(),
};
// After the visitor's own choice and their browser's languages comes the language the group chose for its archive.
localization.RequestCultureProviders.Add(new CustomRequestCultureProvider(async context =>
    new ProviderCultureResult((await context.RequestServices.GetRequiredService<ArchiveProfile>().GetAsync()).DefaultLanguage)));
app.UseRequestLocalization(localization);

app.Use(async (context, next) =>
{
    var identity = await context.RequestServices.GetRequiredService<ArchiveProfile>().GetAsync();
    var zone = identity.TimeZoneId;
    Dates.Zone.Value = zone is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var found) ? found : null;
    DateLanguages.Current = identity.DateLanguagesFor(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
    await next(context);
});

app.UseRateLimiter();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseUploadGuard();

app.MapHealthChecks("/health");
app.MapMediaEndpoints();
app.MapDateCheck();
app.MapPublicFeeds();
app.MapBrandingEndpoints();
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
