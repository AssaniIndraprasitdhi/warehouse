using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>
/// ครอบคลุม WithdrawalService ทุก method และทุก branch:
/// GetWithdrawalsAsync (filters/search/order), RecordWithdrawalAsync (validation/stock),
/// GetWithdrawableItemsAsync, DeleteAsync (stock return).
/// ยืนยันพฤติกรรมจริง (regression baseline) — จุดที่น่าสงสัยมี AUDIT comment กำกับ.
/// </summary>
public class WithdrawalServiceTests(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    private WithdrawalService Service(params string[] roles)
        => new(Db.Factory, Db.Accessor(roles));

    private WithdrawalService AnonService()
        => new(Db.Factory, Db.Anonymous());

    // ============================================================
    //  RecordWithdrawalAsync — permissions
    // ============================================================

    [Fact]
    public async Task RecordWithdrawal_admin_can_withdraw()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Admin);

        var result = await svc.RecordWithdrawalAsync(item.Id, 5, user.Id, "ใช้งาน", DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task RecordWithdrawal_purchaser_can_withdraw()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Purchaser);

        var result = await svc.RecordWithdrawalAsync(item.Id, 5, user.Id, null, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task RecordWithdrawal_staff_can_withdraw()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Staff);

        var result = await svc.RecordWithdrawalAsync(item.Id, 5, user.Id, null, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task RecordWithdrawal_viewer_cannot_withdraw()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Viewer);

        var result = await svc.RecordWithdrawalAsync(item.Id, 5, user.Id, null, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
        // ยืนยันว่าไม่มีการเปลี่ยนสต็อกเมื่อถูกปฏิเสธ
        Assert.Equal(0, await Db.CountAsync(c => c.Withdrawals));
    }

    [Fact]
    public async Task RecordWithdrawal_anonymous_cannot_withdraw()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = AnonService();

        var result = await svc.RecordWithdrawalAsync(item.Id, 5, user.Id, null, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Withdrawals));
    }

    [Fact]
    public async Task RecordWithdrawal_permission_checked_before_validation()
    {
        // anonymous + invalid quantity -> ต้องได้ Forbidden ไม่ใช่ข้อความ validation
        var svc = AnonService();
        var result = await svc.RecordWithdrawalAsync(999, 0, "nobody", null, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    // ============================================================
    //  RecordWithdrawalAsync — validation & business rules
    // ============================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task RecordWithdrawal_rejects_quantity_less_than_one(int qty)
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Admin);

        var result = await svc.RecordWithdrawalAsync(item.Id, qty, user.Id, null, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("จำนวนที่เบิกต้องมากกว่า 0", result.Error);
    }

    [Fact]
    public async Task RecordWithdrawal_item_not_found()
    {
        var user = await Db.AddUserAsync();
        var svc = Service(AppRoles.Admin);

        var result = await svc.RecordWithdrawalAsync(123456, 1, user.Id, null, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการสินค้า", result.Error);
    }

    [Theory]
    [InlineData(ItemType.Server)]
    [InlineData(ItemType.Software)]
    [InlineData(ItemType.Other)]
    public async Task RecordWithdrawal_rejects_non_iot_item(ItemType type)
    {
        var user = await Db.AddUserAsync();
        // มีสต็อกพอ แต่เป็นชนิดที่เบิกไม่ได้
        var item = await Db.AddItemAsync(i => { i.Type = type; i.Quantity = 100; });
        var svc = Service(AppRoles.Admin);

        var result = await svc.RecordWithdrawalAsync(item.Id, 1, user.Id, null, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("เบิกได้เฉพาะวัสดุ IoT เท่านั้น", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Withdrawals));
    }

    [Fact]
    public async Task RecordWithdrawal_insufficient_stock_shows_remaining()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 3, configure: i => i.Unit = "ม้วน");
        var svc = Service(AppRoles.Admin);

        var result = await svc.RecordWithdrawalAsync(item.Id, 4, user.Id, null, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("สต็อกคงเหลือไม่พอ (เหลือ 3 ม้วน)", result.Error);
        // สต็อกไม่ถูกแตะ
        await using var ctx = Db.NewContext();
        Assert.Equal(3, (await ctx.Items.FindAsync(item.Id))!.Quantity);
    }

    [Fact]
    public async Task RecordWithdrawal_insufficient_when_stock_is_zero()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 0);
        var svc = Service(AppRoles.Admin);

        var result = await svc.RecordWithdrawalAsync(item.Id, 1, user.Id, null, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Contains("สต็อกคงเหลือไม่พอ", result.Error);
    }

    [Fact]
    public async Task RecordWithdrawal_success_decrements_stock_and_creates_row()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Admin);
        var before = DateTime.UtcNow;

        var result = await svc.RecordWithdrawalAsync(item.Id, 12, user.Id, "ติดตั้งเซ็นเซอร์", before, "หมายเหตุ");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        Assert.Equal(12, result.Value!.Quantity);
        Assert.Equal("ติดตั้งเซ็นเซอร์", result.Value.Purpose);
        Assert.Equal("หมายเหตุ", result.Value.Note);
        Assert.Equal(user.Id, result.Value.WithdrawnById);
        Assert.True(result.Value.Id > 0);

        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(38, fresh!.Quantity);              // 50 - 12
        Assert.True(fresh.UpdatedAt >= before);          // UpdatedAt ถูกอัปเดต
        Assert.Equal(1, await ctx.Withdrawals.CountAsync());
    }

    [Fact]
    public async Task RecordWithdrawal_exactly_all_stock_leaves_zero()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 7);
        var svc = Service(AppRoles.Admin);

        var result = await svc.RecordWithdrawalAsync(item.Id, 7, user.Id, null, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        Assert.Equal(0, (await ctx.Items.FindAsync(item.Id))!.Quantity);
    }

    [Fact]
    public async Task RecordWithdrawal_null_purpose_and_note_are_stored_as_null()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 10);
        var svc = Service(AppRoles.Admin);

        var result = await svc.RecordWithdrawalAsync(item.Id, 1, user.Id, null, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var row = await ctx.Withdrawals.SingleAsync();
        Assert.Null(row.Purpose);
        Assert.Null(row.Note);
    }

    [Fact]
    public async Task RecordWithdrawal_stores_provided_when_timestamp_verbatim()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 10);
        var svc = Service(AppRoles.Admin);
        var when = new DateTime(2025, 3, 15, 8, 30, 0, DateTimeKind.Utc);

        var result = await svc.RecordWithdrawalAsync(item.Id, 1, user.Id, null, when, null);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var row = await ctx.Withdrawals.SingleAsync();
        Assert.Equal(when, row.WithdrawnAt);
    }

    [Fact]
    public async Task RecordWithdrawal_accepts_future_when_timestamp()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 10);
        var svc = Service(AppRoles.Admin);
        var future = DateTime.UtcNow.AddYears(5);

        var result = await svc.RecordWithdrawalAsync(item.Id, 1, user.Id, null, future, null);

        // AUDIT[low]: ไม่มีการ validate ว่า `when` ต้องไม่เป็นอนาคต — บันทึกเบิกล่วงหน้าได้ ทำให้รายงาน/timeline เพี้ยน
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task RecordWithdrawal_empty_withdrawnById_throws_unhandled_db_exception()
    {
        var item = await Db.AddIotItemAsync(quantity: 10);
        var svc = Service(AppRoles.Admin);

        // AUDIT[medium]: RecordWithdrawalAsync ไม่ validate withdrawnById เลย ส่ง string ว่าง/ไม่มีตัวตนได้
        // FK ไป AspNetUsers บังคับจริง → SaveChanges โยน DbUpdateException ที่ไม่ถูก catch (HTTP 500)
        // แทนที่จะคืน error ที่เป็นมิตร (ปกติ id มาจากผู้ใช้ที่ login จึงไม่ค่อยเกิด แต่ไม่มีการป้องกันเชิงลึก)
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            svc.RecordWithdrawalAsync(item.Id, 1, "", null, DateTime.UtcNow, null));

        // ยืนยันว่าไม่มีรายการเบิกถูกบันทึก และสต็อกไม่ถูกหัก (transaction ล้มเหลวทั้งก้อน)
        Assert.Equal(0, await Db.CountAsync(c => c.Withdrawals));
        await using var ctx = Db.NewContext();
        Assert.Equal(10, (await ctx.Items.SingleAsync(i => i.Id == item.Id)).Quantity);
    }

    [Fact]
    public async Task RecordWithdrawal_two_sequential_withdrawals_accumulate_decrement()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 20);
        var svc = Service(AppRoles.Admin);

        Assert.True((await svc.RecordWithdrawalAsync(item.Id, 5, user.Id, null, DateTime.UtcNow, null)).Success);
        Assert.True((await svc.RecordWithdrawalAsync(item.Id, 8, user.Id, null, DateTime.UtcNow, null)).Success);

        await using var ctx = Db.NewContext();
        Assert.Equal(7, (await ctx.Items.FindAsync(item.Id))!.Quantity); // 20 - 5 - 8
        Assert.Equal(2, await ctx.Withdrawals.CountAsync());
    }

    // ============================================================
    //  GetWithdrawableItemsAsync
    // ============================================================

    [Fact]
    public async Task GetWithdrawableItems_returns_only_iot_ordered_by_name()
    {
        await Db.AddIotItemAsync(quantity: 5, configure: i => i.Name = "เซ็นเซอร์");
        await Db.AddIotItemAsync(quantity: 5, configure: i => i.Name = "การ์ด");
        await Db.AddItemAsync(i => { i.Type = ItemType.Server; i.Name = "เซิร์ฟเวอร์"; });
        await Db.AddItemAsync(i => { i.Type = ItemType.Software; i.Name = "โปรแกรม"; });
        await Db.AddItemAsync(i => { i.Type = ItemType.Other; i.Name = "อื่นๆ"; });
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawableItemsAsync();

        Assert.Equal(2, list.Count);
        Assert.All(list, i => Assert.Equal(ItemType.IotMaterial, i.Type));
        Assert.Equal(new[] { "การ์ด", "เซ็นเซอร์" }, list.Select(i => i.Name).ToArray());
    }

    [Fact]
    public async Task GetWithdrawableItems_includes_zero_stock_iot_items()
    {
        await Db.AddIotItemAsync(quantity: 0, configure: i => i.Name = "หมดสต็อก");
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawableItemsAsync();

        // AUDIT[low]: ชื่อ method/contract ระบุ "ยังมีสต็อกให้เบิก" แต่จริง ๆ คืน IoT ทุกตัวรวมที่ Quantity=0 ด้วย — UI อาจเสนอให้เบิกของที่หมดสต็อก
        Assert.Single(list);
        Assert.Equal("หมดสต็อก", list[0].Name);
    }

    [Fact]
    public async Task GetWithdrawableItems_empty_when_no_iot()
    {
        await Db.AddItemAsync(i => i.Type = ItemType.Server);
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawableItemsAsync();

        Assert.Empty(list);
    }

    [Fact]
    public async Task GetWithdrawableItems_has_no_permission_gate()
    {
        await Db.AddIotItemAsync(quantity: 5);
        var svc = AnonService();

        var list = await svc.GetWithdrawableItemsAsync();

        // AUDIT[low]: GetWithdrawableItemsAsync ไม่มีการตรวจสิทธิ์เลย — anonymous เรียกดูรายการ IoT ทั้งหมดได้
        Assert.Single(list);
    }

    // ============================================================
    //  GetWithdrawalsAsync — listing, filters, search, ordering
    // ============================================================

    [Fact]
    public async Task GetWithdrawals_returns_all_with_includes()
    {
        var user = await Db.AddUserAsync(fullName: "สมชาย");
        var item = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "เซ็นเซอร์ A");
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.Quantity = 3);
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync();

        Assert.Single(list);
        Assert.NotNull(list[0].Item);              // Include(Item)
        Assert.Equal("เซ็นเซอร์ A", list[0].Item.Name);
        Assert.NotNull(list[0].WithdrawnBy);       // Include(WithdrawnBy)
        Assert.Equal("สมชาย", list[0].WithdrawnBy.FullName);
    }

    [Fact]
    public async Task GetWithdrawals_empty_returns_empty_list()
    {
        var svc = Service(AppRoles.Admin);
        var list = await svc.GetWithdrawalsAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetWithdrawals_has_no_permission_gate()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddWithdrawalAsync(item.Id, user.Id);
        var svc = AnonService();

        var list = await svc.GetWithdrawalsAsync();

        // AUDIT[low]: GetWithdrawalsAsync ไม่มีการตรวจสิทธิ์ — anonymous ดูประวัติการเบิกทั้งหมดได้
        Assert.Single(list);
    }

    [Fact]
    public async Task GetWithdrawals_orders_by_withdrawnAt_desc_then_id_desc()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        var t1 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        // สอง row ที่เวลาเท่ากัน -> tie-break ด้วย Id desc
        var wOldA = await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.WithdrawnAt = t1);
        var wOldB = await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.WithdrawnAt = t1);
        var wNew = await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.WithdrawnAt = t2);
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync();

        // ใหม่สุดก่อน, จากนั้นภายในเวลาเดียวกัน Id ใหญ่กว่ามาก่อน
        Assert.Equal(new[] { wNew.Id, wOldB.Id, wOldA.Id }, list.Select(w => w.Id).ToArray());
    }

    [Fact]
    public async Task GetWithdrawals_filters_by_itemId()
    {
        var user = await Db.AddUserAsync();
        var itemA = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "A");
        var itemB = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "B");
        await Db.AddWithdrawalAsync(itemA.Id, user.Id);
        await Db.AddWithdrawalAsync(itemA.Id, user.Id);
        await Db.AddWithdrawalAsync(itemB.Id, user.Id);
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(itemId: itemA.Id);

        Assert.Equal(2, list.Count);
        Assert.All(list, w => Assert.Equal(itemA.Id, w.ItemId));
    }

    [Fact]
    public async Task GetWithdrawals_filter_from_is_inclusive_and_uses_full_time()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        var boundary = new DateTime(2025, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var atBoundary = await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.WithdrawnAt = boundary);
        var justBefore = await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.WithdrawnAt = boundary.AddMinutes(-1));
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(from: boundary);

        // AUDIT[medium]: `from` ใช้ค่าเวลาเต็ม (w.WithdrawnAt >= from) ไม่ normalize เป็นวันเหมือน `to` (ใช้ .Date.AddDays(1))
        // ทำให้ filter ช่วงวันไม่สมมาตร: ส่ง from=วันที่+เวลา 10:00 จะตัด record ของวันเดียวกันก่อน 10:00 ทิ้ง
        Assert.Single(list);
        Assert.Equal(atBoundary.Id, list[0].Id);
        Assert.DoesNotContain(list, w => w.Id == justBefore.Id);
    }

    [Fact]
    public async Task GetWithdrawals_filter_to_is_inclusive_of_whole_day()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        var to = new DateTime(2025, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        // ปลายวันของ `to` ต้องยังถูกรวม (boundary inclusive ทั้งวัน)
        var endOfDay = await Db.AddWithdrawalAsync(item.Id, user.Id,
            w => w.WithdrawnAt = new DateTime(2025, 5, 10, 23, 59, 59, DateTimeKind.Utc));
        // เริ่มวันถัดไปต้องถูกตัด
        var nextDay = await Db.AddWithdrawalAsync(item.Id, user.Id,
            w => w.WithdrawnAt = new DateTime(2025, 5, 11, 0, 0, 0, DateTimeKind.Utc));
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(to: to);

        Assert.Single(list);
        Assert.Equal(endOfDay.Id, list[0].Id);
        Assert.DoesNotContain(list, w => w.Id == nextDay.Id);
    }

    [Fact]
    public async Task GetWithdrawals_to_ignores_time_component_of_argument()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        // record เวลา 20:00 ของวันที่ 10
        var rec = await Db.AddWithdrawalAsync(item.Id, user.Id,
            w => w.WithdrawnAt = new DateTime(2025, 5, 10, 20, 0, 0, DateTimeKind.Utc));
        var svc = Service(AppRoles.Admin);

        // ส่ง to = 10 พ.ค. เวลา 08:00 (ก่อน record) — แต่เพราะ .Date.AddDays(1) จึงยังรวมทั้งวัน
        var list = await svc.GetWithdrawalsAsync(to: new DateTime(2025, 5, 10, 8, 0, 0, DateTimeKind.Utc));

        Assert.Single(list);
        Assert.Equal(rec.Id, list[0].Id);
    }

    [Fact]
    public async Task GetWithdrawals_from_and_to_combined_range()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        var inRange = await Db.AddWithdrawalAsync(item.Id, user.Id,
            w => w.WithdrawnAt = new DateTime(2025, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        await Db.AddWithdrawalAsync(item.Id, user.Id,
            w => w.WithdrawnAt = new DateTime(2025, 4, 30, 12, 0, 0, DateTimeKind.Utc)); // ก่อน from
        await Db.AddWithdrawalAsync(item.Id, user.Id,
            w => w.WithdrawnAt = new DateTime(2025, 5, 20, 12, 0, 0, DateTimeKind.Utc)); // หลัง to
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(
            from: new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2025, 5, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(list);
        Assert.Equal(inRange.Id, list[0].Id);
    }

    [Fact]
    public async Task GetWithdrawals_search_matches_item_name_case_insensitive()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "Arduino Uno");
        var other = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "Raspberry Pi");
        await Db.AddWithdrawalAsync(item.Id, user.Id);
        await Db.AddWithdrawalAsync(other.Id, user.Id);
        var svc = Service(AppRoles.Admin);

        // ILike -> case-insensitive
        var list = await svc.GetWithdrawalsAsync(search: "arduino");

        Assert.Single(list);
        Assert.Equal("Arduino Uno", list[0].Item.Name);
    }

    [Fact]
    public async Task GetWithdrawals_search_matches_purpose_case_insensitive()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "Sensor");
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.Purpose = "Maintenance Job");
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.Purpose = "Other");
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(search: "MAINTENANCE");

        Assert.Single(list);
        Assert.Equal("Maintenance Job", list[0].Purpose);
    }

    [Fact]
    public async Task GetWithdrawals_search_partial_substring()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "Temperature Sensor");
        await Db.AddWithdrawalAsync(item.Id, user.Id);
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(search: "perat");

        Assert.Single(list);
    }

    [Fact]
    public async Task GetWithdrawals_search_with_null_purpose_does_not_throw_and_matches_name()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "GPS Module");
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.Purpose = null);
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(search: "gps");

        // null Purpose guarded by (w.Purpose != null && ...) -> ไม่ throw, ยัง match จากชื่อ item
        Assert.Single(list);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetWithdrawals_blank_search_is_ignored(string search)
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        await Db.AddWithdrawalAsync(item.Id, user.Id);
        await Db.AddWithdrawalAsync(item.Id, user.Id);
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(search: search);

        // string ว่าง/whitespace -> ไม่กรอง (IsNullOrWhiteSpace)
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task GetWithdrawals_search_is_trimmed()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "Relay");
        await Db.AddWithdrawalAsync(item.Id, user.Id);
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(search: "  Relay  ");

        Assert.Single(list);
    }

    [Fact]
    public async Task GetWithdrawals_search_no_match_returns_empty()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "Relay");
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => w.Purpose = "install");
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(search: "zzz-nomatch");

        Assert.Empty(list);
    }

    [Fact]
    public async Task GetWithdrawals_all_filters_combined()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "Beacon");
        var noise = await Db.AddIotItemAsync(quantity: 100, configure: i => i.Name = "Other");
        var match = await Db.AddWithdrawalAsync(item.Id, user.Id, w =>
        {
            w.WithdrawnAt = new DateTime(2025, 6, 1, 9, 0, 0, DateTimeKind.Utc);
            w.Purpose = "deploy";
        });
        // ตรง item+search+purpose แต่อยู่นอกช่วงวันที่
        await Db.AddWithdrawalAsync(item.Id, user.Id, w =>
        {
            w.WithdrawnAt = new DateTime(2025, 7, 1, 9, 0, 0, DateTimeKind.Utc);
            w.Purpose = "deploy";
        });
        // อยู่ในช่วงแต่คนละ item
        await Db.AddWithdrawalAsync(noise.Id, user.Id, w =>
        {
            w.WithdrawnAt = new DateTime(2025, 6, 1, 9, 0, 0, DateTimeKind.Utc);
            w.Purpose = "deploy";
        });
        var svc = Service(AppRoles.Admin);

        var list = await svc.GetWithdrawalsAsync(
            from: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            itemId: item.Id,
            search: "beacon");

        Assert.Single(list);
        Assert.Equal(match.Id, list[0].Id);
    }

    // ============================================================
    //  DeleteAsync — permissions
    // ============================================================

    [Fact]
    public async Task Delete_admin_can_delete()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 90);
        var w = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 10);
        var svc = Service(AppRoles.Admin);

        var result = await svc.DeleteAsync(w.Id);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Delete_purchaser_can_delete()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 90);
        var w = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 10);
        var svc = Service(AppRoles.Purchaser);

        var result = await svc.DeleteAsync(w.Id);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Delete_staff_cannot_delete()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 90);
        var w = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 10);
        var svc = Service(AppRoles.Staff);

        var result = await svc.DeleteAsync(w.Id);

        // STAFF เบิกได้แต่ลบไม่ได้
        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
        Assert.Equal(1, await Db.CountAsync(c => c.Withdrawals)); // ยังอยู่
    }

    [Fact]
    public async Task Delete_viewer_cannot_delete()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 90);
        var w = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 10);
        var svc = Service(AppRoles.Viewer);

        var result = await svc.DeleteAsync(w.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task Delete_anonymous_cannot_delete()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 90);
        var w = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 10);
        var svc = AnonService();

        var result = await svc.DeleteAsync(w.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task Delete_permission_checked_before_existence()
    {
        // ไม่มี row id=999 แต่ผู้ใช้ไม่มีสิทธิ์ -> ต้องได้ Forbidden ไม่ใช่ not-found
        var svc = Service(AppRoles.Staff);
        var result = await svc.DeleteAsync(999);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    // ============================================================
    //  DeleteAsync — behavior
    // ============================================================

    [Fact]
    public async Task Delete_not_found_returns_error()
    {
        var svc = Service(AppRoles.Admin);

        var result = await svc.DeleteAsync(424242);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการเบิก", result.Error);
    }

    [Fact]
    public async Task Delete_iot_withdrawal_returns_stock_and_removes_row()
    {
        var user = await Db.AddUserAsync();
        // arrange a state consistent with a prior withdrawal: stock already decremented
        var item = await Db.AddIotItemAsync(quantity: 38); // เหมือนหลังเบิก 12 จาก 50
        var w = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 12);
        var svc = Service(AppRoles.Admin);

        var result = await svc.DeleteAsync(w.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        Assert.Equal(50, (await ctx.Items.FindAsync(item.Id))!.Quantity); // 38 + 12 คืนสต็อก
        Assert.Equal(0, await ctx.Withdrawals.CountAsync());              // row หาย
    }

    [Fact]
    public async Task Delete_record_then_delete_restores_original_stock_roundtrip()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Admin);

        var rec = await svc.RecordWithdrawalAsync(item.Id, 20, user.Id, null, DateTime.UtcNow, null);
        Assert.True(rec.Success, rec.Error);
        var del = await svc.DeleteAsync(rec.Value!.Id);
        Assert.True(del.Success, del.Error);

        await using var ctx = Db.NewContext();
        Assert.Equal(50, (await ctx.Items.FindAsync(item.Id))!.Quantity); // กลับเท่าเดิม
        Assert.Equal(0, await ctx.Withdrawals.CountAsync());
    }

    [Fact]
    public async Task Delete_returned_stock_can_exceed_prior_quantity_no_cap()
    {
        // หากสต็อกถูกแก้ไปทางอื่นหลังเบิก การลบจะบวกกลับแบบไม่จำกัดเพดาน
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100); // มีเพิ่มเข้ามาภายหลัง
        var w = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 5);
        var svc = Service(AppRoles.Admin);

        var result = await svc.DeleteAsync(w.Id);

        // AUDIT[low]: DeleteAsync บวกคืนสต็อกแบบไม่มีการตรวจว่ารายการเบิกนี้เคยหักไปจริงไหม
        // หากแก้สต็อก/นำเข้าใหม่ในระหว่างนั้น การลบจะทำให้ยอดเกินจริง (double count)
        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        Assert.Equal(105, (await ctx.Items.FindAsync(item.Id))!.Quantity);
    }

    [Fact]
    public async Task Delete_double_delete_second_is_not_found()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 90);
        var w = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 10);
        var svc = Service(AppRoles.Admin);

        Assert.True((await svc.DeleteAsync(w.Id)).Success);
        var second = await svc.DeleteAsync(w.Id);

        Assert.False(second.Success);
        Assert.Equal("ไม่พบรายการเบิก", second.Error);
        await using var ctx = Db.NewContext();
        Assert.Equal(100, (await ctx.Items.FindAsync(item.Id))!.Quantity); // คืนแค่ครั้งเดียว
    }

    [Fact]
    public async Task Delete_only_targets_specified_row()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 80);
        var keep = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 5);
        var drop = await Db.AddWithdrawalAsync(item.Id, user.Id, x => x.Quantity = 15);
        var svc = Service(AppRoles.Admin);

        var result = await svc.DeleteAsync(drop.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        Assert.Equal(95, (await ctx.Items.FindAsync(item.Id))!.Quantity); // 80 + 15
        Assert.Equal(1, await ctx.Withdrawals.CountAsync());
        Assert.NotNull(await ctx.Withdrawals.FindAsync(keep.Id));
    }
}
