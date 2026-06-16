using ElevenX.Warehouse.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace ElevenX.Warehouse.Services;

/// <summary>
/// อ่านผู้ใช้ที่ login อยู่จาก <see cref="AuthenticationStateProvider"/> เพื่อนำ user id ไปผูกกับทุก action
/// (PurchasedById / WithdrawnById / AssignedById) แบบอัตโนมัติ
/// </summary>
public class CurrentUserAccessor(
    AuthenticationStateProvider authProvider,
    UserManager<ApplicationUser> userManager)
{
    public async Task<string?> GetUserIdAsync()
    {
        var state = await authProvider.GetAuthenticationStateAsync();
        return userManager.GetUserId(state.User);
    }

    public async Task<ApplicationUser?> GetUserAsync()
    {
        var state = await authProvider.GetAuthenticationStateAsync();
        return await userManager.GetUserAsync(state.User);
    }

    public async Task<string> GetDisplayNameAsync()
    {
        var user = await GetUserAsync();
        return user is null ? "" : (string.IsNullOrWhiteSpace(user.FullName) ? user.UserName ?? "" : user.FullName);
    }

    /// <summary>ตรวจว่าผู้ใช้ที่ login อยู่มี role ใด role หนึ่งหรือไม่ (ใช้บังคับสิทธิ์ฝั่ง server ใน service)</summary>
    public async Task<bool> IsInAnyRoleAsync(params string[] roles)
    {
        var state = await authProvider.GetAuthenticationStateAsync();
        return roles.Any(r => state.User.IsInRole(r));
    }
}
