using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>
/// ครอบคลุม UserService (Identity-backed user management): GetUsersAsync, CreateUserAsync,
/// UpdateUserAsync, SetActiveAsync, ResetPasswordAsync, DeleteUserAsync
/// รวมถึง guard "admin only", last-admin protection และ edge case ต่าง ๆ
/// </summary>
public class UserServiceTests(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    private const string ValidPassword = "Passw0rd!";
    private const string WeakPassword = "123";

    // service ของ admin (สิทธิ์เต็ม)
    private UserService AdminSvc() => new(Db.UserManager, Db.RoleManager, Db.Accessor(AppRoles.Admin));

    // service ของผู้ใช้ที่ไม่ใช่ admin
    private UserService PurchaserSvc() => new(Db.UserManager, Db.RoleManager, Db.Accessor(AppRoles.Purchaser));

    private UserService AnonymousSvc() => new(Db.UserManager, Db.RoleManager, Db.Anonymous());

    /// <summary>สร้างผู้ใช้ผ่าน Identity โดยตรง (ให้ Identity consistent) แล้วใส่ role ที่กำหนด</summary>
    private async Task<ApplicationUser> SeedIdentityUserAsync(string role, string fullName = "ผู้ใช้", string? email = null, string password = ValidPassword)
    {
        email ??= $"{Guid.NewGuid():N}@seed.local";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = fullName,
            CreatedAt = DateTime.UtcNow,
        };
        var created = await Db.UserManager.CreateAsync(user, password);
        Assert.True(created.Succeeded, string.Join(" ", created.Errors.Select(e => e.Description)));
        var roleResult = await Db.UserManager.AddToRoleAsync(user, role);
        Assert.True(roleResult.Succeeded, string.Join(" ", roleResult.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task<ApplicationUser> ReloadAsync(string id)
    {
        // โหลดใหม่จาก context สด เพื่อไม่ติด tracking cache ของ UserManager
        await using var ctx = Db.NewContext();
        var u = await Db.UserManager.FindByIdAsync(id);
        Assert.NotNull(u);
        return u!;
    }

    // ===================== GetUsersAsync =====================

    [Fact]
    public async Task GetUsersAsync_returns_empty_when_no_users()
    {
        var svc = AdminSvc();
        var rows = await svc.GetUsersAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetUsersAsync_returns_user_with_role_email_and_not_locked()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff, fullName: "สมชาย", email: "somchai@seed.local");
        var svc = AdminSvc();

        var rows = await svc.GetUsersAsync();

        var row = Assert.Single(rows);
        Assert.Equal(user.Id, row.Id);
        Assert.Equal("somchai@seed.local", row.Email);
        Assert.Equal("สมชาย", row.FullName);
        Assert.Equal(AppRoles.Staff, row.Role);
        Assert.False(row.IsLockedOut);
    }

    [Fact]
    public async Task GetUsersAsync_orders_by_fullName_ascending()
    {
        await SeedIdentityUserAsync(AppRoles.Staff, fullName: "Zebra");
        await SeedIdentityUserAsync(AppRoles.Staff, fullName: "Apple");
        await SeedIdentityUserAsync(AppRoles.Staff, fullName: "Mango");
        var svc = AdminSvc();

        var rows = await svc.GetUsersAsync();

        Assert.Equal(new[] { "Apple", "Mango", "Zebra" }, rows.Select(r => r.FullName).ToArray());
    }

    [Fact]
    public async Task GetUsersAsync_reports_locked_out_after_SetActive_false()
    {
        // มี admin อีกคนเพื่อให้ปิดผู้ใช้เป้าหมายได้ (ไม่ใช่ admin คนสุดท้าย)
        var target = await SeedIdentityUserAsync(AppRoles.Staff, fullName: "ผู้ถูกล็อก");
        var svc = AdminSvc();

        var disable = await svc.SetActiveAsync(target.Id, active: false);
        Assert.True(disable.Success, disable.Error);

        var rows = await svc.GetUsersAsync();
        var row = Assert.Single(rows, r => r.Id == target.Id);
        Assert.True(row.IsLockedOut);
    }

    [Fact]
    public async Task GetUsersAsync_shows_dash_role_when_user_has_no_role()
    {
        // เพิ่มผู้ใช้ตรง ๆ ผ่าน DbContext โดยไม่ผูก role
        var user = await Db.AddUserAsync(fullName: "ไร้บทบาท");
        var svc = AdminSvc();

        var rows = await svc.GetUsersAsync();

        var row = Assert.Single(rows, r => r.Id == user.Id);
        Assert.Equal("-", row.Role);
    }

    // AUDIT[low]: GetUsersAsync ไม่มี guard สิทธิ์เลย — ผู้ใช้ anonymous ก็เรียกดูรายชื่อ/อีเมลผู้ใช้ทั้งหมดได้
    [Fact]
    public async Task GetUsersAsync_has_no_permission_guard_anonymous_can_read_all_users()
    {
        await SeedIdentityUserAsync(AppRoles.Admin, fullName: "แอดมิน");
        var svc = AnonymousSvc();

        var rows = await svc.GetUsersAsync();

        Assert.Single(rows);
    }

    // ===================== CreateUserAsync =====================

    [Fact]
    public async Task CreateUserAsync_succeeds_for_admin_and_sets_role_and_emailconfirmed()
    {
        var svc = AdminSvc();

        var result = await svc.CreateUserAsync("new@user.local", "ผู้ใช้ใหม่", ValidPassword, AppRoles.Staff);

        Assert.True(result.Success, result.Error);
        var created = await Db.UserManager.FindByEmailAsync("new@user.local");
        Assert.NotNull(created);
        Assert.True(created!.EmailConfirmed);
        Assert.Equal("ผู้ใช้ใหม่", created.FullName);
        Assert.Equal("new@user.local", created.UserName);
        Assert.Contains(AppRoles.Staff, await Db.UserManager.GetRolesAsync(created));
    }

    [Fact]
    public async Task CreateUserAsync_forbidden_for_purchaser()
    {
        var svc = PurchaserSvc();

        var result = await svc.CreateUserAsync("x@user.local", "x", ValidPassword, AppRoles.Staff);

        Assert.False(result.Success);
        Assert.Equal("เฉพาะผู้ดูแลระบบเท่านั้นที่จัดการผู้ใช้ได้", result.Error);
        Assert.Null(await Db.UserManager.FindByEmailAsync("x@user.local"));
    }

    [Fact]
    public async Task CreateUserAsync_forbidden_for_anonymous()
    {
        var svc = AnonymousSvc();

        var result = await svc.CreateUserAsync("x@user.local", "x", ValidPassword, AppRoles.Staff);

        Assert.False(result.Success);
        Assert.Equal("เฉพาะผู้ดูแลระบบเท่านั้นที่จัดการผู้ใช้ได้", result.Error);
    }

    [Fact]
    public async Task CreateUserAsync_rejects_invalid_role()
    {
        var svc = AdminSvc();

        var result = await svc.CreateUserAsync("x@user.local", "x", ValidPassword, "NOT_A_ROLE");

        Assert.False(result.Success);
        Assert.Equal("Role ไม่ถูกต้อง", result.Error);
        Assert.Null(await Db.UserManager.FindByEmailAsync("x@user.local"));
    }

    [Fact]
    public async Task CreateUserAsync_rejects_duplicate_email()
    {
        await SeedIdentityUserAsync(AppRoles.Staff, email: "dup@user.local");
        var svc = AdminSvc();

        var result = await svc.CreateUserAsync("dup@user.local", "ใหม่", ValidPassword, AppRoles.Staff);

        Assert.False(result.Success);
        Assert.Equal("อีเมลนี้ถูกใช้งานแล้ว", result.Error);
    }

    [Fact]
    public async Task CreateUserAsync_trims_email_before_duplicate_check_and_storage()
    {
        await SeedIdentityUserAsync(AppRoles.Staff, email: "trim@user.local");
        var svc = AdminSvc();

        // อีเมลเดิมแต่มี whitespace ห่อ — ควรถูก trim แล้วถือว่าซ้ำ
        var result = await svc.CreateUserAsync("  trim@user.local  ", "ใหม่", ValidPassword, AppRoles.Staff);

        Assert.False(result.Success);
        Assert.Equal("อีเมลนี้ถูกใช้งานแล้ว", result.Error);
    }

    [Fact]
    public async Task CreateUserAsync_trims_email_on_success()
    {
        var svc = AdminSvc();

        var result = await svc.CreateUserAsync("  spaced@user.local  ", "ผู้ใช้", ValidPassword, AppRoles.Staff);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(await Db.UserManager.FindByEmailAsync("spaced@user.local"));
    }

    [Fact]
    public async Task CreateUserAsync_trims_fullName()
    {
        var svc = AdminSvc();

        var result = await svc.CreateUserAsync("ft@user.local", "  ชื่อเต็ม  ", ValidPassword, AppRoles.Staff);

        Assert.True(result.Success, result.Error);
        var u = await Db.UserManager.FindByEmailAsync("ft@user.local");
        Assert.Equal("ชื่อเต็ม", u!.FullName);
    }

    [Fact]
    public async Task CreateUserAsync_weak_password_fails_with_joined_identity_errors()
    {
        var svc = AdminSvc();

        var result = await svc.CreateUserAsync("weak@user.local", "x", WeakPassword, AppRoles.Staff);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        // ข้อความ error เป็นภาษาอังกฤษจาก Identity (joined) ไม่ใช่ข้อความ custom
        Assert.NotEqual("Role ไม่ถูกต้อง", result.Error);
        Assert.NotEqual("อีเมลนี้ถูกใช้งานแล้ว", result.Error);
        // ไม่มี user ถูกสร้าง
        Assert.Null(await Db.UserManager.FindByEmailAsync("weak@user.local"));
    }

    [Theory]
    [InlineData(AppRoles.Admin)]
    [InlineData(AppRoles.Purchaser)]
    [InlineData(AppRoles.Staff)]
    [InlineData(AppRoles.Viewer)]
    public async Task CreateUserAsync_accepts_each_valid_role(string role)
    {
        var svc = AdminSvc();
        var email = $"{role}@valid.local";

        var result = await svc.CreateUserAsync(email, "x", ValidPassword, role);

        Assert.True(result.Success, result.Error);
        var u = await Db.UserManager.FindByEmailAsync(email);
        Assert.Contains(role, await Db.UserManager.GetRolesAsync(u!));
    }

    // ===================== UpdateUserAsync =====================

    [Fact]
    public async Task UpdateUserAsync_forbidden_for_non_admin()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff, fullName: "เดิม");
        var svc = PurchaserSvc();

        var result = await svc.UpdateUserAsync(user.Id, "ใหม่", AppRoles.Viewer);

        Assert.False(result.Success);
        Assert.Equal("เฉพาะผู้ดูแลระบบเท่านั้นที่จัดการผู้ใช้ได้", result.Error);
        var reloaded = await ReloadAsync(user.Id);
        Assert.Equal("เดิม", reloaded.FullName);
    }

    [Fact]
    public async Task UpdateUserAsync_rejects_invalid_role()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff);
        var svc = AdminSvc();

        var result = await svc.UpdateUserAsync(user.Id, "ใหม่", "BOGUS");

        Assert.False(result.Success);
        Assert.Equal("Role ไม่ถูกต้อง", result.Error);
    }

    [Fact]
    public async Task UpdateUserAsync_user_not_found()
    {
        var svc = AdminSvc();

        var result = await svc.UpdateUserAsync("does-not-exist", "ใหม่", AppRoles.Staff);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบผู้ใช้", result.Error);
    }

    [Fact]
    public async Task UpdateUserAsync_updates_fullName()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff, fullName: "เดิม");
        var svc = AdminSvc();

        var result = await svc.UpdateUserAsync(user.Id, "ชื่อใหม่", AppRoles.Staff);

        Assert.True(result.Success, result.Error);
        var reloaded = await ReloadAsync(user.Id);
        Assert.Equal("ชื่อใหม่", reloaded.FullName);
        // role เดิมยังอยู่
        Assert.Contains(AppRoles.Staff, await Db.UserManager.GetRolesAsync(reloaded));
    }

    [Fact]
    public async Task UpdateUserAsync_trims_fullName()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff, fullName: "เดิม");
        var svc = AdminSvc();

        var result = await svc.UpdateUserAsync(user.Id, "  ชื่อมีช่องว่าง  ", AppRoles.Staff);

        Assert.True(result.Success, result.Error);
        var reloaded = await ReloadAsync(user.Id);
        Assert.Equal("ชื่อมีช่องว่าง", reloaded.FullName);
    }

    [Fact]
    public async Task UpdateUserAsync_changes_role()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff, fullName: "ก");
        var svc = AdminSvc();

        var result = await svc.UpdateUserAsync(user.Id, "ก", AppRoles.Purchaser);

        Assert.True(result.Success, result.Error);
        var reloaded = await ReloadAsync(user.Id);
        var roles = await Db.UserManager.GetRolesAsync(reloaded);
        Assert.Contains(AppRoles.Purchaser, roles);
        Assert.DoesNotContain(AppRoles.Staff, roles);
        Assert.Single(roles);
    }

    [Fact]
    public async Task UpdateUserAsync_blocks_demoting_the_last_admin()
    {
        var onlyAdmin = await SeedIdentityUserAsync(AppRoles.Admin, fullName: "เดียว");
        var svc = AdminSvc();

        var result = await svc.UpdateUserAsync(onlyAdmin.Id, "เดียว", AppRoles.Staff);

        Assert.False(result.Success);
        Assert.Equal("ไม่สามารถเปลี่ยน role ของผู้ดูแลระบบคนสุดท้ายได้", result.Error);
        // ยังเป็น admin อยู่
        var reloaded = await ReloadAsync(onlyAdmin.Id);
        Assert.Contains(AppRoles.Admin, await Db.UserManager.GetRolesAsync(reloaded));
    }

    [Fact]
    public async Task UpdateUserAsync_allows_demoting_admin_when_another_admin_exists()
    {
        var admin1 = await SeedIdentityUserAsync(AppRoles.Admin, fullName: "แอดมินหนึ่ง");
        await SeedIdentityUserAsync(AppRoles.Admin, fullName: "แอดมินสอง");
        var svc = AdminSvc();

        var result = await svc.UpdateUserAsync(admin1.Id, "แอดมินหนึ่ง", AppRoles.Staff);

        Assert.True(result.Success, result.Error);
        var reloaded = await ReloadAsync(admin1.Id);
        var roles = await Db.UserManager.GetRolesAsync(reloaded);
        Assert.Contains(AppRoles.Staff, roles);
        Assert.DoesNotContain(AppRoles.Admin, roles);
    }

    [Fact]
    public async Task UpdateUserAsync_keeping_last_admin_as_admin_is_allowed_and_updates_name()
    {
        // เปลี่ยนเฉพาะชื่อของ admin คนสุดท้าย โดย role ยังเป็น Admin — ไม่ควรถูกบล็อก
        var onlyAdmin = await SeedIdentityUserAsync(AppRoles.Admin, fullName: "เดิม");
        var svc = AdminSvc();

        var result = await svc.UpdateUserAsync(onlyAdmin.Id, "ชื่อใหม่", AppRoles.Admin);

        Assert.True(result.Success, result.Error);
        var reloaded = await ReloadAsync(onlyAdmin.Id);
        Assert.Equal("ชื่อใหม่", reloaded.FullName);
        Assert.Contains(AppRoles.Admin, await Db.UserManager.GetRolesAsync(reloaded));
    }

    [Fact]
    public async Task UpdateUserAsync_assigns_role_when_user_had_none()
    {
        // ผู้ใช้ที่ไม่มี role เลย -> update ควรเพิ่ม role ใหม่ให้ (currentRoles.Count != 1 => true)
        var user = await Db.AddUserAsync(fullName: "ไร้บทบาท");
        var svc = AdminSvc();

        var result = await svc.UpdateUserAsync(user.Id, "ไร้บทบาท", AppRoles.Viewer);

        Assert.True(result.Success, result.Error);
        var reloaded = await ReloadAsync(user.Id);
        Assert.Equal(new[] { AppRoles.Viewer }, (await Db.UserManager.GetRolesAsync(reloaded)).ToArray());
    }

    // ===================== SetActiveAsync =====================

    [Fact]
    public async Task SetActiveAsync_forbidden_for_non_admin()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff);
        var svc = PurchaserSvc();

        var result = await svc.SetActiveAsync(user.Id, active: false);

        Assert.False(result.Success);
        Assert.Equal("เฉพาะผู้ดูแลระบบเท่านั้นที่จัดการผู้ใช้ได้", result.Error);
    }

    [Fact]
    public async Task SetActiveAsync_user_not_found()
    {
        var svc = AdminSvc();

        var result = await svc.SetActiveAsync("missing", active: false);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบผู้ใช้", result.Error);
    }

    [Fact]
    public async Task SetActiveAsync_disable_blocks_last_admin()
    {
        var onlyAdmin = await SeedIdentityUserAsync(AppRoles.Admin);
        var svc = AdminSvc();

        var result = await svc.SetActiveAsync(onlyAdmin.Id, active: false);

        Assert.False(result.Success);
        Assert.Equal("ไม่สามารถปิดการใช้งานผู้ดูแลระบบคนสุดท้ายได้", result.Error);
        var reloaded = await ReloadAsync(onlyAdmin.Id);
        Assert.Null(reloaded.LockoutEnd);
    }

    [Fact]
    public async Task SetActiveAsync_disable_allowed_when_other_admin_exists()
    {
        var admin1 = await SeedIdentityUserAsync(AppRoles.Admin, fullName: "หนึ่ง");
        await SeedIdentityUserAsync(AppRoles.Admin, fullName: "สอง");
        var svc = AdminSvc();

        var result = await svc.SetActiveAsync(admin1.Id, active: false);

        Assert.True(result.Success, result.Error);
        var reloaded = await ReloadAsync(admin1.Id);
        Assert.NotNull(reloaded.LockoutEnd);
        Assert.True(reloaded.LockoutEnd > DateTimeOffset.UtcNow.AddYears(50));
    }

    [Fact]
    public async Task SetActiveAsync_disable_sets_far_future_lockout_and_enables_lockout_flag()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff);
        var svc = AdminSvc();

        var result = await svc.SetActiveAsync(user.Id, active: false);

        Assert.True(result.Success, result.Error);
        var reloaded = await ReloadAsync(user.Id);
        Assert.True(reloaded.LockoutEnabled);
        Assert.NotNull(reloaded.LockoutEnd);
        Assert.True(reloaded.LockoutEnd > DateTimeOffset.UtcNow.AddYears(99));
    }

    [Fact]
    public async Task SetActiveAsync_enable_clears_lockout()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff);
        var svc = AdminSvc();

        var disable = await svc.SetActiveAsync(user.Id, active: false);
        Assert.True(disable.Success, disable.Error);

        var enable = await svc.SetActiveAsync(user.Id, active: true);
        Assert.True(enable.Success, enable.Error);

        var reloaded = await ReloadAsync(user.Id);
        Assert.Null(reloaded.LockoutEnd);
        var rows = await svc.GetUsersAsync();
        Assert.False(Assert.Single(rows, r => r.Id == user.Id).IsLockedOut);
    }

    // AUDIT[low]: SetActiveAsync(active:true) บน admin คนสุดท้าย "ผ่าน" (ไม่เข้าเงื่อนไข last-admin)
    // แต่จริง ๆ เป็น no-op ที่ปลอดภัย — บันทึกไว้เป็น edge case
    [Fact]
    public async Task SetActiveAsync_enable_last_admin_is_allowed()
    {
        var onlyAdmin = await SeedIdentityUserAsync(AppRoles.Admin);
        var svc = AdminSvc();

        var result = await svc.SetActiveAsync(onlyAdmin.Id, active: true);

        Assert.True(result.Success, result.Error);
    }

    // ===================== ResetPasswordAsync =====================

    [Fact]
    public async Task ResetPasswordAsync_forbidden_for_non_admin()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff);
        var svc = PurchaserSvc();

        var result = await svc.ResetPasswordAsync(user.Id, "NewPassw0rd!");

        Assert.False(result.Success);
        Assert.Equal("เฉพาะผู้ดูแลระบบเท่านั้นที่จัดการผู้ใช้ได้", result.Error);
        // รหัสเดิมยังใช้ได้
        var reloaded = await ReloadAsync(user.Id);
        Assert.True(await Db.UserManager.CheckPasswordAsync(reloaded, ValidPassword));
    }

    [Fact]
    public async Task ResetPasswordAsync_user_not_found()
    {
        var svc = AdminSvc();

        var result = await svc.ResetPasswordAsync("nope", "NewPassw0rd!");

        Assert.False(result.Success);
        Assert.Equal("ไม่พบผู้ใช้", result.Error);
    }

    [Fact]
    public async Task ResetPasswordAsync_success_changes_password()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff, password: ValidPassword);
        var svc = AdminSvc();

        var result = await svc.ResetPasswordAsync(user.Id, "Changed1!");

        Assert.True(result.Success, result.Error);
        var reloaded = await ReloadAsync(user.Id);
        Assert.True(await Db.UserManager.CheckPasswordAsync(reloaded, "Changed1!"));
        Assert.False(await Db.UserManager.CheckPasswordAsync(reloaded, ValidPassword));
    }

    [Fact]
    public async Task ResetPasswordAsync_weak_password_fails_and_keeps_old_password()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff, password: ValidPassword);
        var svc = AdminSvc();

        var result = await svc.ResetPasswordAsync(user.Id, WeakPassword);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        var reloaded = await ReloadAsync(user.Id);
        Assert.True(await Db.UserManager.CheckPasswordAsync(reloaded, ValidPassword));
    }

    // ===================== DeleteUserAsync =====================

    [Fact]
    public async Task DeleteUserAsync_forbidden_for_non_admin()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff);
        var svc = PurchaserSvc();

        var result = await svc.DeleteUserAsync(user.Id);

        Assert.False(result.Success);
        Assert.Equal("เฉพาะผู้ดูแลระบบเท่านั้นที่จัดการผู้ใช้ได้", result.Error);
        Assert.NotNull(await Db.UserManager.FindByIdAsync(user.Id));
    }

    [Fact]
    public async Task DeleteUserAsync_user_not_found()
    {
        var svc = AdminSvc();

        var result = await svc.DeleteUserAsync("ghost");

        Assert.False(result.Success);
        Assert.Equal("ไม่พบผู้ใช้", result.Error);
    }

    [Fact]
    public async Task DeleteUserAsync_blocks_deleting_last_admin()
    {
        var onlyAdmin = await SeedIdentityUserAsync(AppRoles.Admin);
        var svc = AdminSvc();

        var result = await svc.DeleteUserAsync(onlyAdmin.Id);

        Assert.False(result.Success);
        Assert.Equal("ไม่สามารถลบผู้ดูแลระบบคนสุดท้ายได้", result.Error);
        Assert.NotNull(await Db.UserManager.FindByIdAsync(onlyAdmin.Id));
    }

    [Fact]
    public async Task DeleteUserAsync_allows_deleting_admin_when_another_admin_exists()
    {
        var admin1 = await SeedIdentityUserAsync(AppRoles.Admin, fullName: "หนึ่ง");
        await SeedIdentityUserAsync(AppRoles.Admin, fullName: "สอง");
        var svc = AdminSvc();

        var result = await svc.DeleteUserAsync(admin1.Id);

        Assert.True(result.Success, result.Error);
        Assert.Null(await Db.UserManager.FindByIdAsync(admin1.Id));
    }

    [Fact]
    public async Task DeleteUserAsync_removes_normal_user()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff);
        var svc = AdminSvc();

        var result = await svc.DeleteUserAsync(user.Id);

        Assert.True(result.Success, result.Error);
        Assert.Null(await Db.UserManager.FindByIdAsync(user.Id));
        Assert.Empty(await svc.GetUsersAsync());
    }

    // AUDIT[medium]: DeleteUserAsync จับ DbUpdateException เพื่อกันลบผู้ใช้ที่มีประวัติ FK
    // แต่ Purchase/Withdrawal/LicenseAssignment อ้าง user ผ่าน FK — ถ้า cascade เป็น Restrict
    // การลบควรล้มเหลวด้วยข้อความนี้ ทดสอบว่าผู้ใช้ที่มีประวัติการซื้อถูกป้องกันหรือถูกลบ (บันทึกพฤติกรรมจริง)
    [Fact]
    public async Task DeleteUserAsync_with_purchase_history_behavior()
    {
        var user = await SeedIdentityUserAsync(AppRoles.Staff);
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id);
        var svc = AdminSvc();

        var result = await svc.DeleteUserAsync(user.Id);

        if (result.Success)
        {
            // ถ้า cascade ลบประวัติไปด้วย -> ผู้ใช้หายจริง
            Assert.Null(await Db.UserManager.FindByIdAsync(user.Id));
        }
        else
        {
            // ถ้า FK เป็น Restrict -> ข้อความเตือนเรื่องประวัติ
            Assert.Equal("ลบไม่ได้ เพราะผู้ใช้นี้มีประวัติการซื้อ/เบิก/License อยู่ในระบบ", result.Error);
            Assert.NotNull(await Db.UserManager.FindByIdAsync(user.Id));
        }
    }
}
