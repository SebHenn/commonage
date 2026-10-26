using Commonage.Web.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// The connection string comes from user-secrets (local development) or the
// ConnectionStrings__Default environment variable (Docker). It is never stored in the repository.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Default is not configured. See docs/development.md.");

builder.Services.AddDbContext<CommonageDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHealthChecks().AddDbContextCheck<CommonageDbContext>();
builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
