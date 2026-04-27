using KrakenDeploy.Server.Components;
using KrakenDeploy.Server.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("KrakenDb")
    ?? throw new InvalidOperationException(
        "Connection string 'KrakenDb' is not configured. " +
        "Set ConnectionStrings:KrakenDb in appsettings.{Environment}.json or via user-secrets.");

builder.Services.AddKrakenDeployData(connectionString);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<KrakenDbContext>();
    await db.Database.MigrateAsync();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
