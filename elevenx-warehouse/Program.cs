using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ElevenX.Warehouse.Components;
using ElevenX.Warehouse.Components.Account;
using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;

// เก็บ DateTime แบบ "timestamp without time zone" (ไม่บังคับ UTC) — เหมาะกับระบบภายในที่ไม่สนใจ timezone
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// QuestPDF — ใช้สัญญาอนุญาตแบบ Community (ฟรี)
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// ---------- Blazor + Razor components (Interactive Server) ----------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// ใช้หน้า login/แจ้งสิทธิ์ที่ทำธีมเอง แทน path ค่าเริ่มต้นของ Identity
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/auth/logout";
    options.AccessDeniedPath = "/access-denied";
    options.ReturnUrlParameter = "returnUrl";
});

builder.Services.AddAuthorization();

// ---------- Database (PostgreSQL via Npgsql) ----------
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

// IDbContextFactory สำหรับใช้ใน Blazor Server (service ทุกตัวสร้าง context อายุสั้นจาก factory)
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
// ลงทะเบียน scoped ApplicationDbContext (สร้างจาก factory) ให้ ASP.NET Core Identity ใช้งาน
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ---------- ASP.NET Core Identity + Roles ----------
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

// ---------- Business services ----------
builder.Services.AddScoped<CurrentUserAccessor>();
builder.Services.AddScoped<IItemService, ItemService>();
builder.Services.AddScoped<IPurchaseService, PurchaseService>();
builder.Services.AddScoped<IWithdrawalService, WithdrawalService>();
builder.Services.AddScoped<ILicenseService, LicenseService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddSingleton<IExportService, ExportService>();
builder.Services.AddScoped<ToastService>();

var app = builder.Build();

// ---------- Apply migration + seed ----------
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var dbFactory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(sp);
}

// ---------- HTTP pipeline ----------
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

// ---------- Themed login (cookie sign-in needs an HTTP context, so it runs as an endpoint) ----------
app.MapPost("/auth/login", async (
    Microsoft.AspNetCore.Identity.SignInManager<ApplicationUser> signInManager,
    [Microsoft.AspNetCore.Mvc.FromForm] string email,
    [Microsoft.AspNetCore.Mvc.FromForm] string password,
    [Microsoft.AspNetCore.Mvc.FromForm] bool? rememberMe,
    [Microsoft.AspNetCore.Mvc.FromForm] string? returnUrl) =>
{
    var result = await signInManager.PasswordSignInAsync(email ?? "", password ?? "", rememberMe ?? false, lockoutOnFailure: false);
    if (result.Succeeded)
    {
        var dest = "/dashboard";
        if (!string.IsNullOrWhiteSpace(returnUrl))
        {
            if (Uri.TryCreate(returnUrl, UriKind.Absolute, out var abs)) dest = abs.PathAndQuery;
            else if (returnUrl.StartsWith('/')) dest = returnUrl;
        }
        return Results.LocalRedirect(dest);
    }

    var reason = result.IsLockedOut ? "locked" : "invalid";
    return Results.LocalRedirect($"/login?error={reason}&returnUrl={Uri.EscapeDataString(returnUrl ?? "")}");
}).DisableAntiforgery();

app.MapPost("/auth/logout", async (Microsoft.AspNetCore.Identity.SignInManager<ApplicationUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.LocalRedirect("/login");
}).DisableAntiforgery();

app.Run();
