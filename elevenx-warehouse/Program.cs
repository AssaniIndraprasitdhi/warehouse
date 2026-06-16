using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
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

// PaaS (Railway/Render/Fly) ส่งพอร์ตที่ต้อง bind มาทาง env "PORT" — รองรับให้ครบ
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

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

// ---------- Reverse proxy (Railway / Cloudflare) ----------
// อ่าน X-Forwarded-Proto/For เพื่อให้ scheme เป็น https ที่ถูกต้องหลัง TLS termination
// (กัน redirect loop, ทำให้ auth cookie Secure ทำงาน, และ Blazor ใช้ wss)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // proxy ของ PaaS/Cloudflare ใช้ IP ไม่แน่นอน — เคลียร์ allow-list เริ่มต้น
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

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

// ---------- Data Protection ----------
// เก็บ key ring ใน DB (ผ่าน ApplicationDbContext) — กัน user หลุด login ทุกครั้งที่ redeploy
// SetApplicationName ให้ key คงที่ข้าม instance/deploy
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>()
    .SetApplicationName("ElevenX.Warehouse");

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
// ต้องมาก่อน middleware อื่น เพื่อให้ scheme/IP ถูกแก้จาก header ก่อนนำไปใช้
app.UseForwardedHeaders();

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

// ---------- Health check (ใช้โดย Railway/Render เพื่อเช็คว่า container พร้อม) ----------
app.MapGet("/healthz", () => Results.Ok("healthy")).AllowAnonymous();

app.Run();
