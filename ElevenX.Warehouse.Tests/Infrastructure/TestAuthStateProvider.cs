using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace ElevenX.Warehouse.Tests.Infrastructure;

/// <summary>
/// AuthenticationStateProvider ปลอม ใช้กำหนด role/identity ของผู้ใช้ที่ "login อยู่" ใน test
/// เพื่อทดสอบการบังคับสิทธิ์ฝั่ง server ที่ <see cref="ElevenX.Warehouse.Services.CurrentUserAccessor"/> อ่าน
/// </summary>
public sealed class TestAuthStateProvider : AuthenticationStateProvider
{
    private readonly AuthenticationState _state;

    public TestAuthStateProvider(string? userId, params string[] roles)
    {
        ClaimsIdentity identity;
        if (userId is null && roles.Length == 0)
        {
            // ผู้ใช้ที่ไม่ได้ login (anonymous)
            identity = new ClaimsIdentity();
        }
        else
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId ?? "test-user") };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            identity = new ClaimsIdentity(claims, authenticationType: "TestAuth");
        }
        _state = new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);
}
