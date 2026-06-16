using ElevenX.Warehouse.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Services;

public record UserRow(string Id, string Email, string FullName, string Role, DateTime CreatedAt, bool IsLockedOut);

public interface IUserService
{
    Task<List<UserRow>> GetUsersAsync();
    Task<OperationResult> CreateUserAsync(string email, string fullName, string password, string role);
    Task<OperationResult> UpdateUserAsync(string userId, string fullName, string role);
    Task<OperationResult> SetActiveAsync(string userId, bool active);
    Task<OperationResult> ResetPasswordAsync(string userId, string newPassword);
    Task<OperationResult> DeleteUserAsync(string userId);
}

public class UserService(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    CurrentUserAccessor currentUser) : IUserService
{
    private async Task<bool> IsAdminAsync() => await currentUser.IsInAnyRoleAsync(AppRoles.Admin);
    private const string Forbidden = "เฉพาะผู้ดูแลระบบเท่านั้นที่จัดการผู้ใช้ได้";

    public async Task<List<UserRow>> GetUsersAsync()
    {
        var users = await userManager.Users.OrderBy(u => u.FullName).ToListAsync();
        var rows = new List<UserRow>();
        foreach (var u in users)
        {
            var roles = await userManager.GetRolesAsync(u);
            var lockedOut = u.LockoutEnd is not null && u.LockoutEnd > DateTimeOffset.UtcNow;
            rows.Add(new UserRow(u.Id, u.Email ?? "", u.FullName, roles.FirstOrDefault() ?? "-", u.CreatedAt, lockedOut));
        }
        return rows;
    }

    public async Task<OperationResult> CreateUserAsync(string email, string fullName, string password, string role)
    {
        if (!await IsAdminAsync()) return OperationResult.Fail(Forbidden);
        email = email.Trim();
        if (!AppRoles.All.Contains(role))
            return OperationResult.Fail("Role ไม่ถูกต้อง");
        if (await userManager.FindByEmailAsync(email) is not null)
            return OperationResult.Fail("อีเมลนี้ถูกใช้งานแล้ว");

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = fullName.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
            return OperationResult.Fail(JoinErrors(created));

        await EnsureRoleExists(role);
        var roleResult = await userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
            return OperationResult.Fail(JoinErrors(roleResult));

        return OperationResult.Ok();
    }

    public async Task<OperationResult> UpdateUserAsync(string userId, string fullName, string role)
    {
        if (!await IsAdminAsync()) return OperationResult.Fail(Forbidden);
        if (!AppRoles.All.Contains(role))
            return OperationResult.Fail("Role ไม่ถูกต้อง");

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return OperationResult.Fail("ไม่พบผู้ใช้");

        var currentRoles = await userManager.GetRolesAsync(user);
        // กันถอด admin คนสุดท้าย
        if (currentRoles.Contains(AppRoles.Admin) && role != AppRoles.Admin && await IsLastAdmin(userId))
            return OperationResult.Fail("ไม่สามารถเปลี่ยน role ของผู้ดูแลระบบคนสุดท้ายได้");

        user.FullName = fullName.Trim();
        var upd = await userManager.UpdateAsync(user);
        if (!upd.Succeeded) return OperationResult.Fail(JoinErrors(upd));

        if (!currentRoles.Contains(role) || currentRoles.Count != 1)
        {
            await userManager.RemoveFromRolesAsync(user, currentRoles);
            await EnsureRoleExists(role);
            await userManager.AddToRoleAsync(user, role);
        }
        return OperationResult.Ok();
    }

    public async Task<OperationResult> SetActiveAsync(string userId, bool active)
    {
        if (!await IsAdminAsync()) return OperationResult.Fail(Forbidden);
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return OperationResult.Fail("ไม่พบผู้ใช้");

        if (!active && await userManager.IsInRoleAsync(user, AppRoles.Admin) && await IsLastAdmin(userId))
            return OperationResult.Fail("ไม่สามารถปิดการใช้งานผู้ดูแลระบบคนสุดท้ายได้");

        await userManager.SetLockoutEnabledAsync(user, true);
        var end = active ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddYears(100);
        var res = await userManager.SetLockoutEndDateAsync(user, end);
        return res.Succeeded ? OperationResult.Ok() : OperationResult.Fail(JoinErrors(res));
    }

    public async Task<OperationResult> ResetPasswordAsync(string userId, string newPassword)
    {
        if (!await IsAdminAsync()) return OperationResult.Fail(Forbidden);
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return OperationResult.Fail("ไม่พบผู้ใช้");

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var res = await userManager.ResetPasswordAsync(user, token, newPassword);
        return res.Succeeded ? OperationResult.Ok() : OperationResult.Fail(JoinErrors(res));
    }

    public async Task<OperationResult> DeleteUserAsync(string userId)
    {
        if (!await IsAdminAsync()) return OperationResult.Fail(Forbidden);
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return OperationResult.Fail("ไม่พบผู้ใช้");

        if (await userManager.IsInRoleAsync(user, AppRoles.Admin) && await IsLastAdmin(userId))
            return OperationResult.Fail("ไม่สามารถลบผู้ดูแลระบบคนสุดท้ายได้");

        try
        {
            var res = await userManager.DeleteAsync(user);
            return res.Succeeded ? OperationResult.Ok() : OperationResult.Fail(JoinErrors(res));
        }
        catch (DbUpdateException)
        {
            return OperationResult.Fail("ลบไม่ได้ เพราะผู้ใช้นี้มีประวัติการซื้อ/เบิก/License อยู่ในระบบ");
        }
    }

    private async Task<bool> IsLastAdmin(string excludingUserId)
    {
        var admins = await userManager.GetUsersInRoleAsync(AppRoles.Admin);
        return admins.Count(a => a.Id != excludingUserId) == 0;
    }

    private async Task EnsureRoleExists(string role)
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }

    private static string JoinErrors(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e => e.Description));
}
