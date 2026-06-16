using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ElevenX.Warehouse.Data;

/// <summary>สร้าง roles และผู้ใช้ Admin เริ่มต้นเท่านั้น (ไม่ seed ข้อมูลตัวอย่าง)</summary>
public static class DbSeeder
{
    /// <summary>รหัสผ่าน dev เริ่มต้น — production ต้อง override ด้วย config "Seed:AdminPassword" (env Seed__AdminPassword)</summary>
    public const string DefaultPassword = "Passw0rd!";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var config = services.GetRequiredService<IConfiguration>();

        // Admin เริ่มต้น — อ่านจาก config เพื่อไม่ฝัง credential สาธารณะลง production
        var adminEmail = config["Seed:AdminEmail"] ?? "admin@elevenx.local";
        var adminPassword = config["Seed:AdminPassword"] ?? DefaultPassword;

        if (adminPassword == DefaultPassword)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("DbSeeder")
                .LogWarning("กำลังสร้าง/ใช้ Admin ด้วยรหัสผ่านเริ่มต้น (สาธารณะ) — ตั้ง Seed__AdminPassword ก่อนใช้งานจริง");
        }

        // 1) Roles (โครงสร้างสิทธิ์ — จำเป็นสำหรับการจัดการผู้ใช้)
        foreach (var role in AppRoles.All)
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        // 2) ผู้ใช้ Admin เริ่มต้นเท่านั้น
        await EnsureUser(userManager, adminEmail, "ผู้ดูแลระบบ", AppRoles.Admin, adminPassword);
    }

    private static async Task EnsureUser(UserManager<ApplicationUser> userManager, string email, string fullName, string role, string password)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName,
                CreatedAt = DateTime.UtcNow,
            };
            await userManager.CreateAsync(user, password);
        }
        if (!await userManager.IsInRoleAsync(user, role))
            await userManager.AddToRoleAsync(user, role);
    }
}
