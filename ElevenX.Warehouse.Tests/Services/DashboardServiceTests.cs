using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>
/// ครอบคลุม DashboardService.GetSummaryAsync ทุก aggregate / branch:
/// IotStockValue, ThisMonth/LastMonthSpend windows, subscriptions, upcoming renewals,
/// low stock, seat usage, 6-month series, recent activity, และ empty-DB.
/// แต่ละ test arrange scenario เล็ก ๆ ที่ควบคุมได้ แล้ว assert ค่าที่เจาะจง
/// </summary>
public class DashboardServiceTests(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    private DashboardService Service() => new(Db.Factory);

    // วันที่อ้างอิงเหมือนที่ service ใช้ภายใน (DateTime.UtcNow.Date / month boundaries)
    private static DateTime Today => DateTime.UtcNow.Date;
    private static DateTime MonthStart => new(Today.Year, Today.Month, 1);

    // ======================================================================
    // Empty DB
    // ======================================================================

    [Fact]
    public async Task GetSummaryAsync_on_empty_db_returns_zeros_and_empty_lists()
    {
        var summary = await Service().GetSummaryAsync();

        Assert.Equal(0m, summary.IotStockValue);
        Assert.Equal(0, summary.IotItemCount);
        Assert.Equal(0m, summary.ThisMonthSpend);
        Assert.Equal(0m, summary.LastMonthSpend);
        Assert.Equal(0m, summary.MonthlySubscriptionTotal);
        Assert.Equal(0, summary.ActiveSubscriptions);
        Assert.Equal(0, summary.LowStockCount);
        Assert.Equal(0, summary.SeatsNearFullCount);
        Assert.Equal(0, summary.UsedSeatsTotal);
        Assert.Equal(0, summary.TotalSeatsTotal);

        Assert.Empty(summary.UpcomingRenewals);
        Assert.Empty(summary.LowStockItems);
        Assert.Empty(summary.SeatsNearFull);
        Assert.Empty(summary.RecentActivity);

        // series ต้องเป็น 6 จุดเสมอแม้ DB ว่าง
        Assert.Equal(6, summary.MonthlySpendSeries.Count);
        Assert.All(summary.MonthlySpendSeries, p => Assert.Equal(0m, p.Total));
    }

    // ======================================================================
    // IotStockValue — weighted average cost
    // ======================================================================

    [Fact]
    public async Task IotStockValue_uses_weighted_average_of_purchases_times_current_quantity()
    {
        var user = await Db.AddUserAsync();
        // current stock = 50 ชิ้น
        var item = await Db.AddIotItemAsync(quantity: 50, minQuantity: 0);

        // ซื้อสองครั้งราคาต่างกัน: 10 ชิ้น @ TotalCost 100 + 30 ชิ้น @ TotalCost 600
        // weighted avg = (100 + 600) / (10 + 30) = 700/40 = 17.5 ต่อชิ้น
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Quantity = 10; p.TotalCost = 100m; p.UnitPrice = 10m; });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Quantity = 30; p.TotalCost = 600m; p.UnitPrice = 20m; });

        var summary = await Service().GetSummaryAsync();

        // 50 * 17.5 = 875
        Assert.Equal(875m, summary.IotStockValue);
        Assert.Equal(1, summary.IotItemCount);
    }

    [Fact]
    public async Task IotStockValue_excludes_recurring_charges_from_average()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 10, minQuantity: 0);

        // ซื้อจริง 10 ชิ้น @ 100 -> avg 10 ต่อชิ้น
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Quantity = 10; p.TotalCost = 100m; });
        // ค่ารอบ (IsRecurringCharge) ต้องไม่ถูกนับ แม้จะ Quantity>0
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.IsRecurringCharge = true; p.Quantity = 5; p.TotalCost = 9999m; });

        var summary = await Service().GetSummaryAsync();

        // 10 (stock) * 10 (avg) = 100 — ค่ารอบไม่กระทบ
        Assert.Equal(100m, summary.IotStockValue);
    }

    [Fact]
    public async Task IotStockValue_ignores_purchases_with_zero_quantity()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 4, minQuantity: 0);

        // purchase ที่ Quantity = 0 (เช่นค่าธรรมเนียม) ต้องถูกตัดออกจาก aggregate (p.Quantity > 0)
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Quantity = 0; p.TotalCost = 500m; });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Quantity = 8; p.TotalCost = 80m; }); // avg = 10

        var summary = await Service().GetSummaryAsync();

        // 4 * 10 = 40 ; ถ้า Quantity=0 ถูกนับจะกลายเป็น (500+80)/8 ซึ่งผิด
        Assert.Equal(40m, summary.IotStockValue);
    }

    [Fact]
    public async Task IotStockValue_is_zero_when_item_has_no_purchases()
    {
        // มีของในสต็อกแต่ไม่มีประวัติซื้อ -> avg = 0 -> มูลค่า = 0
        await Db.AddIotItemAsync(quantity: 99, minQuantity: 0);

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(0m, summary.IotStockValue);
        Assert.Equal(1, summary.IotItemCount);
    }

    [Fact]
    public async Task IotStockValue_only_counts_iot_items_not_software_or_server()
    {
        var user = await Db.AddUserAsync();
        // Software item ที่มี purchase และ Quantity ตั้งค่าไว้ — ต้องไม่นับเข้า IotStockValue
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Quantity = 100);
        await Db.AddPurchaseAsync(sw.Id, user.Id, p => { p.Quantity = 100; p.TotalCost = 5000m; });

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(0m, summary.IotStockValue);
        Assert.Equal(0, summary.IotItemCount); // ไม่ใช่ IotMaterial
    }

    [Fact]
    public async Task IotStockValue_with_negative_current_quantity_yields_negative_value()
    {
        var user = await Db.AddUserAsync();
        // สต็อกติดลบ (เบิกเกิน) — service ไม่ป้องกัน จึงได้มูลค่าติดลบ
        var item = await Db.AddIotItemAsync(quantity: -5, minQuantity: 0);
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Quantity = 10; p.TotalCost = 100m; }); // avg 10

        var summary = await Service().GetSummaryAsync();

        // AUDIT[low]: IotStockValue ไม่กันค่าติดลบ — สต็อกติดลบทำให้มูลค่ารวมเป็นลบ (-50)
        Assert.Equal(-50m, summary.IotStockValue);
    }

    // ======================================================================
    // ThisMonthSpend / LastMonthSpend windows
    // ======================================================================

    [Fact]
    public async Task ThisMonthSpend_sums_only_current_calendar_month_purchases()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 0, minQuantity: 0);

        // ภายในเดือนปัจจุบัน: วันที่ 1 ของเดือน และวันนี้
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = MonthStart; p.TotalCost = 100m; });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = Today; p.TotalCost = 250m; });
        // เดือนก่อน — ต้องไม่ถูกนับใน ThisMonth
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = MonthStart.AddDays(-1); p.TotalCost = 9999m; });

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(350m, summary.ThisMonthSpend);
    }

    [Fact]
    public async Task LastMonthSpend_sums_only_previous_calendar_month_purchases()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 0, minQuantity: 0);

        var lastMonthStart = MonthStart.AddMonths(-1);
        // ภายในเดือนก่อน: วันแรกของเดือนก่อน และวันสุดท้ายก่อน monthStart
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = lastMonthStart; p.TotalCost = 40m; });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = MonthStart.AddDays(-1); p.TotalCost = 60m; });
        // เดือนปัจจุบัน — ไม่นับใน LastMonth
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = Today; p.TotalCost = 9999m; });
        // ก่อนเดือนก่อนหน้านั้น (2 เดือนที่แล้ว) — ไม่นับใน LastMonth
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = lastMonthStart.AddDays(-1); p.TotalCost = 8888m; });

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(100m, summary.LastMonthSpend);
    }

    [Fact]
    public async Task MonthSpend_window_is_inclusive_of_month_start_exclusive_of_next_month()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 0, minQuantity: 0);

        // ขอบเขตล่างของเดือนนี้ (รวม) = monthStart 00:00
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = MonthStart; p.TotalCost = 11m; });
        // ขอบเขตบน (ไม่รวม) = วันแรกของเดือนถัดไป 00:00 — ต้องไม่ถูกนับในเดือนนี้
        var nextMonthStart = MonthStart.AddMonths(1);
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = nextMonthStart; p.TotalCost = 22m; });

        var summary = await Service().GetSummaryAsync();

        // เฉพาะ 11 อยู่ในเดือนนี้; 22 ตกไปเดือนถัดไป (อนาคต) ไม่อยู่ทั้ง this/last
        Assert.Equal(11m, summary.ThisMonthSpend);
        Assert.Equal(0m, summary.LastMonthSpend);
    }

    [Fact]
    public async Task MonthSpend_counts_all_item_types_including_recurring_charges()
    {
        var user = await Db.AddUserAsync();
        var iot = await Db.AddIotItemAsync(quantity: 0, minQuantity: 0);
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);

        await Db.AddPurchaseAsync(iot.Id, user.Id, p => { p.Date = Today; p.TotalCost = 100m; });
        await Db.AddPurchaseAsync(sw.Id, user.Id, p => { p.Date = Today; p.TotalCost = 200m; });
        // ค่ารอบก็ถูกนับใน spend รวม (window กรองแค่ Date ไม่กรอง IsRecurringCharge)
        await Db.AddPurchaseAsync(sw.Id, user.Id, p => { p.Date = Today; p.IsRecurringCharge = true; p.TotalCost = 50m; });

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(350m, summary.ThisMonthSpend);
    }

    // ======================================================================
    // Subscriptions: MonthlySubscriptionTotal & ActiveSubscriptions
    // ======================================================================

    [Fact]
    public async Task MonthlySubscriptionTotal_only_counts_active_recurring_with_monthly_equivalent()
    {
        // Monthly 1000 -> 1000 ; Quarterly 300 -> 100 ; Yearly 1200 -> 100  => รวม 1200
        await Db.AddSubscriptionAsync(amount: 1000m, cycle: BillingCycle.Monthly, status: SubscriptionStatus.Active);
        await Db.AddSubscriptionAsync(amount: 300m, cycle: BillingCycle.Quarterly, status: SubscriptionStatus.Active);
        await Db.AddSubscriptionAsync(amount: 1200m, cycle: BillingCycle.Yearly, status: SubscriptionStatus.Active);
        // Cancelled / Expired ต้องไม่ถูกนับ
        await Db.AddSubscriptionAsync(amount: 5000m, cycle: BillingCycle.Monthly, status: SubscriptionStatus.Cancelled);
        await Db.AddSubscriptionAsync(amount: 5000m, cycle: BillingCycle.Monthly, status: SubscriptionStatus.Expired);
        // OneTime item ที่มี RecurringAmount มั่ว ๆ — ไม่ใช่ Recurring จึงไม่ถูกนับ
        await Db.AddIotItemAsync(quantity: 0, configure: i => i.RecurringAmount = 9999m);

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(3, summary.ActiveSubscriptions);
        Assert.Equal(1200m, summary.MonthlySubscriptionTotal);
    }

    [Fact]
    public async Task ActiveSubscriptions_counts_recurring_of_any_item_type()
    {
        // CostType==Recurring filter ไม่กรอง ItemType — subscription แบบ Server/Other ก็ถูกนับ
        await Db.AddSubscriptionAsync(type: ItemType.Server, amount: 100m, cycle: BillingCycle.Monthly);
        await Db.AddSubscriptionAsync(type: ItemType.Other, amount: 200m, cycle: BillingCycle.Monthly);

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(2, summary.ActiveSubscriptions);
        Assert.Equal(300m, summary.MonthlySubscriptionTotal);
    }

    [Fact]
    public async Task MonthlySubscriptionTotal_treats_null_amount_and_cycle_as_zero_and_monthly()
    {
        // active recurring แต่ RecurringAmount = null และ BillingCycle = null
        await Db.AddSubscriptionAsync(amount: 0m, cycle: BillingCycle.Monthly, configure: i =>
        {
            i.RecurringAmount = null;
            i.BillingCycle = null;
        });
        // อีกตัวปกติ
        await Db.AddSubscriptionAsync(amount: 500m, cycle: BillingCycle.Monthly);

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(2, summary.ActiveSubscriptions);
        // null amount -> 0 ; null cycle -> Monthly ; รวม = 0 + 500
        Assert.Equal(500m, summary.MonthlySubscriptionTotal);
    }

    [Fact]
    public async Task MonthlySubscriptionTotal_quarterly_division_can_produce_repeating_decimal()
    {
        // 100 / 3 = 33.333... (decimal) — ยืนยันว่าไม่ถูกปัดเป็น int
        await Db.AddSubscriptionAsync(amount: 100m, cycle: BillingCycle.Quarterly);

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(100m / 3m, summary.MonthlySubscriptionTotal);
    }

    // ======================================================================
    // UpcomingRenewals
    // ======================================================================

    [Fact]
    public async Task UpcomingRenewals_includes_only_active_recurring_within_30_days_ordered_by_date()
    {
        // ภายใน 30 วัน
        var inTen = await Db.AddSubscriptionAsync(amount: 100m, nextBillingDate: Today.AddDays(10));
        var inThirty = await Db.AddSubscriptionAsync(amount: 200m, nextBillingDate: Today.AddDays(30));
        // เกิน 30 วัน — ไม่รวม
        await Db.AddSubscriptionAsync(amount: 300m, nextBillingDate: Today.AddDays(31));
        // Cancelled แม้ใกล้ครบ — ไม่รวม
        await Db.AddSubscriptionAsync(amount: 400m, status: SubscriptionStatus.Cancelled, nextBillingDate: Today.AddDays(5));

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(2, summary.UpcomingRenewals.Count);
        // เรียงตาม NextBillingDate น้อยไปมาก
        Assert.Equal(inTen.Id, summary.UpcomingRenewals[0].ItemId);
        Assert.Equal(inThirty.Id, summary.UpcomingRenewals[1].ItemId);
        Assert.Equal(10, summary.UpcomingRenewals[0].DaysUntil);
        Assert.Equal(30, summary.UpcomingRenewals[1].DaysUntil);
    }

    [Fact]
    public async Task UpcomingRenewals_includes_past_due_with_negative_days_until()
    {
        // NextBillingDate ในอดีต ก็ยังเข้าเงื่อนไข (<= today+30) เพราะไม่มีขอบล่าง
        await Db.AddSubscriptionAsync(amount: 100m, nextBillingDate: Today.AddDays(-5));

        var summary = await Service().GetSummaryAsync();

        // AUDIT[medium]: UpcomingRenewals ไม่มีขอบล่าง — subscription ที่เลยกำหนดบิลไปแล้ว
        // (NextBillingDate ในอดีต) ยังถูกจัดเป็น "ใกล้ครบกำหนด" และได้ DaysUntil ติดลบ
        var renewal = Assert.Single(summary.UpcomingRenewals);
        Assert.Equal(-5, renewal.DaysUntil);
    }

    [Fact]
    public async Task UpcomingRenewals_boundary_today_is_included_with_zero_days()
    {
        await Db.AddSubscriptionAsync(amount: 100m, nextBillingDate: Today);

        var summary = await Service().GetSummaryAsync();

        var renewal = Assert.Single(summary.UpcomingRenewals);
        Assert.Equal(0, renewal.DaysUntil);
    }

    [Fact]
    public async Task UpcomingRenewals_excludes_active_recurring_with_null_next_billing_date()
    {
        await Db.AddSubscriptionAsync(amount: 100m, configure: i => i.NextBillingDate = null);

        var summary = await Service().GetSummaryAsync();

        Assert.Empty(summary.UpcomingRenewals);
        // แต่ยังถูกนับเป็น active subscription
        Assert.Equal(1, summary.ActiveSubscriptions);
    }

    [Fact]
    public async Task UpcomingRenewals_carries_amount_cycle_and_falls_back_when_null()
    {
        await Db.AddSubscriptionAsync(cycle: BillingCycle.Yearly, nextBillingDate: Today.AddDays(7), configure: i =>
        {
            i.RecurringAmount = null;  // -> 0
            i.BillingCycle = null;     // -> Monthly
        });

        var summary = await Service().GetSummaryAsync();

        var renewal = Assert.Single(summary.UpcomingRenewals);
        Assert.Equal(0m, renewal.Amount);
        Assert.Equal(BillingCycle.Monthly, renewal.Cycle);
    }

    // ======================================================================
    // LowStock
    // ======================================================================

    [Fact]
    public async Task LowStock_includes_iot_items_at_or_below_min_quantity_ordered_ascending()
    {
        await Db.AddIotItemAsync(quantity: 5, minQuantity: 10);  // ต่ำ
        await Db.AddIotItemAsync(quantity: 10, minQuantity: 10); // เท่ากับ min -> นับเป็นต่ำ (<=)
        await Db.AddIotItemAsync(quantity: 11, minQuantity: 10); // เกิน min -> ไม่ต่ำ

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(2, summary.LowStockCount);
        Assert.Equal(2, summary.LowStockItems.Count);
        // เรียงตาม Quantity น้อยไปมาก
        Assert.Equal(5, summary.LowStockItems[0].Quantity);
        Assert.Equal(10, summary.LowStockItems[1].Quantity);
    }

    [Fact]
    public async Task LowStock_with_zero_min_quantity_flags_zero_and_negative_stock()
    {
        await Db.AddIotItemAsync(quantity: 0, minQuantity: 0);   // 0 <= 0 -> ต่ำ
        await Db.AddIotItemAsync(quantity: -3, minQuantity: 0);  // -3 <= 0 -> ต่ำ (สต็อกติดลบ)
        await Db.AddIotItemAsync(quantity: 1, minQuantity: 0);   // 1 > 0 -> ไม่ต่ำ

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(2, summary.LowStockCount);
        // ตัวติดลบมาก่อน (เรียง quantity น้อยไปมาก)
        Assert.Equal(-3, summary.LowStockItems[0].Quantity);
        Assert.Equal(0, summary.LowStockItems[1].Quantity);
    }

    [Fact]
    public async Task LowStock_does_not_consider_software_or_server_items()
    {
        // Software มี Quantity 0 และ MinQuantity 0 แต่ไม่ใช่ IoT จึงไม่ถูกพิจารณาเป็น low stock
        await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => { i.Quantity = 0; i.MinQuantity = 5; });
        await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 5, configure: i => { i.Quantity = 0; i.MinQuantity = 5; });

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(0, summary.LowStockCount);
        Assert.Empty(summary.LowStockItems);
    }

    // ======================================================================
    // Seat usage: SeatsNearFull, UsedSeatsTotal, TotalSeatsTotal
    // ======================================================================

    [Fact]
    public async Task SeatUsage_totals_sum_across_all_seat_items_using_active_licenses_only()
    {
        var assigner = await Db.AddUserAsync();
        var u1 = await Db.AddUserAsync();
        var u2 = await Db.AddUserAsync();

        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);
        var srv = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 4);

        // active license 2 ใบบน sw, 1 ใบบน srv
        await Db.AddLicenseAsync(sw.Id, u1.Id, assigner.Id);
        await Db.AddLicenseAsync(sw.Id, u2.Id, assigner.Id);
        await Db.AddLicenseAsync(srv.Id, u1.Id, assigner.Id);
        // license ที่ release แล้ว — ต้องไม่ถูกนับเป็น used
        await Db.AddLicenseAsync(sw.Id, u2.Id, assigner.Id, releasedAt: DateTime.UtcNow);

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(3, summary.UsedSeatsTotal);   // 2 + 1
        Assert.Equal(14, summary.TotalSeatsTotal);  // 10 + 4
    }

    [Fact]
    public async Task SeatsNearFull_flags_when_available_one_or_less()
    {
        var assigner = await Db.AddUserAsync();
        var users = new List<ApplicationUser>();
        for (int k = 0; k < 9; k++) users.Add(await Db.AddUserAsync());

        // Total 10, Used 9 -> Available 1 -> ใกล้เต็ม (Available <= 1)
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);
        foreach (var u in users) await Db.AddLicenseAsync(sw.Id, u.Id, assigner.Id);

        // Total 10, Used 5 -> Available 5, ratio 0.5 -> ไม่ใกล้เต็ม
        var srv = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 10);
        for (int k = 0; k < 5; k++) await Db.AddLicenseAsync(srv.Id, users[k].Id, assigner.Id);

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(1, summary.SeatsNearFullCount);
        var near = Assert.Single(summary.SeatsNearFull);
        Assert.Equal(sw.Id, near.ItemId);
        Assert.Equal(9, near.Used);
        Assert.Equal(10, near.Total);
        Assert.Equal(1, near.Available);
    }

    [Fact]
    public async Task SeatsNearFull_flags_when_used_ratio_at_least_ninety_percent()
    {
        var assigner = await Db.AddUserAsync();
        var users = new List<ApplicationUser>();
        for (int k = 0; k < 18; k++) users.Add(await Db.AddUserAsync());

        // Total 20, Used 18 -> Available 2 (>1) แต่ ratio = 0.9 -> ใกล้เต็มผ่านเงื่อนไข ratio
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 20);
        foreach (var u in users) await Db.AddLicenseAsync(sw.Id, u.Id, assigner.Id);

        var summary = await Service().GetSummaryAsync();

        var near = Assert.Single(summary.SeatsNearFull);
        Assert.Equal(sw.Id, near.ItemId);
        Assert.Equal(2, near.Available);
        Assert.True(near.UsedRatio >= 0.9);
    }

    [Fact]
    public async Task SeatsNearFull_flags_empty_single_seat_item_because_available_equals_one()
    {
        // Total 1, Used 0 -> Available 1 -> เข้าเงื่อนไข Available <= 1 แม้ยังว่างทั้งหมด
        await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 1);

        var summary = await Service().GetSummaryAsync();

        // AUDIT[low]: seat item ที่มีแค่ 1 ที่นั่งและยังไม่ถูกใช้เลย (Used=0, ratio=0)
        // ถูกจัดเป็น "ใกล้เต็ม" เพราะ Available<=1 — อาจทำให้ dashboard เตือนเกินจริง
        var near = Assert.Single(summary.SeatsNearFull);
        Assert.Equal(0, near.Used);
        Assert.Equal(1, near.Total);
        Assert.Equal(0.0, near.UsedRatio);
    }

    [Fact]
    public async Task SeatsNearFull_excludes_items_with_zero_total_seats()
    {
        // TotalSeats = 0 -> s.Total > 0 เป็น false -> ไม่ near-full แม้ Available=0
        await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 0);

        var summary = await Service().GetSummaryAsync();

        Assert.Empty(summary.SeatsNearFull);
        Assert.Equal(0, summary.SeatsNearFullCount);
        // TotalSeats=0 ยังถูกนับใน totals (มี TotalSeats != null)
        Assert.Equal(0, summary.TotalSeatsTotal);
        Assert.Equal(0, summary.UsedSeatsTotal);
    }

    [Fact]
    public async Task SeatUsage_ignores_items_with_null_total_seats()
    {
        // Software ที่ไม่ตั้ง TotalSeats (null) — ถูกตัดออกจาก seatItems ทั้งหมด
        await Db.AddItemAsync(i =>
        {
            i.Type = ItemType.Software;
            i.CostType = CostType.OneTime;
            i.TotalSeats = null;
        });

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(0, summary.TotalSeatsTotal);
        Assert.Equal(0, summary.UsedSeatsTotal);
        Assert.Empty(summary.SeatsNearFull);
    }

    [Fact]
    public async Task SeatsNearFull_ordered_by_used_ratio_descending()
    {
        var assigner = await Db.AddUserAsync();
        var users = new List<ApplicationUser>();
        for (int k = 0; k < 20; k++) users.Add(await Db.AddUserAsync());

        // A: Total 10 Used 10 -> ratio 1.0
        var a = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);
        for (int k = 0; k < 10; k++) await Db.AddLicenseAsync(a.Id, users[k].Id, assigner.Id);
        // B: Total 10 Used 9 -> ratio 0.9
        var b = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 10);
        for (int k = 0; k < 9; k++) await Db.AddLicenseAsync(b.Id, users[k].Id, assigner.Id);

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(2, summary.SeatsNearFull.Count);
        Assert.Equal(a.Id, summary.SeatsNearFull[0].ItemId); // ratio สูงสุดมาก่อน
        Assert.Equal(b.Id, summary.SeatsNearFull[1].ItemId);
    }

    // ======================================================================
    // MonthlySpendSeries (6 points split by type)
    // ======================================================================

    [Fact]
    public async Task MonthlySpendSeries_always_has_exactly_six_points_in_chronological_order()
    {
        var summary = await Service().GetSummaryAsync();

        Assert.Equal(6, summary.MonthlySpendSeries.Count);
        // จุดสุดท้ายคือเดือนปัจจุบัน, จุดแรกคือ 5 เดือนก่อน
        var seriesStart = MonthStart.AddMonths(-5);
        Assert.Equal(seriesStart.ToString("MM/yyyy"), summary.MonthlySpendSeries[0].Month);
        Assert.Equal(MonthStart.ToString("MM/yyyy"), summary.MonthlySpendSeries[5].Month);
    }

    [Fact]
    public async Task MonthlySpendSeries_splits_current_month_spend_by_item_type()
    {
        var user = await Db.AddUserAsync();
        var iot = await Db.AddIotItemAsync(quantity: 0, minQuantity: 0);
        var srv = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 5);
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        var other = await Db.AddItemAsync(i => { i.Type = ItemType.Other; i.CostType = CostType.OneTime; });

        await Db.AddPurchaseAsync(iot.Id, user.Id, p => { p.Date = Today; p.TotalCost = 10m; });
        await Db.AddPurchaseAsync(srv.Id, user.Id, p => { p.Date = Today; p.TotalCost = 20m; });
        await Db.AddPurchaseAsync(sw.Id, user.Id, p => { p.Date = Today; p.TotalCost = 30m; });
        await Db.AddPurchaseAsync(other.Id, user.Id, p => { p.Date = Today; p.TotalCost = 40m; });

        var summary = await Service().GetSummaryAsync();

        var current = summary.MonthlySpendSeries[5]; // เดือนปัจจุบัน
        Assert.Equal(10m, current.Iot);
        Assert.Equal(20m, current.Server);
        Assert.Equal(30m, current.Software);
        Assert.Equal(40m, current.Other);
        Assert.Equal(100m, current.Total);
    }

    [Fact]
    public async Task MonthlySpendSeries_attributes_spend_to_the_correct_month_bucket()
    {
        var user = await Db.AddUserAsync();
        var iot = await Db.AddIotItemAsync(quantity: 0, minQuantity: 0);

        // เดือนปัจจุบัน
        await Db.AddPurchaseAsync(iot.Id, user.Id, p => { p.Date = Today; p.TotalCost = 5m; });
        // 5 เดือนก่อน (จุดแรกของ series)
        var fiveMonthsAgoStart = MonthStart.AddMonths(-5);
        await Db.AddPurchaseAsync(iot.Id, user.Id, p => { p.Date = fiveMonthsAgoStart; p.TotalCost = 7m; });
        // 6 เดือนก่อน (ก่อน seriesStart) — ต้องไม่ปรากฏใน series ใด ๆ
        await Db.AddPurchaseAsync(iot.Id, user.Id, p => { p.Date = fiveMonthsAgoStart.AddMonths(-1); p.TotalCost = 999m; });

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(7m, summary.MonthlySpendSeries[0].Iot);  // 5 เดือนก่อน
        Assert.Equal(5m, summary.MonthlySpendSeries[5].Iot);  // เดือนนี้
        // เดือนกลาง ๆ ต้องเป็น 0 และยอดรวมทั้ง series = 12 (ไม่รวม 999 ที่อยู่นอกหน้าต่าง)
        Assert.Equal(12m, summary.MonthlySpendSeries.Sum(p => p.Total));
    }

    // ======================================================================
    // RecentActivity
    // ======================================================================

    [Fact]
    public async Task RecentActivity_merges_purchases_withdrawals_licenses_ordered_by_when_descending()
    {
        var user = await Db.AddUserAsync("สมชาย");
        var assignee = await Db.AddUserAsync("ผู้รับ");
        var item = await Db.AddIotItemAsync(quantity: 100, minQuantity: 0);
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);

        var now = DateTime.UtcNow;
        // license เก่าสุด
        await Db.AddLicenseAsync(sw.Id, assignee.Id, user.Id, configure: l => l.AssignedAt = now.AddHours(-3));
        // withdrawal กลาง
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.WithdrawnAt = now.AddHours(-2); w.Quantity = 4; });
        // purchase ใหม่สุด
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = now.AddHours(-1); p.Quantity = 2; p.TotalCost = 100m; });

        var summary = await Service().GetSummaryAsync();

        Assert.Equal(3, summary.RecentActivity.Count);
        Assert.Equal("ซื้อ/เติม", summary.RecentActivity[0].Kind);
        Assert.Equal("เบิก", summary.RecentActivity[1].Kind);
        Assert.Equal("License", summary.RecentActivity[2].Kind);
    }

    [Fact]
    public async Task RecentActivity_purchase_recurring_charge_uses_distinct_kind_and_color()
    {
        var user = await Db.AddUserAsync("ผู้บันทึก");
        var sw = await Db.AddSubscriptionAsync(amount: 500m, nextBillingDate: Today.AddDays(60)); // ไม่เข้า upcoming

        await Db.AddPurchaseAsync(sw.Id, user.Id, p => { p.Date = DateTime.UtcNow; p.IsRecurringCharge = true; p.Quantity = 0; p.TotalCost = 500m; });

        var summary = await Service().GetSummaryAsync();

        var entry = Assert.Single(summary.RecentActivity);
        Assert.Equal("ค่ารอบ", entry.Kind);
        Assert.Equal("info", entry.Color);
        Assert.Contains("บันทึกค่ารอบ", entry.Description);
    }

    [Fact]
    public async Task RecentActivity_caps_at_twelve_entries()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 1000, minQuantity: 0);

        var now = DateTime.UtcNow;
        // 8 purchases + 8 withdrawals = 16 รายการ (แต่ละแหล่ง Take 8) -> ผลรวมก่อนตัด 16
        for (int k = 0; k < 8; k++)
            await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = now.AddMinutes(-k); p.TotalCost = 10m; p.Quantity = 1; });
        for (int k = 0; k < 8; k++)
            await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.WithdrawnAt = now.AddMinutes(-20 - k); w.Quantity = 1; });

        var summary = await Service().GetSummaryAsync();

        // ตัดเหลือ 12 รายการล่าสุด
        Assert.Equal(12, summary.RecentActivity.Count);
        Assert.True(summary.RecentActivity.SequenceEqual(
            summary.RecentActivity.OrderByDescending(a => a.When)),
            "RecentActivity ต้องเรียงตาม When จากใหม่ไปเก่า");
    }

    [Fact]
    public async Task RecentActivity_each_source_limited_to_eight_before_merge()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(quantity: 1000, minQuantity: 0);

        var now = DateTime.UtcNow;
        // 10 purchases เท่านั้น -> แหล่งนี้ Take 8 -> RecentActivity ได้สูงสุด 8
        for (int k = 0; k < 10; k++)
            await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = now.AddMinutes(-k); p.TotalCost = 10m; p.Quantity = 1; });

        var summary = await Service().GetSummaryAsync();

        // AUDIT[low]: แต่ละแหล่งถูกจำกัด Take(8) ก่อน merge — แม้รวมแล้วยังไม่ถึง 12
        // ผู้ใช้ก็จะเห็น purchase ล่าสุดได้ไม่เกิน 8 รายการ (อาจพลาดกิจกรรมจริงที่ใหม่กว่า)
        Assert.Equal(8, summary.RecentActivity.Count);
    }

    [Fact]
    public async Task RecentActivity_uses_full_name_for_purchaser_and_assignee()
    {
        var purchaser = await Db.AddUserAsync("จัดซื้อ ก");
        var assignee = await Db.AddUserAsync("พนักงาน ข");
        var item = await Db.AddIotItemAsync(quantity: 100, minQuantity: 0);
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);

        await Db.AddPurchaseAsync(item.Id, purchaser.Id, p => { p.Date = DateTime.UtcNow; p.Quantity = 1; p.TotalCost = 50m; });
        await Db.AddLicenseAsync(sw.Id, assignee.Id, purchaser.Id, configure: l => l.AssignedAt = DateTime.UtcNow.AddHours(-1));

        var summary = await Service().GetSummaryAsync();

        var purchase = summary.RecentActivity.Single(a => a.Kind == "ซื้อ/เติม");
        Assert.Equal("จัดซื้อ ก", purchase.User);

        var license = summary.RecentActivity.Single(a => a.Kind == "License");
        // license entry มี User = null และ assignee อยู่ใน Description
        Assert.Null(license.User);
        Assert.Contains("พนักงาน ข", license.Description);
    }

    [Fact]
    public async Task RecentActivity_falls_back_to_email_when_full_name_blank()
    {
        var user = await Db.AddUserAsync(fullName: " ", email: "fallback@test.local");
        var item = await Db.AddIotItemAsync(quantity: 100, minQuantity: 0);

        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.Date = DateTime.UtcNow; p.Quantity = 1; p.TotalCost = 50m; });

        var summary = await Service().GetSummaryAsync();

        var entry = Assert.Single(summary.RecentActivity);
        // DisplayName: FullName ว่าง -> UserName (= email ที่ตั้งให้)
        Assert.Equal("fallback@test.local", entry.User);
    }
}
