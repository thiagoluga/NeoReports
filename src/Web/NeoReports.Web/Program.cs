using NeoReports.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Builder-wizard state, shared across the 5 steps (scoped = per circuit).
builder.Services.AddScoped<BuilderState>();

var app = builder.Build();

// Mount the whole app under /neoreports — it is "mountable" as the spec asks.
// Must run before routing/static files so the prefix is stripped first.
// In production, prefer hosting as a Razor Class Library and applying
// UsePathBase in the real host.
app.UsePathBase("/neoreports");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
