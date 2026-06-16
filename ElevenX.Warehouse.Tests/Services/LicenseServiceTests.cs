using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>
/// ครอบคลุม LicenseService (จ่าย/คืน seat + การรายงานการใช้งาน) ทุก function และทุก branch
/// บน PostgreSQL จริง (partial unique index IX_active_seat บังคับใช้จริง, ILike case-insensitive ใช้ได้)
/// </summary>
public class LicenseServiceTests(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    private LicenseService NewService(CurrentUserAccessor accessor) => new(Db.Factory, accessor);
    private LicenseService AdminService() => NewService(Db.Accessor(AppRoles.Admin));
    private LicenseService PurchaserService() => NewService(Db.Accessor(AppRoles.Purchaser));

    // ============================================================
    // AssignSeatAsync — permission
    // ============================================================

    [Fact]
    public async Task AssignSeat_admin_can_assign()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assignee = await Db.AddUserAsync("คนรับ");
        var assigner = await Db.AddUserAsync("คนจ่าย");

        var result = await AdminService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, "note");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        Assert.Equal(item.Id, result.Value!.ItemId);
        Assert.Equal(assignee.Id, result.Value.AssignedToId);
        Assert.Equal(assigner.Id, result.Value.AssignedById);
        Assert.Equal("note", result.Value.Note);
        Assert.Null(result.Value.ReleasedAt);
        Assert.True(result.Value.IsActive);
    }

    [Fact]
    public async Task AssignSeat_purchaser_can_assign()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 3);
        var assignee = await Db.AddUserAsync();
        var assigner = await Db.AddUserAsync();

        var result = await PurchaserService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        Assert.Null(result.Value!.Note);
    }

    [Fact]
    public async Task AssignSeat_staff_is_forbidden()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assignee = await Db.AddUserAsync();
        var assigner = await Db.AddUserAsync();

        var result = await NewService(Db.Accessor(AppRoles.Staff)).AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.LicenseAssignments));
    }

    [Fact]
    public async Task AssignSeat_viewer_is_forbidden()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assignee = await Db.AddUserAsync();
        var assigner = await Db.AddUserAsync();

        var result = await NewService(Db.Accessor(AppRoles.Viewer)).AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task AssignSeat_anonymous_is_forbidden()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assignee = await Db.AddUserAsync();
        var assigner = await Db.AddUserAsync();

        var result = await NewService(Db.Anonymous()).AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    // ============================================================
    // AssignSeatAsync — validation
    // ============================================================

    [Fact]
    public async Task AssignSeat_item_not_found_fails()
    {
        var assignee = await Db.AddUserAsync();
        var assigner = await Db.AddUserAsync();

        var result = await AdminService().AssignSeatAsync(99999, assignee.Id, assigner.Id, null);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการสินค้า", result.Error);
    }

    [Theory]
    [InlineData(ItemType.IotMaterial)]
    [InlineData(ItemType.Other)]
    public async Task AssignSeat_non_server_or_software_rejected(ItemType type)
    {
        // arrange item ของ type ที่จ่าย seat ไม่ได้ แต่บังคับให้มี TotalSeats เพื่อให้แน่ใจว่า branch type ถูกตรวจก่อน
        var item = await Db.AddItemAsync(i => { i.Type = type; i.TotalSeats = 5; });
        var assignee = await Db.AddUserAsync();
        var assigner = await Db.AddUserAsync();

        var result = await AdminService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);

        Assert.False(result.Success);
        Assert.Equal("จ่าย License/Seat ได้เฉพาะ Server หรือ Software", result.Error);
    }

    [Fact]
    public async Task AssignSeat_total_seats_null_rejected()
    {
        // Server/Software แต่ไม่ได้ตั้ง TotalSeats
        var item = await Db.AddItemAsync(i => { i.Type = ItemType.Software; i.TotalSeats = null; });
        var assignee = await Db.AddUserAsync();
        var assigner = await Db.AddUserAsync();

        var result = await AdminService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);

        Assert.False(result.Success);
        Assert.Equal("รายการนี้ยังไม่ได้กำหนดจำนวน License/Seat", result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AssignSeat_total_seats_zero_or_negative_rejected(int seats)
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: seats);
        var assignee = await Db.AddUserAsync();
        var assigner = await Db.AddUserAsync();

        var result = await AdminService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);

        Assert.False(result.Success);
        Assert.Equal("รายการนี้ยังไม่ได้กำหนดจำนวน License/Seat", result.Error);
    }

    [Fact]
    public async Task AssignSeat_when_full_rejected()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 1);
        var assigner = await Db.AddUserAsync();
        var first = await Db.AddUserAsync();
        var second = await Db.AddUserAsync();

        var ok = await AdminService().AssignSeatAsync(item.Id, first.Id, assigner.Id, null);
        Assert.True(ok.Success, ok.Error);

        var full = await AdminService().AssignSeatAsync(item.Id, second.Id, assigner.Id, null);

        Assert.False(full.Success);
        Assert.Equal("License/Seat เต็มแล้ว (1/1)", full.Error);
        Assert.Equal(1, await Db.CountAsync(c => c.LicenseAssignments));
    }

    [Fact]
    public async Task AssignSeat_full_count_ignores_released_assignments()
    {
        // seat ที่ถูก release แล้ว ไม่นับว่าใช้อยู่ จึงจ่ายได้ทั้งที่ TotalSeats = 1
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 1);
        var assigner = await Db.AddUserAsync();
        var oldUser = await Db.AddUserAsync();
        var newUser = await Db.AddUserAsync();

        await Db.AddLicenseAsync(item.Id, oldUser.Id, assigner.Id, releasedAt: DateTime.UtcNow);

        var result = await AdminService().AssignSeatAsync(item.Id, newUser.Id, assigner.Id, null);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task AssignSeat_duplicate_active_assignment_to_same_user_rejected()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync();

        var first = await AdminService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);
        Assert.True(first.Success, first.Error);

        var dup = await AdminService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);

        Assert.False(dup.Success);
        Assert.Equal("ผู้ใช้นี้ได้รับ License/Seat ของรายการนี้อยู่แล้ว", dup.Error);
        Assert.Equal(1, await Db.CountAsync(c => c.LicenseAssignments));
    }

    [Fact]
    public async Task AssignSeat_same_user_on_different_items_allowed()
    {
        var itemA = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var itemB = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync();

        var a = await AdminService().AssignSeatAsync(itemA.Id, assignee.Id, assigner.Id, null);
        var b = await AdminService().AssignSeatAsync(itemB.Id, assignee.Id, assigner.Id, null);

        Assert.True(a.Success, a.Error);
        Assert.True(b.Success, b.Error);
        Assert.Equal(2, await Db.CountAsync(c => c.LicenseAssignments));
    }

    [Fact]
    public async Task AssignSeat_release_then_reassign_same_user_succeeds()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync();

        var first = await AdminService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);
        Assert.True(first.Success, first.Error);

        var release = await AdminService().ReleaseSeatAsync(first.Value!.Id);
        Assert.True(release.Success, release.Error);

        var second = await AdminService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);

        Assert.True(second.Success, second.Error);
        // มี 2 record (เก่า released + ใหม่ active) แต่ active เดียวต่อ user
        Assert.Equal(2, await Db.CountAsync(c => c.LicenseAssignments));
        Assert.Equal(1, await AdminService().GetUsedSeatsAsync(item.Id));
    }

    [Fact]
    public async Task AssignSeat_sets_assigned_at_to_now_and_bumps_item_updated_at()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var result = await AdminService().AssignSeatAsync(item.Id, assignee.Id, assigner.Id, null);
        Assert.True(result.Success, result.Error);

        Assert.True(result.Value!.AssignedAt >= before);
        await using var ctx = Db.NewContext();
        var refreshed = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.True(refreshed.UpdatedAt >= before);
    }

    [Fact]
    public async Task AssignSeat_nonexistent_user_fails_with_duplicate_message_due_to_fk()
    {
        // AUDIT[high]: AssignSeatAsync ไม่ตรวจว่า assignedToId เป็น user จริง — FK violation จะถูกจับเป็น
        // DbUpdateException แล้วคืนข้อความ "ผู้ใช้นี้ได้รับ License/Seat อยู่แล้ว" ซึ่งสื่อความผิด (ที่จริงคือ user ไม่มี)
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();

        var result = await AdminService().AssignSeatAsync(item.Id, "ghost-user-id-does-not-exist", assigner.Id, null);

        Assert.False(result.Success);
        Assert.Equal("ผู้ใช้นี้ได้รับ License/Seat ของรายการนี้อยู่แล้ว", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.LicenseAssignments));
    }

    [Fact]
    public async Task AssignSeat_permission_checked_before_item_lookup()
    {
        // ส่ง item id ที่ไม่มีจริง + ผู้ใช้ไม่มีสิทธิ์ → ต้องคืน Forbidden ไม่ใช่ "ไม่พบรายการ"
        var result = await NewService(Db.Anonymous()).AssignSeatAsync(99999, "x", "y", null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    // ============================================================
    // ReleaseSeatAsync
    // ============================================================

    [Fact]
    public async Task ReleaseSeat_admin_can_release()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync();
        var lic = await Db.AddLicenseAsync(item.Id, assignee.Id, assigner.Id);
        var before = DateTime.UtcNow.AddSeconds(-2);

        var result = await AdminService().ReleaseSeatAsync(lic.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var refreshed = await ctx.LicenseAssignments.FirstAsync(l => l.Id == lic.Id);
        Assert.NotNull(refreshed.ReleasedAt);
        Assert.True(refreshed.ReleasedAt >= before);
    }

    [Fact]
    public async Task ReleaseSeat_purchaser_can_release()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync();
        var lic = await Db.AddLicenseAsync(item.Id, assignee.Id, assigner.Id);

        var result = await PurchaserService().ReleaseSeatAsync(lic.Id);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task ReleaseSeat_staff_is_forbidden()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync();
        var lic = await Db.AddLicenseAsync(item.Id, assignee.Id, assigner.Id);

        var result = await NewService(Db.Accessor(AppRoles.Staff)).ReleaseSeatAsync(lic.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
        // ยังไม่ถูก release
        Assert.Equal(1, await AdminService().GetUsedSeatsAsync(item.Id));
    }

    [Fact]
    public async Task ReleaseSeat_anonymous_is_forbidden()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync();
        var lic = await Db.AddLicenseAsync(item.Id, assignee.Id, assigner.Id);

        var result = await NewService(Db.Anonymous()).ReleaseSeatAsync(lic.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task ReleaseSeat_not_found_fails()
    {
        var result = await AdminService().ReleaseSeatAsync(99999);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการ License", result.Error);
    }

    [Fact]
    public async Task ReleaseSeat_already_released_rejected()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync();
        var lic = await Db.AddLicenseAsync(item.Id, assignee.Id, assigner.Id, releasedAt: DateTime.UtcNow.AddDays(-1));

        var result = await AdminService().ReleaseSeatAsync(lic.Id);

        Assert.False(result.Success);
        Assert.Equal("License นี้ถูกคืนไปแล้ว", result.Error);
    }

    [Fact]
    public async Task ReleaseSeat_frees_a_seat_so_full_item_can_assign_new_user()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 1);
        var assigner = await Db.AddUserAsync();
        var first = await Db.AddUserAsync();
        var second = await Db.AddUserAsync();

        var assign = await AdminService().AssignSeatAsync(item.Id, first.Id, assigner.Id, null);
        Assert.True(assign.Success, assign.Error);

        // เต็มแล้ว — จ่ายใหม่ไม่ได้
        var full = await AdminService().AssignSeatAsync(item.Id, second.Id, assigner.Id, null);
        Assert.False(full.Success);

        // คืน seat แรก
        var release = await AdminService().ReleaseSeatAsync(assign.Value!.Id);
        Assert.True(release.Success, release.Error);

        // ตอนนี้ว่าง — จ่ายให้คนใหม่ได้
        var reassign = await AdminService().AssignSeatAsync(item.Id, second.Id, assigner.Id, null);
        Assert.True(reassign.Success, reassign.Error);
        Assert.Equal(1, await AdminService().GetUsedSeatsAsync(item.Id));
    }

    [Fact]
    public async Task ReleaseSeat_permission_checked_before_lookup()
    {
        // assignmentId ไม่มีจริง + ไม่มีสิทธิ์ → Forbidden ไม่ใช่ "ไม่พบ"
        var result = await NewService(Db.Anonymous()).ReleaseSeatAsync(99999);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    // ============================================================
    // GetUsedSeatsAsync
    // ============================================================

    [Fact]
    public async Task GetUsedSeats_counts_only_active_for_that_item()
    {
        var itemA = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);
        var itemB = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 10);
        var assigner = await Db.AddUserAsync();
        var u1 = await Db.AddUserAsync();
        var u2 = await Db.AddUserAsync();
        var u3 = await Db.AddUserAsync();

        await Db.AddLicenseAsync(itemA.Id, u1.Id, assigner.Id);                                   // active
        await Db.AddLicenseAsync(itemA.Id, u2.Id, assigner.Id);                                   // active
        await Db.AddLicenseAsync(itemA.Id, u3.Id, assigner.Id, releasedAt: DateTime.UtcNow);      // released — ไม่นับ
        await Db.AddLicenseAsync(itemB.Id, u1.Id, assigner.Id);                                   // active แต่คนละ item

        Assert.Equal(2, await AdminService().GetUsedSeatsAsync(itemA.Id));
        Assert.Equal(1, await AdminService().GetUsedSeatsAsync(itemB.Id));
    }

    [Fact]
    public async Task GetUsedSeats_returns_zero_for_item_with_no_assignments()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);

        Assert.Equal(0, await AdminService().GetUsedSeatsAsync(item.Id));
    }

    [Fact]
    public async Task GetUsedSeats_returns_zero_for_unknown_item()
    {
        Assert.Equal(0, await AdminService().GetUsedSeatsAsync(99999));
    }

    // ============================================================
    // GetUsedSeatsMapAsync
    // ============================================================

    [Fact]
    public async Task GetUsedSeatsMap_groups_active_by_item_and_excludes_released()
    {
        var itemA = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);
        var itemB = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 10);
        var assigner = await Db.AddUserAsync();
        var u1 = await Db.AddUserAsync();
        var u2 = await Db.AddUserAsync();
        var u3 = await Db.AddUserAsync();

        await Db.AddLicenseAsync(itemA.Id, u1.Id, assigner.Id);
        await Db.AddLicenseAsync(itemA.Id, u2.Id, assigner.Id);
        await Db.AddLicenseAsync(itemA.Id, u3.Id, assigner.Id, releasedAt: DateTime.UtcNow);
        await Db.AddLicenseAsync(itemB.Id, u1.Id, assigner.Id);

        var map = await AdminService().GetUsedSeatsMapAsync();

        Assert.Equal(2, map[itemA.Id]);
        Assert.Equal(1, map[itemB.Id]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public async Task GetUsedSeatsMap_item_with_only_released_is_absent()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);
        var assigner = await Db.AddUserAsync();
        var u1 = await Db.AddUserAsync();
        await Db.AddLicenseAsync(item.Id, u1.Id, assigner.Id, releasedAt: DateTime.UtcNow);

        var map = await AdminService().GetUsedSeatsMapAsync();

        Assert.False(map.ContainsKey(item.Id));
        Assert.Empty(map);
    }

    [Fact]
    public async Task GetUsedSeatsMap_empty_when_no_assignments()
    {
        var map = await AdminService().GetUsedSeatsMapAsync();
        Assert.Empty(map);
    }

    // ============================================================
    // GetSeatItemsAsync
    // ============================================================

    [Fact]
    public async Task GetSeatItems_returns_only_server_and_software_ordered_by_name()
    {
        await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "Zoom");
        await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 5, configure: i => i.Name = "Apache");
        await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "Microsoft 365");
        await Db.AddIotItemAsync(configure: i => i.Name = "Cable");                        // IoT — ต้องไม่ติด
        await Db.AddItemAsync(i => { i.Type = ItemType.Other; i.Name = "Other thing"; });  // Other — ต้องไม่ติด

        var items = await AdminService().GetSeatItemsAsync();

        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { "Apache", "Microsoft 365", "Zoom" }, items.Select(i => i.Name).ToArray());
    }

    [Fact]
    public async Task GetSeatItems_includes_server_software_even_without_total_seats()
    {
        // AUDIT[low]: GetSeatItemsAsync คืน Server/Software ที่ TotalSeats = null ด้วย (ต่างจาก GetSeatUsageAsync)
        // ทำให้ UI dropdown เลือกได้ แต่จ่าย seat จริงจะถูกปฏิเสธทันที — ควรกรอง/แสดงเตือนให้สอดคล้องกัน
        await Db.AddItemAsync(i => { i.Type = ItemType.Software; i.Name = "NoSeats"; i.TotalSeats = null; });

        var items = await AdminService().GetSeatItemsAsync();

        Assert.Single(items);
        Assert.Equal("NoSeats", items[0].Name);
        Assert.Null(items[0].TotalSeats);
    }

    [Fact]
    public async Task GetSeatItems_empty_when_none()
    {
        await Db.AddIotItemAsync();
        var items = await AdminService().GetSeatItemsAsync();
        Assert.Empty(items);
    }

    // ============================================================
    // GetSeatUsageAsync
    // ============================================================

    [Fact]
    public async Task GetSeatUsage_excludes_items_without_total_seats()
    {
        await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "HasSeats");
        await Db.AddItemAsync(i => { i.Type = ItemType.Software; i.Name = "NoSeats"; i.TotalSeats = null; });

        var usage = await AdminService().GetSeatUsageAsync();

        Assert.Single(usage);
        Assert.Equal("HasSeats", usage[0].Name);
    }

    [Fact]
    public async Task GetSeatUsage_excludes_iot_and_other_types()
    {
        await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "Sw");
        await Db.AddIotItemAsync(configure: i => { i.Name = "Iot"; i.TotalSeats = 5; });
        await Db.AddItemAsync(i => { i.Type = ItemType.Other; i.Name = "Other"; i.TotalSeats = 5; });

        var usage = await AdminService().GetSeatUsageAsync();

        Assert.Single(usage);
        Assert.Equal("Sw", usage[0].Name);
    }

    [Fact]
    public async Task GetSeatUsage_used_counts_active_only_and_item_with_zero_used_appears()
    {
        var busy = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10, configure: i => i.Name = "Busy");
        var idle = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 10, configure: i => i.Name = "Idle");
        var assigner = await Db.AddUserAsync();
        var u1 = await Db.AddUserAsync();
        var u2 = await Db.AddUserAsync();

        await Db.AddLicenseAsync(busy.Id, u1.Id, assigner.Id);                              // active
        await Db.AddLicenseAsync(busy.Id, u2.Id, assigner.Id, releasedAt: DateTime.UtcNow); // released — ไม่นับ

        var usage = await AdminService().GetSeatUsageAsync();

        var busyRow = usage.Single(s => s.ItemId == busy.Id);
        var idleRow = usage.Single(s => s.ItemId == idle.Id);
        Assert.Equal(1, busyRow.Used);
        Assert.Equal(10, busyRow.Total);
        Assert.Equal(0, idleRow.Used);   // item ที่ไม่มี active assignment ก็ยังปรากฏด้วย Used = 0
        Assert.Equal(10, idleRow.Total);
        Assert.Equal(2, usage.Count);
    }

    [Fact]
    public async Task GetSeatUsage_ordered_by_used_ratio_descending()
    {
        // low ratio: 1/10 = 0.1 ; high ratio: 3/4 = 0.75
        var low = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10, configure: i => i.Name = "Low");
        var high = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 4, configure: i => i.Name = "High");
        var assigner = await Db.AddUserAsync();

        await Db.AddLicenseAsync(low.Id, (await Db.AddUserAsync()).Id, assigner.Id);
        for (var n = 0; n < 3; n++)
            await Db.AddLicenseAsync(high.Id, (await Db.AddUserAsync()).Id, assigner.Id);

        var usage = await AdminService().GetSeatUsageAsync();

        Assert.Equal(high.Id, usage[0].ItemId);   // ratio สูงกว่ามาก่อน
        Assert.Equal(low.Id, usage[1].ItemId);
        Assert.True(usage[0].UsedRatio > usage[1].UsedRatio);
    }

    [Fact]
    public async Task GetSeatUsage_seatusage_record_available_and_ratio_computed()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 4, configure: i => i.Name = "X");
        var assigner = await Db.AddUserAsync();
        await Db.AddLicenseAsync(item.Id, (await Db.AddUserAsync()).Id, assigner.Id);

        var usage = await AdminService().GetSeatUsageAsync();
        var row = Assert.Single(usage);
        Assert.Equal(3, row.Available);             // 4 - 1
        Assert.Equal(0.25, row.UsedRatio, 5);       // 1/4
    }

    [Fact]
    public async Task GetSeatUsage_item_with_zero_total_seats_has_zero_ratio_and_full_available()
    {
        // AUDIT[low]: รายการที่ TotalSeats = 0 ปรากฏใน GetSeatUsageAsync (TotalSeats != null) แม้จ่าย seat ไม่ได้เลย;
        // UsedRatio ป้องกัน divide-by-zero (Total <= 0 → 0) และ Available = Max(0, 0-0) = 0
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 0, configure: i => i.Name = "ZeroSeats");

        var usage = await AdminService().GetSeatUsageAsync();
        var row = Assert.Single(usage);
        Assert.Equal(0, row.Used);
        Assert.Equal(0, row.Total);
        Assert.Equal(0.0, row.UsedRatio);
        Assert.Equal(0, row.Available);
    }

    [Fact]
    public async Task GetSeatUsage_empty_when_no_seat_items()
    {
        await Db.AddIotItemAsync();
        var usage = await AdminService().GetSeatUsageAsync();
        Assert.Empty(usage);
    }

    // ============================================================
    // GetAssignmentsAsync — filters, search, ordering, includes
    // ============================================================

    [Fact]
    public async Task GetAssignments_no_filter_returns_all_with_includes()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "Slack");
        var assigner = await Db.AddUserAsync();
        var assignee = await Db.AddUserAsync("ชื่อเต็ม");
        await Db.AddLicenseAsync(item.Id, assignee.Id, assigner.Id);

        var list = await AdminService().GetAssignmentsAsync();

        var row = Assert.Single(list);
        Assert.NotNull(row.Item);                  // Include(Item)
        Assert.Equal("Slack", row.Item.Name);
        Assert.NotNull(row.AssignedTo);            // Include(AssignedTo)
        Assert.Equal("ชื่อเต็ม", row.AssignedTo.FullName);
    }

    [Fact]
    public async Task GetAssignments_empty_when_none()
    {
        var list = await AdminService().GetAssignmentsAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetAssignments_item_filter()
    {
        var itemA = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var itemB = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        await Db.AddLicenseAsync(itemA.Id, (await Db.AddUserAsync()).Id, assigner.Id);
        await Db.AddLicenseAsync(itemB.Id, (await Db.AddUserAsync()).Id, assigner.Id);

        var list = await AdminService().GetAssignmentsAsync(itemId: itemA.Id);

        var row = Assert.Single(list);
        Assert.Equal(itemA.Id, row.ItemId);
    }

    [Fact]
    public async Task GetAssignments_active_only_filter_excludes_released()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        var activeUser = await Db.AddUserAsync();
        var releasedUser = await Db.AddUserAsync();
        await Db.AddLicenseAsync(item.Id, activeUser.Id, assigner.Id);
        await Db.AddLicenseAsync(item.Id, releasedUser.Id, assigner.Id, releasedAt: DateTime.UtcNow);

        var all = await AdminService().GetAssignmentsAsync();
        var active = await AdminService().GetAssignmentsAsync(activeOnly: true);

        Assert.Equal(2, all.Count);
        var row = Assert.Single(active);
        Assert.Equal(activeUser.Id, row.AssignedToId);
        Assert.Null(row.ReleasedAt);
    }

    [Fact]
    public async Task GetAssignments_item_and_active_filters_combined()
    {
        var itemA = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var itemB = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        await Db.AddLicenseAsync(itemA.Id, (await Db.AddUserAsync()).Id, assigner.Id);                                 // A active
        await Db.AddLicenseAsync(itemA.Id, (await Db.AddUserAsync()).Id, assigner.Id, releasedAt: DateTime.UtcNow);    // A released
        await Db.AddLicenseAsync(itemB.Id, (await Db.AddUserAsync()).Id, assigner.Id);                                 // B active

        var list = await AdminService().GetAssignmentsAsync(itemId: itemA.Id, activeOnly: true);

        var row = Assert.Single(list);
        Assert.Equal(itemA.Id, row.ItemId);
        Assert.Null(row.ReleasedAt);
    }

    [Fact]
    public async Task GetAssignments_search_matches_item_name_case_insensitive()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "Microsoft Office");
        var other = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 5, configure: i => i.Name = "Linux Server");
        var assigner = await Db.AddUserAsync();
        await Db.AddLicenseAsync(item.Id, (await Db.AddUserAsync()).Id, assigner.Id);
        await Db.AddLicenseAsync(other.Id, (await Db.AddUserAsync()).Id, assigner.Id);

        // lowercase query ต้องเจอชื่อที่มีตัวพิมพ์ใหญ่ (ILike)
        var list = await AdminService().GetAssignmentsAsync(search: "microsoft");

        var row = Assert.Single(list);
        Assert.Equal(item.Id, row.ItemId);
    }

    [Fact]
    public async Task GetAssignments_search_matches_assignee_fullname_case_insensitive()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "App");
        var assigner = await Db.AddUserAsync();
        var target = await Db.AddUserAsync("Somchai Jaidee");
        var other = await Db.AddUserAsync("Wanida Suk");
        await Db.AddLicenseAsync(item.Id, target.Id, assigner.Id);
        await Db.AddLicenseAsync(item.Id, other.Id, assigner.Id);

        var list = await AdminService().GetAssignmentsAsync(search: "SOMCHAI");

        var row = Assert.Single(list);
        Assert.Equal(target.Id, row.AssignedToId);
    }

    [Fact]
    public async Task GetAssignments_search_matches_assignee_email_case_insensitive()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "App");
        var assigner = await Db.AddUserAsync();
        var target = await Db.AddUserAsync("X", email: "Special.User@Carpet.CO.TH");
        var other = await Db.AddUserAsync("Y", email: "nobody@elsewhere.com");
        await Db.AddLicenseAsync(item.Id, target.Id, assigner.Id);
        await Db.AddLicenseAsync(item.Id, other.Id, assigner.Id);

        var list = await AdminService().GetAssignmentsAsync(search: "special.user@carpet");

        var row = Assert.Single(list);
        Assert.Equal(target.Id, row.AssignedToId);
    }

    [Fact]
    public async Task GetAssignments_search_no_match_returns_empty()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "App");
        var assigner = await Db.AddUserAsync();
        await Db.AddLicenseAsync(item.Id, (await Db.AddUserAsync("Real Name", email: "real@x.com")).Id, assigner.Id);

        var list = await AdminService().GetAssignmentsAsync(search: "zzz-no-such-thing");

        Assert.Empty(list);
    }

    [Fact]
    public async Task GetAssignments_search_is_trimmed()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "Photoshop");
        var assigner = await Db.AddUserAsync();
        await Db.AddLicenseAsync(item.Id, (await Db.AddUserAsync()).Id, assigner.Id);

        var list = await AdminService().GetAssignmentsAsync(search: "   photoshop   ");

        Assert.Single(list);
    }

    [Fact]
    public async Task GetAssignments_whitespace_search_is_ignored_returns_all()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "App");
        var assigner = await Db.AddUserAsync();
        await Db.AddLicenseAsync(item.Id, (await Db.AddUserAsync()).Id, assigner.Id);
        await Db.AddLicenseAsync(item.Id, (await Db.AddUserAsync()).Id, assigner.Id);

        // string.IsNullOrWhiteSpace → ไม่ filter
        var list = await AdminService().GetAssignmentsAsync(search: "   ");

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task GetAssignments_search_with_percent_is_treated_literally_by_ilike_wildcard()
    {
        // AUDIT[low]: search ใส่ค่าตรงเข้า ILike pattern ไม่ escape % หรือ _ — ผู้ใช้ที่พิมพ์ "%" จะ match ทุกแถว
        // (LIKE wildcard injection ระดับ filter — ไม่อันตรายต่อความปลอดภัยแต่ผลลัพธ์ search ผิดจากที่คาด)
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "Anything");
        var assigner = await Db.AddUserAsync();
        await Db.AddLicenseAsync(item.Id, (await Db.AddUserAsync()).Id, assigner.Id);

        var list = await AdminService().GetAssignmentsAsync(search: "%");

        Assert.Single(list);  // "%" เป็น wildcard จึง match ทุกแถว
    }

    [Fact]
    public async Task GetAssignments_ordered_active_first_then_assigned_at_desc()
    {
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);
        var assigner = await Db.AddUserAsync();
        var uReleasedNew = await Db.AddUserAsync();
        var uActiveOld = await Db.AddUserAsync();
        var uActiveNew = await Db.AddUserAsync();

        // released — แม้ AssignedAt ใหม่สุด ก็ต้องอยู่ท้าย (active-first)
        await Db.AddLicenseAsync(item.Id, uReleasedNew.Id, assigner.Id,
            releasedAt: DateTime.UtcNow, configure: l => l.AssignedAt = DateTime.UtcNow);
        // active เก่า
        var activeOld = await Db.AddLicenseAsync(item.Id, uActiveOld.Id, assigner.Id,
            configure: l => l.AssignedAt = DateTime.UtcNow.AddDays(-5));
        // active ใหม่
        var activeNew = await Db.AddLicenseAsync(item.Id, uActiveNew.Id, assigner.Id,
            configure: l => l.AssignedAt = DateTime.UtcNow.AddDays(-1));

        var list = await AdminService().GetAssignmentsAsync();

        Assert.Equal(3, list.Count);
        // active เรียงตาม AssignedAt desc: activeNew ก่อน activeOld
        Assert.Equal(activeNew.Id, list[0].Id);
        Assert.Equal(activeOld.Id, list[1].Id);
        // released ไปอยู่ท้าย
        Assert.Equal(uReleasedNew.Id, list[2].AssignedToId);
        Assert.NotNull(list[2].ReleasedAt);
    }

    [Fact]
    public async Task GetAssignments_has_no_permission_check_returns_data_for_anonymous()
    {
        // AUDIT[medium]: read methods (GetAssignments/GetSeatUsage/ฯลฯ) ไม่มีการตรวจสิทธิ์เลย
        // ผู้ใช้ anonymous/role ใดก็เรียกดูการจ่าย license ของทุกคน (รวมอีเมล) ได้ — ข้อมูลรั่วถ้า service ถูกเรียกตรง
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var assigner = await Db.AddUserAsync();
        await Db.AddLicenseAsync(item.Id, (await Db.AddUserAsync()).Id, assigner.Id);

        var list = await NewService(Db.Anonymous()).GetAssignmentsAsync();

        Assert.Single(list);
    }
}
