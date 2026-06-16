using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>
/// ครอบคลุม PurchaseService ทั้งหมด: GetPurchasesAsync (filter/search/order/include),
/// RecordPurchaseAsync (สิทธิ์/validate/stock-seat effects), DeleteAsync (สิทธิ์/not-found/reverse stock)
/// </summary>
public class PurchaseServiceTests(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    private PurchaseService Service(params string[] roles) =>
        new(Db.Factory, Db.Accessor(roles));

    private PurchaseService AdminService() => Service(AppRoles.Admin);

    // ===================================================================
    // RecordPurchaseAsync — permission
    // ===================================================================

    [Fact]
    public async Task RecordPurchase_anonymous_is_forbidden()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = new PurchaseService(Db.Factory, Db.Anonymous());

        var result = await svc.RecordPurchaseAsync(item.Id, 5, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
        Assert.Null(result.Value);
        // ไม่มี purchase ถูกบันทึก
        Assert.Equal(0, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task RecordPurchase_staff_role_is_forbidden()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Staff);

        var result = await svc.RecordPurchaseAsync(item.Id, 5, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task RecordPurchase_viewer_role_is_forbidden()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Viewer);

        var result = await svc.RecordPurchaseAsync(item.Id, 5, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task RecordPurchase_purchaser_role_is_allowed()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var svc = Service(AppRoles.Purchaser);

        var result = await svc.RecordPurchaseAsync(item.Id, 5, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
    }

    // ===================================================================
    // RecordPurchaseAsync — validation
    // ===================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task RecordPurchase_quantity_below_one_is_rejected(int qty)
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);

        var result = await AdminService().RecordPurchaseAsync(item.Id, qty, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("จำนวนต้องมากกว่า 0", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task RecordPurchase_quantity_exactly_one_is_accepted()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);

        var result = await AdminService().RecordPurchaseAsync(item.Id, 1, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.Value!.Quantity);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-1)]
    [InlineData(-9999.99)]
    public async Task RecordPurchase_negative_unit_price_is_rejected(double priceD)
    {
        var price = (decimal)priceD;
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);

        var result = await AdminService().RecordPurchaseAsync(item.Id, 5, price, null, user.Id, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("ราคาต่อหน่วยต้องไม่ติดลบ", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task RecordPurchase_zero_unit_price_is_accepted()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);

        // AUDIT[low]: unitPrice == 0 ถูกยอมรับ (TotalCost = 0) — ของแถม/free tier บันทึกได้ ไม่มีการกันความผิดพลาดป้อนราคาว่าง
        var result = await AdminService().RecordPurchaseAsync(item.Id, 5, 0m, null, user.Id, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0m, result.Value!.UnitPrice);
        Assert.Equal(0m, result.Value!.TotalCost);
    }

    [Fact]
    public async Task RecordPurchase_item_not_found_is_rejected()
    {
        var user = await Db.AddUserAsync();

        var result = await AdminService().RecordPurchaseAsync(999999, 5, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการสินค้า", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task RecordPurchase_validation_runs_before_item_lookup_so_bad_quantity_with_missing_item_reports_quantity_error()
    {
        var user = await Db.AddUserAsync();

        // quantity invalid AND item missing — code validates quantity first
        var result = await AdminService().RecordPurchaseAsync(999999, 0, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.False(result.Success);
        Assert.Equal("จำนวนต้องมากกว่า 0", result.Error);
    }

    // ===================================================================
    // RecordPurchaseAsync — stock / seat effects + fields
    // ===================================================================

    [Fact]
    public async Task RecordPurchase_iot_increments_item_quantity_by_qty()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100, minQuantity: 10);

        var result = await AdminService().RecordPurchaseAsync(item.Id, 25, 5m, null, user.Id, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(125, reloaded.Quantity);   // 100 + 25
        Assert.Null(reloaded.TotalSeats);        // IoT ไม่แตะ seat
    }

    [Fact]
    public async Task RecordPurchase_iot_does_not_touch_total_seats()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);

        await AdminService().RecordPurchaseAsync(item.Id, 5, 5m, null, user.Id, DateTime.UtcNow, null);

        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Null(reloaded.TotalSeats);
    }

    [Fact]
    public async Task RecordPurchase_software_increments_total_seats()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);

        await AdminService().RecordPurchaseAsync(item.Id, 3, 200m, null, user.Id, DateTime.UtcNow, null);

        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(13, reloaded.TotalSeats);   // 10 + 3
        Assert.Equal(0, reloaded.Quantity);      // seat item ไม่แตะ Quantity
    }

    [Fact]
    public async Task RecordPurchase_server_increments_total_seats()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 4);

        await AdminService().RecordPurchaseAsync(item.Id, 2, 1000m, null, user.Id, DateTime.UtcNow, null);

        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(6, reloaded.TotalSeats);
    }

    [Fact]
    public async Task RecordPurchase_seat_item_with_null_total_seats_is_treated_as_zero()
    {
        var user = await Db.AddUserAsync();
        // seat item แต่ TotalSeats = null (ยังไม่เคยตั้งค่า)
        var item = await Db.AddItemAsync(i =>
        {
            i.Type = ItemType.Software;
            i.CostType = CostType.OneTime;
            i.TotalSeats = null;
        });

        await AdminService().RecordPurchaseAsync(item.Id, 7, 50m, null, user.Id, DateTime.UtcNow, null);

        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(7, reloaded.TotalSeats);   // (null -> 0) + 7
    }

    [Fact]
    public async Task RecordPurchase_other_type_touches_neither_quantity_nor_seats()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddItemAsync(i =>
        {
            i.Type = ItemType.Other;
            i.CostType = CostType.OneTime;
            i.Quantity = 3;
            i.TotalSeats = null;
        });

        var result = await AdminService().RecordPurchaseAsync(item.Id, 9, 12m, null, user.Id, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        // AUDIT[low]: Type=Other ยังบันทึก purchase (quantity 9) ได้แต่ไม่อัปเดต stock/seat ใดเลย
        Assert.Equal(3, reloaded.Quantity);
        Assert.Null(reloaded.TotalSeats);
    }

    [Fact]
    public async Task RecordPurchase_total_cost_is_quantity_times_unit_price()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 0);

        var result = await AdminService().RecordPurchaseAsync(item.Id, 7, 12.50m, null, user.Id, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(87.50m, result.Value!.TotalCost);   // 7 * 12.50
        Assert.Equal(7, result.Value!.Quantity);
        Assert.Equal(12.50m, result.Value!.UnitPrice);
    }

    [Fact]
    public async Task RecordPurchase_persisted_total_cost_rounds_to_two_decimals()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 0);

        // 3 * 0.333 = 0.999 -> decimal(12,2) rounds when persisted
        var result = await AdminService().RecordPurchaseAsync(item.Id, 3, 0.333m, null, user.Id, DateTime.UtcNow, null);
        Assert.True(result.Success, result.Error);

        await using var ctx = Db.NewContext();
        var stored = await ctx.Purchases.FirstAsync(p => p.Id == result.Value!.Id);
        // AUDIT[low]: TotalCost computed = 0.999 แต่คอลัมน์ decimal(12,2) ปัดเป็น 1.00 ทำให้ค่าในหน่วยความจำกับใน DB ต่างกัน
        Assert.Equal(1.00m, stored.TotalCost);
        Assert.Equal(0.33m, stored.UnitPrice);   // 0.333 -> 0.33
    }

    [Fact]
    public async Task RecordPurchase_sets_is_recurring_charge_false()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 0);

        var result = await AdminService().RecordPurchaseAsync(item.Id, 1, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        Assert.False(result.Value!.IsRecurringCharge);
    }

    [Fact]
    public async Task RecordPurchase_updates_item_updated_at()
    {
        var user = await Db.AddUserAsync();
        var oldStamp = DateTime.UtcNow.AddDays(-30);
        var item = await Db.AddIotItemAsync(quantity: 0, configure: i => i.UpdatedAt = oldStamp);

        await AdminService().RecordPurchaseAsync(item.Id, 1, 10m, null, user.Id, DateTime.UtcNow, null);

        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.True(reloaded.UpdatedAt > oldStamp);
    }

    [Fact]
    public async Task RecordPurchase_persists_supplier_note_date_and_purchaser()
    {
        var user = await Db.AddUserAsync();
        var supplier = await Db.AddSupplierAsync("ผู้ขายA");
        var item = await Db.AddIotItemAsync(quantity: 0);
        var theDate = new DateTime(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc);

        var result = await AdminService().RecordPurchaseAsync(
            item.Id, 2, 5m, supplier.Id, user.Id, theDate, "หมายเหตุการซื้อ");

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var stored = await ctx.Purchases.FirstAsync(p => p.Id == result.Value!.Id);
        Assert.Equal(supplier.Id, stored.SupplierId);
        Assert.Equal("หมายเหตุการซื้อ", stored.Note);
        Assert.Equal(user.Id, stored.PurchasedById);
        Assert.Equal(theDate, stored.Date);
    }

    [Fact]
    public async Task RecordPurchase_allows_null_supplier_and_null_note()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 0);

        var result = await AdminService().RecordPurchaseAsync(item.Id, 1, 10m, null, user.Id, DateTime.UtcNow, null);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var stored = await ctx.Purchases.FirstAsync(p => p.Id == result.Value!.Id);
        Assert.Null(stored.SupplierId);
        Assert.Null(stored.Note);
    }

    [Fact]
    public async Task RecordPurchase_accepts_future_date_without_validation()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 0);
        var future = DateTime.UtcNow.AddYears(5);

        // AUDIT[low]: ไม่มีการตรวจสอบว่า date เป็นอนาคต — บันทึกค่าใช้จ่ายล่วงหน้าได้ ทำให้รายงานเพี้ยน
        var result = await AdminService().RecordPurchaseAsync(item.Id, 1, 10m, null, user.Id, future, null);

        Assert.True(result.Success, result.Error);
    }

    // ===================================================================
    // GetPurchasesAsync — no filter / ordering / includes
    // ===================================================================

    [Fact]
    public async Task GetPurchases_no_filter_returns_all()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id);
        await Db.AddPurchaseAsync(item.Id, user.Id);
        await Db.AddPurchaseAsync(item.Id, user.Id);

        var list = await AdminService().GetPurchasesAsync();

        Assert.Equal(3, list.Count);
    }

    [Fact]
    public async Task GetPurchases_empty_db_returns_empty_list()
    {
        var list = await AdminService().GetPurchasesAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetPurchases_is_readable_without_management_permission()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id);
        var svc = new PurchaseService(Db.Factory, Db.Anonymous());

        // AUDIT[medium]: GetPurchasesAsync ไม่ตรวจสิทธิ์เลย — ผู้ใช้ที่ไม่มี role ใด ๆ (anonymous) อ่านประวัติค่าใช้จ่ายทั้งหมดได้
        var list = await svc.GetPurchasesAsync();

        Assert.Single(list);
    }

    [Fact]
    public async Task GetPurchases_orders_by_date_desc_then_id_desc()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        var day1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        var older = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = day1);
        // สองรายการวันเดียวกัน -> tie-break ด้วย Id desc
        var sameA = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = day2);
        var sameB = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = day2);

        var list = await AdminService().GetPurchasesAsync();

        Assert.Equal(3, list.Count);
        Assert.Equal(sameB.Id, list[0].Id);   // วันเดียวกัน Id ใหญ่กว่าอยู่ก่อน
        Assert.Equal(sameA.Id, list[1].Id);
        Assert.Equal(older.Id, list[2].Id);
    }

    [Fact]
    public async Task GetPurchases_includes_item_supplier_and_purchaser_navigations()
    {
        var user = await Db.AddUserAsync("คนซื้อชื่อจริง");
        var supplier = await Db.AddSupplierAsync("ผู้ขายรวมในผล");
        var item = await Db.AddIotItemAsync(configure: i => i.Name = "สินค้ารวมในผล");
        await Db.AddPurchaseAsync(item.Id, user.Id, p => p.SupplierId = supplier.Id);

        var list = await AdminService().GetPurchasesAsync();

        var row = Assert.Single(list);
        Assert.NotNull(row.Item);
        Assert.Equal("สินค้ารวมในผล", row.Item.Name);
        Assert.NotNull(row.Supplier);
        Assert.Equal("ผู้ขายรวมในผล", row.Supplier!.Name);
        Assert.NotNull(row.PurchasedBy);
        Assert.Equal("คนซื้อชื่อจริง", row.PurchasedBy.FullName);
    }

    // ===================================================================
    // GetPurchasesAsync — from / to date boundaries
    // ===================================================================

    [Fact]
    public async Task GetPurchases_from_filter_is_inclusive_of_from_instant()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        var from = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var before = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = from.AddSeconds(-1));
        var atFrom = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = from);
        var after = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = from.AddDays(1));

        var list = await AdminService().GetPurchasesAsync(from: from);

        var ids = list.Select(p => p.Id).ToHashSet();
        Assert.Contains(atFrom.Id, ids);   // >= from inclusive
        Assert.Contains(after.Id, ids);
        Assert.DoesNotContain(before.Id, ids);
    }

    [Fact]
    public async Task GetPurchases_to_filter_includes_entire_to_day_end()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        var to = new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc);

        // ผู้ใช้ส่ง to เป็นเที่ยงคืนของวันที่ 20 แต่โค้ดใช้ < to.Date.AddDays(1) จึงรวมทั้งวันที่ 20
        var earlyDay = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = to.AddHours(1));
        var lateDay = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = to.AddHours(23).AddMinutes(59));
        var nextDay = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = to.AddDays(1));

        var list = await AdminService().GetPurchasesAsync(to: to);

        var ids = list.Select(p => p.Id).ToHashSet();
        Assert.Contains(earlyDay.Id, ids);
        Assert.Contains(lateDay.Id, ids);    // 23:59 ของวัน to ยังถูกรวม (inclusive whole day)
        Assert.DoesNotContain(nextDay.Id, ids);
    }

    [Fact]
    public async Task GetPurchases_to_filter_ignores_time_component_of_to_using_date_only()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        // ส่ง to กลางวัน (12:00) — code เรียก to.Date จึงตัดเวลาทิ้งและรวมทั้งวันอยู่ดี
        var to = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var afterToTime = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = new DateTime(2026, 5, 5, 18, 0, 0, DateTimeKind.Utc));

        var list = await AdminService().GetPurchasesAsync(to: to);

        // AUDIT[low]: to ใช้ .Date ตัดเวลา -> รายการเวลา 18:00 ของวันเดียวกันถูกรวมแม้ผู้เรียกส่ง to=12:00
        Assert.Contains(afterToTime.Id, list.Select(p => p.Id));
    }

    [Fact]
    public async Task GetPurchases_from_and_to_combined_window()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);
        var inside = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        var beforeWindow = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = new DateTime(2026, 5, 31, 23, 0, 0, DateTimeKind.Utc));
        var afterWindow = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Date = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var list = await AdminService().GetPurchasesAsync(from: from, to: to);

        var ids = list.Select(p => p.Id).ToHashSet();
        Assert.Contains(inside.Id, ids);
        Assert.DoesNotContain(beforeWindow.Id, ids);
        Assert.DoesNotContain(afterWindow.Id, ids);
    }

    // ===================================================================
    // GetPurchasesAsync — itemId / type / recurringOnly filters
    // ===================================================================

    [Fact]
    public async Task GetPurchases_filters_by_item_id()
    {
        var user = await Db.AddUserAsync();
        var itemA = await Db.AddIotItemAsync();
        var itemB = await Db.AddIotItemAsync();
        var pA = await Db.AddPurchaseAsync(itemA.Id, user.Id);
        await Db.AddPurchaseAsync(itemB.Id, user.Id);

        var list = await AdminService().GetPurchasesAsync(itemId: itemA.Id);

        var row = Assert.Single(list);
        Assert.Equal(pA.Id, row.Id);
    }

    [Fact]
    public async Task GetPurchases_filters_by_item_type()
    {
        var user = await Db.AddUserAsync();
        var iot = await Db.AddIotItemAsync();
        var software = await Db.AddSeatItemAsync(ItemType.Software);
        var server = await Db.AddSeatItemAsync(ItemType.Server);
        var iotP = await Db.AddPurchaseAsync(iot.Id, user.Id);
        var swP = await Db.AddPurchaseAsync(software.Id, user.Id);
        await Db.AddPurchaseAsync(server.Id, user.Id);

        var softwareList = await AdminService().GetPurchasesAsync(type: ItemType.Software);
        Assert.Equal(swP.Id, Assert.Single(softwareList).Id);

        var iotList = await AdminService().GetPurchasesAsync(type: ItemType.IotMaterial);
        Assert.Equal(iotP.Id, Assert.Single(iotList).Id);
    }

    [Fact]
    public async Task GetPurchases_recurring_only_true_returns_only_recurring_charges()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();
        var recurring = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = true;
            p.Quantity = 0;
        });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => p.IsRecurringCharge = false);

        var list = await AdminService().GetPurchasesAsync(recurringOnly: true);

        Assert.Equal(recurring.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task GetPurchases_recurring_only_false_returns_only_non_recurring()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => p.IsRecurringCharge = true);
        var oneTime = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.IsRecurringCharge = false);

        var list = await AdminService().GetPurchasesAsync(recurringOnly: false);

        Assert.Equal(oneTime.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task GetPurchases_recurring_only_null_returns_both()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => p.IsRecurringCharge = true);
        await Db.AddPurchaseAsync(item.Id, user.Id, p => p.IsRecurringCharge = false);

        var list = await AdminService().GetPurchasesAsync(recurringOnly: null);

        Assert.Equal(2, list.Count);
    }

    // ===================================================================
    // GetPurchasesAsync — search (ILike, case-insensitive, item name + note)
    // ===================================================================

    [Fact]
    public async Task GetPurchases_search_matches_item_name()
    {
        var user = await Db.AddUserAsync();
        var match = await Db.AddIotItemAsync(configure: i => i.Name = "เซ็นเซอร์อุณหภูมิ");
        var noMatch = await Db.AddIotItemAsync(configure: i => i.Name = "สายไฟ");
        var p1 = await Db.AddPurchaseAsync(match.Id, user.Id);
        await Db.AddPurchaseAsync(noMatch.Id, user.Id);

        var list = await AdminService().GetPurchasesAsync(search: "เซ็นเซอร์");

        Assert.Equal(p1.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task GetPurchases_search_matches_note()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(configure: i => i.Name = "ของทั่วไป");
        var withNote = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Note = "ซื้อด่วนพิเศษ");
        await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Note = "ปกติ");

        var list = await AdminService().GetPurchasesAsync(search: "ด่วนพิเศษ");

        Assert.Equal(withNote.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task GetPurchases_search_is_case_insensitive_via_ilike()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(configure: i => i.Name = "Arduino UNO Board");
        var p1 = await Db.AddPurchaseAsync(item.Id, user.Id);

        var lower = await AdminService().GetPurchasesAsync(search: "arduino");
        var upper = await AdminService().GetPurchasesAsync(search: "ARDUINO");
        var mixed = await AdminService().GetPurchasesAsync(search: "uno BOARD");

        Assert.Equal(p1.Id, Assert.Single(lower).Id);
        Assert.Equal(p1.Id, Assert.Single(upper).Id);
        Assert.Equal(p1.Id, Assert.Single(mixed).Id);
    }

    [Fact]
    public async Task GetPurchases_search_matches_substring_anywhere()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(configure: i => i.Name = "ตัวต้านทาน 10k ohm");
        var p1 = await Db.AddPurchaseAsync(item.Id, user.Id);

        var list = await AdminService().GetPurchasesAsync(search: "10k");

        Assert.Equal(p1.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task GetPurchases_search_trims_whitespace()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(configure: i => i.Name = "รีเลย์");
        var p1 = await Db.AddPurchaseAsync(item.Id, user.Id);

        var list = await AdminService().GetPurchasesAsync(search: "   รีเลย์   ");

        Assert.Equal(p1.Id, Assert.Single(list).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetPurchases_blank_or_null_search_returns_all(string? search)
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(configure: i => i.Name = "อะไรก็ได้");
        await Db.AddPurchaseAsync(item.Id, user.Id);
        await Db.AddPurchaseAsync(item.Id, user.Id);

        var list = await AdminService().GetPurchasesAsync(search: search);

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task GetPurchases_search_with_null_note_does_not_throw_and_matches_only_name()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(configure: i => i.Name = "หลอดไฟ LED");
        // Note = null -> guard p.Note != null ป้องกัน null ใน ILike
        var p1 = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Note = null);

        var list = await AdminService().GetPurchasesAsync(search: "LED");

        Assert.Equal(p1.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task GetPurchases_combined_filters_apply_together()
    {
        var user = await Db.AddUserAsync();
        var sw = await Db.AddSeatItemAsync(ItemType.Software, configure: i => i.Name = "Office License");
        var iot = await Db.AddIotItemAsync(configure: i => i.Name = "Office Chair");
        var from = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc);

        // ตรงทุกเงื่อนไข: type=Software, search "office", ในช่วง, non-recurring
        var match = await Db.AddPurchaseAsync(sw.Id, user.Id, p =>
        {
            p.Date = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
            p.IsRecurringCharge = false;
        });
        // ผิด type
        await Db.AddPurchaseAsync(iot.Id, user.Id, p => p.Date = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));
        // นอกช่วง
        await Db.AddPurchaseAsync(sw.Id, user.Id, p => p.Date = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc));
        // recurring
        await Db.AddPurchaseAsync(sw.Id, user.Id, p =>
        {
            p.Date = new DateTime(2026, 2, 12, 0, 0, 0, DateTimeKind.Utc);
            p.IsRecurringCharge = true;
        });

        var list = await AdminService().GetPurchasesAsync(
            from: from, to: to, type: ItemType.Software, recurringOnly: false, search: "office");

        Assert.Equal(match.Id, Assert.Single(list).Id);
    }

    // ===================================================================
    // DeleteAsync — permission / not-found
    // ===================================================================

    [Fact]
    public async Task Delete_anonymous_is_forbidden()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Quantity = 10);
        var svc = new PurchaseService(Db.Factory, Db.Anonymous());

        var result = await svc.DeleteAsync(purchase.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
        Assert.Equal(1, await Db.CountAsync(c => c.Purchases));   // ยังไม่ถูกลบ
    }

    [Fact]
    public async Task Delete_staff_is_forbidden()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id);
        var svc = Service(AppRoles.Staff);

        var result = await svc.DeleteAsync(purchase.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task Delete_purchaser_is_allowed()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 50);
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Quantity = 5);
        var svc = Service(AppRoles.Purchaser);

        var result = await svc.DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task Delete_not_found_is_rejected()
    {
        var result = await AdminService().DeleteAsync(999999);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการค่าใช้จ่าย", result.Error);
    }

    [Fact]
    public async Task Delete_not_found_takes_precedence_when_permitted_user_but_missing_row()
    {
        // ผู้ใช้มีสิทธิ์ แต่ id ไม่มีจริง
        var result = await AdminService().DeleteAsync(123456);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการค่าใช้จ่าย", result.Error);
    }

    [Fact]
    public async Task Delete_missing_row_check_runs_after_permission_so_anonymous_gets_forbidden_not_notfound()
    {
        var svc = new PurchaseService(Db.Factory, Db.Anonymous());

        var result = await svc.DeleteAsync(999999);

        // permission ถูกตรวจก่อน existence
        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    // ===================================================================
    // DeleteAsync — stock / seat reversal
    // ===================================================================

    [Fact]
    public async Task Delete_non_recurring_iot_purchase_reverses_stock()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = false;
            p.Quantity = 30;
        });

        var result = await AdminService().DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(70, reloaded.Quantity);   // 100 - 30
        Assert.Equal(0, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task Delete_iot_floors_stock_at_zero_when_already_reduced_below_quantity()
    {
        var user = await Db.AddUserAsync();
        // สต็อกปัจจุบัน 5 แต่ purchase บันทึกว่าเคยเติม 30 (สมมติถูกเบิกไปแล้ว)
        var item = await Db.AddIotItemAsync(quantity: 5);
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = false;
            p.Quantity = 30;
        });

        var result = await AdminService().DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        // AUDIT[medium]: Math.Max(0, 5-30) -> 0 แทนที่จะติดลบ ดูเหมือนปลอดภัย แต่ "กลืน" จำนวน 25 ที่หายไป
        // ทำให้สต็อกหลังลบ purchase ผิดความจริง (ควรเป็นค่าก่อนเติมแต่กลับเป็น 0)
        Assert.Equal(0, reloaded.Quantity);
    }

    [Fact]
    public async Task Delete_non_recurring_software_purchase_reverses_seats()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 20);
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = false;
            p.Quantity = 8;
        });

        var result = await AdminService().DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(12, reloaded.TotalSeats);   // 20 - 8
    }

    [Fact]
    public async Task Delete_non_recurring_server_purchase_reverses_seats()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 6);
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = false;
            p.Quantity = 2;
        });

        var result = await AdminService().DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(4, reloaded.TotalSeats);
    }

    [Fact]
    public async Task Delete_seat_reversal_floors_at_zero()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 3);
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = false;
            p.Quantity = 10;
        });

        var result = await AdminService().DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        // AUDIT[medium]: Math.Max(0, 3-10) -> 0 floored; ที่นั่งจริงที่ขายเกินไม่ถูกสะท้อน
        Assert.Equal(0, reloaded.TotalSeats);
    }

    [Fact]
    public async Task Delete_seat_reversal_with_null_total_seats_is_treated_as_zero()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddItemAsync(i =>
        {
            i.Type = ItemType.Server;
            i.CostType = CostType.OneTime;
            i.TotalSeats = null;
        });
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = false;
            p.Quantity = 4;
        });

        var result = await AdminService().DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        // Math.Max(0, (null->0) - 4) = 0
        Assert.Equal(0, reloaded.TotalSeats);
    }

    [Fact]
    public async Task Delete_recurring_charge_does_not_touch_stock()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();
        // recurring charge: quantity บันทึกค่าผิดปกติ แต่ต้องไม่ถูกนำมาหักสต็อก
        await Db.AddItemAsync();   // กันสับสน (ไม่เกี่ยว)
        var startQty = item.Quantity;
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = true;
            p.Quantity = 99;
        });

        var result = await AdminService().DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(startQty, reloaded.Quantity);   // ไม่เปลี่ยน
        Assert.Equal(0, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task Delete_recurring_charge_on_iot_item_does_not_reduce_quantity()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = true;
            p.Quantity = 40;
        });

        var result = await AdminService().DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(100, reloaded.Quantity);   // recurring -> ไม่หักสต็อก
    }

    [Fact]
    public async Task Delete_other_type_purchase_removes_row_without_stock_change()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddItemAsync(i =>
        {
            i.Type = ItemType.Other;
            i.CostType = CostType.OneTime;
            i.Quantity = 7;
            i.TotalSeats = null;
        });
        var purchase = await Db.AddPurchaseAsync(item.Id, user.Id, p =>
        {
            p.IsRecurringCharge = false;
            p.Quantity = 3;
        });

        var result = await AdminService().DeleteAsync(purchase.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(7, reloaded.Quantity);   // Type=Other -> ไม่แตะ
        Assert.Null(reloaded.TotalSeats);
        Assert.Equal(0, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task Delete_removes_only_targeted_row()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        var keep = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Quantity = 5);
        var drop = await Db.AddPurchaseAsync(item.Id, user.Id, p => p.Quantity = 5);

        var result = await AdminService().DeleteAsync(drop.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var remaining = await ctx.Purchases.Select(p => p.Id).ToListAsync();
        Assert.Equal(new[] { keep.Id }, remaining);
    }

    // ===================================================================
    // Round-trip: record then delete restores original stock
    // ===================================================================

    [Fact]
    public async Task Record_then_delete_iot_restores_original_stock()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 100);
        var svc = AdminService();

        var rec = await svc.RecordPurchaseAsync(item.Id, 15, 3m, null, user.Id, DateTime.UtcNow, null);
        Assert.True(rec.Success, rec.Error);

        var del = await svc.DeleteAsync(rec.Value!.Id);
        Assert.True(del.Success, del.Error);

        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(100, reloaded.Quantity);   // กลับสู่ค่าเดิม
    }

    [Fact]
    public async Task Record_then_delete_software_restores_original_seats()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);
        var svc = AdminService();

        var rec = await svc.RecordPurchaseAsync(item.Id, 6, 99m, null, user.Id, DateTime.UtcNow, null);
        Assert.True(rec.Success, rec.Error);

        var del = await svc.DeleteAsync(rec.Value!.Id);
        Assert.True(del.Success, del.Error);

        await using var ctx = Db.NewContext();
        var reloaded = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(10, reloaded.TotalSeats);
    }
}
