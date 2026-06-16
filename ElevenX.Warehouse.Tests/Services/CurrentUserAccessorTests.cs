using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>
/// ครอบคลุม <see cref="CurrentUserAccessor"/> ทุก method/branch โดยสร้าง class ตรง ๆ
/// ด้วย <see cref="TestAuthStateProvider"/> เพื่อควบคุม principal เอง (ไม่ใช้ Db.Accessor)
/// </summary>
public class CurrentUserAccessorTests(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    private CurrentUserAccessor MakeAccessor(string? userId, params string[] roles)
        => new(new TestAuthStateProvider(userId, roles), Db.UserManager);

    /// <summary>สร้าง user จริงผ่าน UserManager เพื่อให้ GetUserAsync ค้นเจอจาก NameIdentifier claim</summary>
    private async Task<ApplicationUser> CreateRealUserAsync(string fullName = "สมชาย ทดสอบ", string? userName = null)
    {
        userName ??= $"{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = userName,
            FullName = fullName,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        var result = await Db.UserManager.CreateAsync(user, "Passw0rd!");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    // ===================== GetUserIdAsync =====================

    [Fact]
    public async Task GetUserIdAsync_returns_name_identifier_for_authenticated_principal()
    {
        var user = await CreateRealUserAsync();
        var accessor = MakeAccessor(user.Id);

        var id = await accessor.GetUserIdAsync();

        Assert.Equal(user.Id, id);
    }

    [Fact]
    public async Task GetUserIdAsync_returns_id_even_when_principal_has_no_roles()
    {
        // ไม่มี role แต่มี NameIdentifier => ยังถือว่า authenticated และคืน id
        var accessor = MakeAccessor("some-arbitrary-id");

        var id = await accessor.GetUserIdAsync();

        Assert.Equal("some-arbitrary-id", id);
    }

    [Fact]
    public async Task GetUserIdAsync_returns_id_even_when_user_does_not_exist_in_db()
    {
        // GetUserId อ่านจาก claim ล้วน ๆ ไม่แตะ DB จึงคืน id ที่ไม่มีจริงได้
        var accessor = MakeAccessor("ghost-id-not-in-db");

        var id = await accessor.GetUserIdAsync();

        Assert.Equal("ghost-id-not-in-db", id);
    }

    [Fact]
    public async Task GetUserIdAsync_returns_null_for_anonymous_principal()
    {
        var accessor = MakeAccessor(null);

        var id = await accessor.GetUserIdAsync();

        Assert.Null(id);
    }

    [Fact]
    public async Task GetUserIdAsync_falls_back_to_test_user_when_userId_null_but_roles_present()
    {
        // AUDIT[low]: TestAuthStateProvider ใส่ NameIdentifier="test-user" เมื่อ userId=null แต่มี roles
        //             ทำให้ "anonymous ที่มี role" กลายเป็น user ปลอมชื่อ test-user (ข้อจำกัดของ test double, ไม่ใช่บั๊กของ app)
        var accessor = MakeAccessor(null, AppRoles.Admin);

        var id = await accessor.GetUserIdAsync();

        Assert.Equal("test-user", id);
    }

    // ===================== GetUserAsync =====================

    [Fact]
    public async Task GetUserAsync_returns_matching_user_for_authenticated_principal()
    {
        var user = await CreateRealUserAsync(fullName: "ผู้ใช้จริง");
        var accessor = MakeAccessor(user.Id);

        var fetched = await accessor.GetUserAsync();

        Assert.NotNull(fetched);
        Assert.Equal(user.Id, fetched!.Id);
        Assert.Equal("ผู้ใช้จริง", fetched.FullName);
    }

    [Fact]
    public async Task GetUserAsync_returns_null_for_anonymous_principal()
    {
        var accessor = MakeAccessor(null);

        var fetched = await accessor.GetUserAsync();

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetUserAsync_returns_null_when_claim_points_to_nonexistent_user()
    {
        // มี NameIdentifier claim แต่ DB ไม่มี user นั้น => GetUserAsync คืน null
        var accessor = MakeAccessor("id-that-was-never-created");

        var fetched = await accessor.GetUserAsync();

        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetUserAsync_resolves_correct_user_among_several()
    {
        await CreateRealUserAsync(fullName: "คนแรก");
        var target = await CreateRealUserAsync(fullName: "คนที่สอง");
        await CreateRealUserAsync(fullName: "คนที่สาม");
        var accessor = MakeAccessor(target.Id);

        var fetched = await accessor.GetUserAsync();

        Assert.NotNull(fetched);
        Assert.Equal(target.Id, fetched!.Id);
        Assert.Equal("คนที่สอง", fetched.FullName);
    }

    // ===================== GetDisplayNameAsync =====================

    [Fact]
    public async Task GetDisplayNameAsync_returns_full_name_when_present()
    {
        var user = await CreateRealUserAsync(fullName: "ชื่อเต็มแสดงผล");
        var accessor = MakeAccessor(user.Id);

        var name = await accessor.GetDisplayNameAsync();

        Assert.Equal("ชื่อเต็มแสดงผล", name);
    }

    [Fact]
    public async Task GetDisplayNameAsync_falls_back_to_username_when_fullname_empty()
    {
        var user = await CreateRealUserAsync(fullName: "", userName: "fallback-user@test.local");
        var accessor = MakeAccessor(user.Id);

        var name = await accessor.GetDisplayNameAsync();

        Assert.Equal("fallback-user@test.local", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task GetDisplayNameAsync_treats_whitespace_fullname_as_blank_and_uses_username(string blankFullName)
    {
        var user = await CreateRealUserAsync(fullName: blankFullName, userName: "ws-user@test.local");
        var accessor = MakeAccessor(user.Id);

        var name = await accessor.GetDisplayNameAsync();

        Assert.Equal("ws-user@test.local", name);
    }

    [Fact]
    public async Task GetDisplayNameAsync_returns_empty_string_for_anonymous_principal()
    {
        var accessor = MakeAccessor(null);

        var name = await accessor.GetDisplayNameAsync();

        Assert.Equal("", name);
    }

    [Fact]
    public async Task GetDisplayNameAsync_returns_empty_string_when_user_not_found()
    {
        var accessor = MakeAccessor("missing-user-id");

        var name = await accessor.GetDisplayNameAsync();

        Assert.Equal("", name);
    }

    [Fact]
    public async Task GetDisplayNameAsync_prefers_fullname_over_username_when_both_set()
    {
        var user = await CreateRealUserAsync(fullName: "ชื่อจริง", userName: "login@test.local");
        var accessor = MakeAccessor(user.Id);

        var name = await accessor.GetDisplayNameAsync();

        Assert.Equal("ชื่อจริง", name);
        Assert.NotEqual("login@test.local", name);
    }

    // ===================== IsInAnyRoleAsync =====================

    [Fact]
    public async Task IsInAnyRoleAsync_true_when_principal_has_the_single_requested_role()
    {
        var accessor = MakeAccessor("u1", AppRoles.Admin);

        Assert.True(await accessor.IsInAnyRoleAsync(AppRoles.Admin));
    }

    [Fact]
    public async Task IsInAnyRoleAsync_true_when_principal_has_one_of_several_requested_roles()
    {
        var accessor = MakeAccessor("u1", AppRoles.Purchaser);

        Assert.True(await accessor.IsInAnyRoleAsync(AppRoles.Admin, AppRoles.Purchaser, AppRoles.Staff));
    }

    [Fact]
    public async Task IsInAnyRoleAsync_true_when_principal_holds_multiple_roles_and_one_matches()
    {
        var accessor = MakeAccessor("u1", AppRoles.Staff, AppRoles.Viewer);

        Assert.True(await accessor.IsInAnyRoleAsync(AppRoles.Viewer));
    }

    [Fact]
    public async Task IsInAnyRoleAsync_false_when_principal_lacks_all_requested_roles()
    {
        var accessor = MakeAccessor("u1", AppRoles.Viewer);

        Assert.False(await accessor.IsInAnyRoleAsync(AppRoles.Admin, AppRoles.Purchaser));
    }

    [Fact]
    public async Task IsInAnyRoleAsync_false_for_anonymous_principal()
    {
        var accessor = MakeAccessor(null);

        Assert.False(await accessor.IsInAnyRoleAsync(AppRoles.Admin, AppRoles.Staff));
    }

    [Fact]
    public async Task IsInAnyRoleAsync_false_when_principal_has_no_roles()
    {
        var accessor = MakeAccessor("u1");

        Assert.False(await accessor.IsInAnyRoleAsync(AppRoles.Admin));
    }

    [Fact]
    public async Task IsInAnyRoleAsync_false_when_no_roles_requested()
    {
        // เรียกด้วย array ว่าง => Any บน empty => false แม้ผู้ใช้จะมี role
        var accessor = MakeAccessor("u1", AppRoles.Admin);

        Assert.False(await accessor.IsInAnyRoleAsync());
    }

    [Fact]
    public async Task IsInAnyRoleAsync_is_case_sensitive_on_role_name()
    {
        // AUDIT[low]: IsInRole เทียบชื่อ role แบบ case-sensitive ตาม claim — "admin" (พิมพ์เล็ก)
        //             ไม่ตรงกับ "ADMIN" ที่เก็บใน claim ทำให้คืน false; ถ้ามีจุดเรียกที่ส่งชื่อ role ผิด case จะเงียบ ๆ ปฏิเสธสิทธิ์
        var accessor = MakeAccessor("u1", AppRoles.Admin);

        Assert.False(await accessor.IsInAnyRoleAsync("admin"));
        Assert.True(await accessor.IsInAnyRoleAsync("ADMIN"));
    }

    [Fact]
    public async Task IsInAnyRoleAsync_works_independently_of_db_user_existence()
    {
        // IsInAnyRoleAsync อ่านจาก claim เท่านั้น ไม่แตะ DB จึง true ได้แม้ user ไม่มีใน DB
        var accessor = MakeAccessor("ghost", AppRoles.Staff);

        Assert.True(await accessor.IsInAnyRoleAsync(AppRoles.Staff));
    }
}
