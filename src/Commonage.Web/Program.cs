using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Commonage.Web;
using Commonage.Web.Data;
using Commonage.Web.Identity;
using Commonage.Web.Media;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.WebEncoders;

var builder = WebApplication.CreateBuilder(args);

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
    })
    .AddEntityFrameworkStores<CommonageDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/Login";
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.ManageArchive, policy => policy.RequireRole(Roles.Admin, Roles.Curator));

// Uploaded originals and their previews. In Docker this directory is a persistent volume.
var storageRoot = Path.GetFullPath(
    builder.Configuration["Storage:RootPath"] ?? Path.Combine("App_Data", "media"), builder.Environment.ContentRootPath);
builder.Services.AddSingleton<IFileStorage>(new LocalFileStorage(storageRoot));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Uploads"));
builder.Services.AddScoped<MediaUploadService>();
builder.Services.AddSingleton<MediaProcessingQueue>();
builder.Services.AddHostedService<MediaProcessor>();

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
// Write letters such as ü and ß as-is instead of as HTML entities. Razor still escapes markup characters.
builder.Services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/Admin", Policies.ManageArchive))
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(options =>
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // A self-hosted archive runs as a single instance, so applying migrations at startup is safe
    // and saves operators a manual upgrade step.
    await scope.ServiceProvider.GetRequiredService<CommonageDbContext>().Database.MigrateAsync();
    await InitialAdminSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Browsers must not guess content types, so an uploaded file can never be interpreted as a script or page.
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next();
});

string[] supportedCultures = ["en", "de"];
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures = supportedCultures.Select(c => new CultureInfo(c)).ToList(),
    SupportedUICultures = supportedCultures.Select(c => new CultureInfo(c)).ToList(),
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapMediaEndpoints();
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
