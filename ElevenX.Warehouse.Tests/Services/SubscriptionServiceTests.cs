using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>
/// ครอบคลุม SubscriptionService ทุกฟังก์ชัน + branch:
/// GetSubscriptionsAsync, GetUpcomingRenewalsAsync, RecordRecurringChargeAsync,
/// CancelAsync, ReactivateAsync และ MonthlyTotal (pure)
/// </summary>
public class SubscriptionServiceTests(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    private SubscriptionService Service(CurrentUserAccessor? accessor = null)
        => new(Db.Factory, accessor ?? Db.Accessor(AppRoles.Admin));

    // helper: build an in-memory Item (no DB) for MonthlyTotal tests
    private static Item Sub(decimal? amount, BillingCycle? cycle,
        CostType costType = CostType.Recurring, SubscriptionStatus? status = SubscriptionStatus.Active)
        => new()
        {
            Name = "x",
            CostType = costType,
            Status = status,
            RecurringAmount = amount,
            BillingCycle = cycle,
        };

    // ============================================================
    //  GetSubscriptionsAsync
    // ============================================================

    [Fact]
    public async Task GetSubscriptions_returns_only_recurring_items_not_onetime()
    {
        await Db.AddSubscriptionAsync(); // Recurring
        await Db.AddIotItemAsync();      // OneTime -> excluded
        await Db.AddSeatItemAsync();     // OneTime -> excluded

        var subs = await Service().GetSubscriptionsAsync();

        Assert.Single(subs);
        Assert.All(subs, s => Assert.Equal(CostType.Recurring, s.CostType));
    }

    [Fact]
    public async Task GetSubscriptions_includes_category_navigation()
    {
        var cat = await Db.AddCategoryAsync("คลาวด์");
        await Db.AddSubscriptionAsync(categoryId: cat.Id);

        var subs = await Service().GetSubscriptionsAsync();

        Assert.Single(subs);
        Assert.NotNull(subs[0].Category);
        Assert.Equal("คลาวด์", subs[0].Category.Name);
    }

    [Fact]
    public async Task GetSubscriptions_with_status_filter_returns_only_that_status()
    {
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Active);
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled);
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Expired);

        var cancelled = await Service().GetSubscriptionsAsync(SubscriptionStatus.Cancelled);

        Assert.Single(cancelled);
        Assert.Equal(SubscriptionStatus.Cancelled, cancelled[0].Status);
    }

    [Fact]
    public async Task GetSubscriptions_null_status_returns_all_statuses()
    {
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Active);
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled);
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Expired);

        var all = await Service().GetSubscriptionsAsync(null);

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task GetSubscriptions_orders_by_status_then_nextBillingDate()
    {
        // Status enum: Active=0, Cancelled=1, Expired=2  -> Active group first.
        var cancelled = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled,
            nextBillingDate: new DateTime(2026, 1, 1));
        var activeLater = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Active,
            nextBillingDate: new DateTime(2026, 12, 1));
        var activeEarlier = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Active,
            nextBillingDate: new DateTime(2026, 3, 1));

        var subs = await Service().GetSubscriptionsAsync();

        Assert.Equal(3, subs.Count);
        // Active group (sorted by NextBillingDate asc) comes before Cancelled group
        Assert.Equal(activeEarlier.Id, subs[0].Id);
        Assert.Equal(activeLater.Id, subs[1].Id);
        Assert.Equal(cancelled.Id, subs[2].Id);
    }

    [Fact]
    public async Task GetSubscriptions_empty_db_returns_empty_list()
    {
        var subs = await Service().GetSubscriptionsAsync();
        Assert.Empty(subs);
    }

    [Fact]
    public async Task GetSubscriptions_requires_no_permission_works_for_anonymous()
    {
        // GetSubscriptionsAsync has no permission check — anonymous can read.
        await Db.AddSubscriptionAsync();
        var svc = Service(Db.Anonymous());

        var subs = await svc.GetSubscriptionsAsync();

        Assert.Single(subs);
    }

    // ============================================================
    //  GetUpcomingRenewalsAsync
    // ============================================================

    [Fact]
    public async Task GetUpcomingRenewals_includes_active_recurring_within_default_30_days()
    {
        var today = DateTime.UtcNow.Date;
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Active,
            nextBillingDate: today.AddDays(10));

        var renewals = await Service().GetUpcomingRenewalsAsync();

        Assert.Single(renewals);
        Assert.Equal(10, renewals[0].DaysUntil);
    }

    [Fact]
    public async Task GetUpcomingRenewals_excludes_nonactive_status()
    {
        var today = DateTime.UtcNow.Date;
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled, nextBillingDate: today.AddDays(5));
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Expired, nextBillingDate: today.AddDays(5));

        var renewals = await Service().GetUpcomingRenewalsAsync();

        Assert.Empty(renewals);
    }

    [Fact]
    public async Task GetUpcomingRenewals_excludes_onetime_items()
    {
        var today = DateTime.UtcNow.Date;
        // OneTime item that happens to have a NextBillingDate set should not be picked
        await Db.AddItemAsync(i =>
        {
            i.CostType = CostType.OneTime;
            i.Status = SubscriptionStatus.Active;
            i.NextBillingDate = today.AddDays(3);
        });

        var renewals = await Service().GetUpcomingRenewalsAsync();

        Assert.Empty(renewals);
    }

    [Fact]
    public async Task GetUpcomingRenewals_excludes_null_nextBillingDate()
    {
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Active, configure: i => i.NextBillingDate = null);

        var renewals = await Service().GetUpcomingRenewalsAsync();

        Assert.Empty(renewals);
    }

    [Fact]
    public async Task GetUpcomingRenewals_excludes_beyond_window()
    {
        var today = DateTime.UtcNow.Date;
        await Db.AddSubscriptionAsync(nextBillingDate: today.AddDays(31)); // beyond default 30

        var renewals = await Service().GetUpcomingRenewalsAsync();

        Assert.Empty(renewals);
    }

    [Fact]
    public async Task GetUpcomingRenewals_boundary_exactly_at_window_is_inclusive()
    {
        var today = DateTime.UtcNow.Date;
        // limit = today + 30; NextBillingDate == limit must be INCLUDED (<=)
        await Db.AddSubscriptionAsync(nextBillingDate: today.AddDays(30));

        var renewals = await Service().GetUpcomingRenewalsAsync();

        Assert.Single(renewals);
        Assert.Equal(30, renewals[0].DaysUntil);
    }

    [Fact]
    public async Task GetUpcomingRenewals_custom_withinDays_window()
    {
        var today = DateTime.UtcNow.Date;
        await Db.AddSubscriptionAsync(nextBillingDate: today.AddDays(5));   // in 7-day window
        await Db.AddSubscriptionAsync(nextBillingDate: today.AddDays(10));  // outside 7-day window

        var renewals = await Service().GetUpcomingRenewalsAsync(withinDays: 7);

        Assert.Single(renewals);
        Assert.Equal(5, renewals[0].DaysUntil);
    }

    [Fact]
    public async Task GetUpcomingRenewals_due_today_has_zero_daysUntil()
    {
        var today = DateTime.UtcNow.Date;
        await Db.AddSubscriptionAsync(nextBillingDate: today);

        var renewals = await Service().GetUpcomingRenewalsAsync();

        Assert.Single(renewals);
        Assert.Equal(0, renewals[0].DaysUntil);
    }

    [Fact]
    public async Task GetUpcomingRenewals_past_due_active_is_included_with_negative_daysUntil()
    {
        var today = DateTime.UtcNow.Date;
        // Active subscription whose billing date is already overdue: there is NO lower bound
        // on the filter, so an overdue Active sub is reported as an "upcoming" renewal with a
        // negative DaysUntil.
        await Db.AddSubscriptionAsync(status: SubscriptionStatus.Active, nextBillingDate: today.AddDays(-3));

        var renewals = await Service().GetUpcomingRenewalsAsync();

        Assert.Single(renewals);
        // AUDIT[medium]: GetUpcomingRenewals has no lower date bound, so overdue Active
        // subscriptions surface as "upcoming renewals" with a negative DaysUntil.
        Assert.Equal(-3, renewals[0].DaysUntil);
    }

    [Fact]
    public async Task GetUpcomingRenewals_ordered_by_nextBillingDate_ascending()
    {
        var today = DateTime.UtcNow.Date;
        var later = await Db.AddSubscriptionAsync(nextBillingDate: today.AddDays(20));
        var earlier = await Db.AddSubscriptionAsync(nextBillingDate: today.AddDays(2));
        var middle = await Db.AddSubscriptionAsync(nextBillingDate: today.AddDays(10));

        var renewals = await Service().GetUpcomingRenewalsAsync();

        Assert.Equal(3, renewals.Count);
        Assert.Equal(earlier.Id, renewals[0].ItemId);
        Assert.Equal(middle.Id, renewals[1].ItemId);
        Assert.Equal(later.Id, renewals[2].ItemId);
    }

    [Fact]
    public async Task GetUpcomingRenewals_maps_fields_and_falls_back_on_null_amount_and_cycle()
    {
        var today = DateTime.UtcNow.Date;
        var item = await Db.AddSubscriptionAsync(
            type: ItemType.Server,
            nextBillingDate: today.AddDays(4),
            configure: i =>
            {
                i.Name = "เซิร์ฟเวอร์คลาวด์";
                i.RecurringAmount = null;   // -> falls back to 0
                i.BillingCycle = null;      // -> falls back to Monthly
            });

        var renewals = await Service().GetUpcomingRenewalsAsync();

        var r = Assert.Single(renewals);
        Assert.Equal(item.Id, r.ItemId);
        Assert.Equal("เซิร์ฟเวอร์คลาวด์", r.Name);
        Assert.Equal(ItemType.Server, r.Type);
        Assert.Equal(today.AddDays(4), r.NextBillingDate);
        Assert.Equal(0m, r.Amount);
        Assert.Equal(BillingCycle.Monthly, r.Cycle);
    }

    // ============================================================
    //  RecordRecurringChargeAsync
    // ============================================================

    [Fact]
    public async Task RecordRecurringCharge_forbidden_for_anonymous()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();
        var svc = Service(Db.Anonymous());

        var result = await svc.RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task RecordRecurringCharge_forbidden_for_staff_role()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();
        var svc = Service(Db.Accessor(AppRoles.Staff));

        var result = await svc.RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task RecordRecurringCharge_allowed_for_purchaser_role()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();
        var svc = Service(Db.Accessor(AppRoles.Purchaser));

        var result = await svc.RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task RecordRecurringCharge_item_not_found()
    {
        var user = await Db.AddUserAsync();

        var result = await Service().RecordRecurringChargeAsync(999999, user.Id);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการสินค้า", result.Error);
    }

    [Fact]
    public async Task RecordRecurringCharge_rejects_non_recurring_item()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync(); // OneTime

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.False(result.Success);
        Assert.Equal("รายการนี้ไม่ใช่ subscription", result.Error);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Expired)]
    public async Task RecordRecurringCharge_rejects_non_active_status(SubscriptionStatus status)
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync(status: status);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.False(result.Success);
        Assert.Equal("subscription นี้ไม่อยู่ในสถานะ Active", result.Error);
    }

    [Fact]
    public async Task RecordRecurringCharge_rejects_null_status_as_non_active()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync(configure: i => i.Status = null);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.False(result.Success);
        Assert.Equal("subscription นี้ไม่อยู่ในสถานะ Active", result.Error);
    }

    [Fact]
    public async Task RecordRecurringCharge_rejects_missing_recurring_amount()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync(configure: i => i.RecurringAmount = null);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.False(result.Success);
        Assert.Equal("ยังไม่ได้กำหนดค่าใช้จ่าย/รอบบิลของ subscription", result.Error);
    }

    [Fact]
    public async Task RecordRecurringCharge_rejects_missing_billing_cycle()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync(configure: i => i.BillingCycle = null);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.False(result.Success);
        Assert.Equal("ยังไม่ได้กำหนดค่าใช้จ่าย/รอบบิลของ subscription", result.Error);
    }

    [Fact]
    public async Task RecordRecurringCharge_success_creates_purchase_with_expected_fields()
    {
        var user = await Db.AddUserAsync();
        var start = new DateTime(2026, 6, 1);
        var item = await Db.AddSubscriptionAsync(amount: 1500m, cycle: BillingCycle.Monthly,
            nextBillingDate: start);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.True(result.Success, result.Error);
        var p = result.Value!;
        Assert.True(p.IsRecurringCharge);
        Assert.Equal(0, p.Quantity);
        Assert.Equal(1500m, p.UnitPrice);
        Assert.Equal(1500m, p.TotalCost);
        Assert.Equal(item.Id, p.ItemId);
        Assert.Equal(user.Id, p.PurchasedById);
        Assert.Null(p.SupplierId);
        Assert.Equal(start, p.PeriodStart);
        Assert.Equal(start.AddMonths(1), p.PeriodEnd); // BillingMath.Advance(start, Monthly)

        // persisted
        Assert.Equal(1, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task RecordRecurringCharge_advances_item_nextBillingDate_to_periodEnd()
    {
        var user = await Db.AddUserAsync();
        var start = new DateTime(2026, 6, 1);
        var item = await Db.AddSubscriptionAsync(cycle: BillingCycle.Quarterly, nextBillingDate: start);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);
        Assert.True(result.Success, result.Error);

        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(start.AddMonths(3), fresh!.NextBillingDate);
        Assert.Equal(result.Value!.PeriodEnd, fresh.NextBillingDate);
    }

    [Theory]
    [InlineData(BillingCycle.Monthly, 1)]
    [InlineData(BillingCycle.Quarterly, 3)]
    [InlineData(BillingCycle.Yearly, 12)]
    public async Task RecordRecurringCharge_periodEnd_matches_cycle(BillingCycle cycle, int months)
    {
        var user = await Db.AddUserAsync();
        var start = new DateTime(2026, 1, 15);
        var item = await Db.AddSubscriptionAsync(cycle: cycle, nextBillingDate: start);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.True(result.Success, result.Error);
        var expectedEnd = cycle == BillingCycle.Yearly ? start.AddYears(1) : start.AddMonths(months);
        Assert.Equal(expectedEnd, result.Value!.PeriodEnd);
    }

    [Fact]
    public async Task RecordRecurringCharge_updates_UpdatedAt()
    {
        var user = await Db.AddUserAsync();
        var oldStamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var item = await Db.AddSubscriptionAsync(configure: i => i.UpdatedAt = oldStamp);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);
        Assert.True(result.Success, result.Error);

        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.True(fresh!.UpdatedAt > oldStamp);
    }

    [Fact]
    public async Task RecordRecurringCharge_default_note_uses_cycle_label()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync(cycle: BillingCycle.Yearly);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id, note: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal($"ค่ารอบ {BillingMath.CycleLabel(BillingCycle.Yearly)}", result.Value!.Note);
    }

    [Fact]
    public async Task RecordRecurringCharge_custom_note_is_preserved()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id, note: "ชำระผ่านบัตรเครดิต");

        Assert.True(result.Success, result.Error);
        Assert.Equal("ชำระผ่านบัตรเครดิต", result.Value!.Note);
    }

    [Fact]
    public async Task RecordRecurringCharge_empty_string_note_is_not_replaced_by_default()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();

        // AUDIT[low]: note default uses `note ?? ...` so an empty string ("") is treated as a
        // provided value and persisted verbatim instead of falling back to the cycle label.
        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id, note: "");

        Assert.True(result.Success, result.Error);
        Assert.Equal("", result.Value!.Note);
    }

    [Fact]
    public async Task RecordRecurringCharge_default_chargeDate_is_now_when_omitted()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync();
        var before = DateTime.UtcNow.AddSeconds(-5);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.True(result.Success, result.Error);
        var after = DateTime.UtcNow.AddSeconds(5);
        Assert.InRange(result.Value!.Date, before, after);
    }

    [Fact]
    public async Task RecordRecurringCharge_custom_chargeDate_is_used_for_date_field()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync(nextBillingDate: new DateTime(2026, 6, 1));
        var chargeDate = new DateTime(2026, 5, 20, 9, 30, 0, DateTimeKind.Utc);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id, chargeDate: chargeDate);

        Assert.True(result.Success, result.Error);
        Assert.Equal(chargeDate, result.Value!.Date);
        // periodStart still derives from NextBillingDate, NOT from chargeDate
        Assert.Equal(new DateTime(2026, 6, 1), result.Value!.PeriodStart);
    }

    [Fact]
    public async Task RecordRecurringCharge_periodStart_falls_back_to_startDate_when_nextBilling_null()
    {
        var user = await Db.AddUserAsync();
        var startDate = new DateTime(2026, 2, 10);
        var item = await Db.AddSubscriptionAsync(configure: i =>
        {
            i.NextBillingDate = null;
            i.StartDate = startDate;
        });

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.True(result.Success, result.Error);
        Assert.Equal(startDate, result.Value!.PeriodStart);
        Assert.Equal(startDate.AddMonths(1), result.Value!.PeriodEnd);
    }

    [Fact]
    public async Task RecordRecurringCharge_periodStart_falls_back_to_chargeDate_when_nextBilling_and_startDate_null()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync(configure: i =>
        {
            i.NextBillingDate = null;
            i.StartDate = null;
        });
        var chargeDate = new DateTime(2026, 3, 5, 14, 0, 0, DateTimeKind.Utc);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id, chargeDate: chargeDate);

        Assert.True(result.Success, result.Error);
        // periodStart = chargeDate.Date (time stripped)
        Assert.Equal(chargeDate.Date, result.Value!.PeriodStart);
    }

    [Fact]
    public async Task RecordRecurringCharge_periodStart_strips_time_component()
    {
        var user = await Db.AddUserAsync();
        var withTime = new DateTime(2026, 6, 1, 13, 45, 0, DateTimeKind.Utc);
        var item = await Db.AddSubscriptionAsync(nextBillingDate: withTime);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.True(result.Success, result.Error);
        Assert.Equal(withTime.Date, result.Value!.PeriodStart);
    }

    [Fact]
    public async Task RecordRecurringCharge_sets_status_expired_when_periodEnd_exceeds_endDate()
    {
        var user = await Db.AddUserAsync();
        var start = new DateTime(2026, 6, 1);
        var item = await Db.AddSubscriptionAsync(cycle: BillingCycle.Monthly, nextBillingDate: start,
            configure: i => i.EndDate = new DateTime(2026, 6, 15)); // periodEnd (Jul 1) > EndDate (Jun 15)

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(SubscriptionStatus.Expired, fresh!.Status);
        // AUDIT[low]: even when the charge pushes the subscription past EndDate and flips it to
        // Expired, the Purchase for that final over-the-end period is still created and
        // NextBillingDate is still advanced past EndDate.
        Assert.Equal(start.AddMonths(1), fresh.NextBillingDate);
        Assert.Equal(1, await Db.CountAsync(c => c.Purchases));
    }

    [Fact]
    public async Task RecordRecurringCharge_stays_active_when_periodEnd_equals_endDate()
    {
        var user = await Db.AddUserAsync();
        var start = new DateTime(2026, 6, 1);
        var item = await Db.AddSubscriptionAsync(cycle: BillingCycle.Monthly, nextBillingDate: start,
            configure: i => i.EndDate = new DateTime(2026, 7, 1)); // periodEnd == EndDate -> NOT > so stays Active

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(SubscriptionStatus.Active, fresh!.Status);
    }

    [Fact]
    public async Task RecordRecurringCharge_stays_active_when_no_endDate()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddSubscriptionAsync(nextBillingDate: new DateTime(2026, 6, 1),
            configure: i => i.EndDate = null);

        var result = await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(SubscriptionStatus.Active, fresh!.Status);
    }

    // ============================================================
    //  CancelAsync
    // ============================================================

    [Fact]
    public async Task Cancel_forbidden_for_anonymous()
    {
        var item = await Db.AddSubscriptionAsync();
        var svc = Service(Db.Anonymous());

        var result = await svc.CancelAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);

        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(SubscriptionStatus.Active, fresh!.Status); // unchanged
    }

    [Fact]
    public async Task Cancel_forbidden_for_staff()
    {
        var item = await Db.AddSubscriptionAsync();
        var svc = Service(Db.Accessor(AppRoles.Staff));

        var result = await svc.CancelAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task Cancel_item_not_found()
    {
        var result = await Service().CancelAsync(123456);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการสินค้า", result.Error);
    }

    [Fact]
    public async Task Cancel_rejects_non_recurring_item()
    {
        var item = await Db.AddIotItemAsync();

        var result = await Service().CancelAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("รายการนี้ไม่ใช่ subscription", result.Error);
    }

    [Fact]
    public async Task Cancel_sets_status_cancelled_and_updates_timestamp()
    {
        var oldStamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Active,
            configure: i => i.UpdatedAt = oldStamp);

        var result = await Service().CancelAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(SubscriptionStatus.Cancelled, fresh!.Status);
        Assert.True(fresh.UpdatedAt > oldStamp);
    }

    [Fact]
    public async Task Cancel_is_idempotent_on_already_cancelled()
    {
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled);

        var result = await Service().CancelAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(SubscriptionStatus.Cancelled, fresh!.Status);
    }

    [Fact]
    public async Task Cancel_does_not_change_nextBillingDate()
    {
        var billing = new DateTime(2026, 9, 1);
        var item = await Db.AddSubscriptionAsync(nextBillingDate: billing);

        var result = await Service().CancelAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        // AUDIT[low]: Cancel leaves NextBillingDate populated; a cancelled sub still carries a
        // future billing date which could confuse downstream reads if they ignore Status.
        Assert.Equal(billing, fresh!.NextBillingDate);
    }

    // ============================================================
    //  ReactivateAsync
    // ============================================================

    [Fact]
    public async Task Reactivate_forbidden_for_anonymous()
    {
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled);
        var svc = Service(Db.Anonymous());

        var result = await svc.ReactivateAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task Reactivate_forbidden_for_staff()
    {
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled);
        var svc = Service(Db.Accessor(AppRoles.Staff));

        var result = await svc.ReactivateAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("คุณไม่มีสิทธิ์ดำเนินการนี้", result.Error);
    }

    [Fact]
    public async Task Reactivate_item_not_found()
    {
        var result = await Service().ReactivateAsync(424242);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการสินค้า", result.Error);
    }

    [Fact]
    public async Task Reactivate_rejects_non_recurring_item()
    {
        var item = await Db.AddIotItemAsync();

        var result = await Service().ReactivateAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("รายการนี้ไม่ใช่ subscription", result.Error);
    }

    [Fact]
    public async Task Reactivate_sets_status_active()
    {
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled,
            nextBillingDate: DateTime.UtcNow.Date.AddDays(30));

        var result = await Service().ReactivateAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(SubscriptionStatus.Active, fresh!.Status);
    }

    [Fact]
    public async Task Reactivate_keeps_future_nextBillingDate_unchanged()
    {
        var future = DateTime.UtcNow.Date.AddDays(45);
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled, nextBillingDate: future);

        var result = await Service().ReactivateAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(future, fresh!.NextBillingDate);
    }

    [Fact]
    public async Task Reactivate_resets_past_nextBillingDate_using_cycle()
    {
        var today = DateTime.UtcNow.Date;
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled,
            cycle: BillingCycle.Monthly, nextBillingDate: today.AddDays(-10)); // in the past

        var result = await Service().ReactivateAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(BillingMath.Advance(today, BillingCycle.Monthly), fresh!.NextBillingDate);
    }

    [Fact]
    public async Task Reactivate_resets_null_nextBillingDate_using_cycle()
    {
        var today = DateTime.UtcNow.Date;
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled,
            cycle: BillingCycle.Quarterly, configure: i => i.NextBillingDate = null);

        var result = await Service().ReactivateAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(BillingMath.Advance(today, BillingCycle.Quarterly), fresh!.NextBillingDate);
    }

    [Fact]
    public async Task Reactivate_null_nextBillingDate_and_null_cycle_uses_today()
    {
        var today = DateTime.UtcNow.Date;
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled,
            configure: i =>
            {
                i.NextBillingDate = null;
                i.BillingCycle = null;
            });

        var result = await Service().ReactivateAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(today, fresh!.NextBillingDate);
    }

    [Fact]
    public async Task Reactivate_past_nextBillingDate_and_null_cycle_uses_today()
    {
        var today = DateTime.UtcNow.Date;
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled,
            nextBillingDate: today.AddDays(-5), configure: i => i.BillingCycle = null);

        var result = await Service().ReactivateAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.Equal(today, fresh!.NextBillingDate);
    }

    [Fact]
    public async Task Reactivate_updates_timestamp()
    {
        var oldStamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Cancelled,
            nextBillingDate: DateTime.UtcNow.Date.AddDays(30),
            configure: i => i.UpdatedAt = oldStamp);

        var result = await Service().ReactivateAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        Assert.True(fresh!.UpdatedAt > oldStamp);
    }

    [Fact]
    public async Task Reactivate_expired_past_endDate_does_not_re_expire_or_validate_endDate()
    {
        var today = DateTime.UtcNow.Date;
        var item = await Db.AddSubscriptionAsync(status: SubscriptionStatus.Expired,
            cycle: BillingCycle.Monthly, nextBillingDate: today.AddDays(-1),
            configure: i => i.EndDate = today.AddDays(-30)); // already past end date

        var result = await Service().ReactivateAsync(item.Id);

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var fresh = await ctx.Items.FindAsync(item.Id);
        // AUDIT[medium]: Reactivate flips status to Active and pushes NextBillingDate forward
        // without checking EndDate, so an expired subscription whose EndDate has already passed
        // can be silently revived into a billable Active state.
        Assert.Equal(SubscriptionStatus.Active, fresh!.Status);
        Assert.Equal(BillingMath.Advance(today, BillingCycle.Monthly), fresh.NextBillingDate);
    }

    // ============================================================
    //  Round-trip: record then it shows up in upcoming / subscriptions
    // ============================================================

    [Fact]
    public async Task RecordRecurringCharge_then_GetSubscriptions_reflects_advanced_date()
    {
        var user = await Db.AddUserAsync();
        var start = new DateTime(2026, 6, 1);
        var item = await Db.AddSubscriptionAsync(cycle: BillingCycle.Monthly, nextBillingDate: start);

        await Service().RecordRecurringChargeAsync(item.Id, user.Id);

        var subs = await Service().GetSubscriptionsAsync(SubscriptionStatus.Active);
        Assert.Single(subs);
        Assert.Equal(start.AddMonths(1), subs[0].NextBillingDate);
    }

    // ============================================================
    //  MonthlyTotal (pure, in-memory)
    // ============================================================

    [Fact]
    public void MonthlyTotal_empty_is_zero()
    {
        Assert.Equal(0m, Service().MonthlyTotal([]));
    }

    [Fact]
    public void MonthlyTotal_sums_active_recurring_monthly_amounts()
    {
        var subs = new[]
        {
            Sub(1000m, BillingCycle.Monthly),
            Sub(500m, BillingCycle.Monthly),
        };

        Assert.Equal(1500m, Service().MonthlyTotal(subs));
    }

    [Fact]
    public void MonthlyTotal_converts_quarterly_and_yearly_to_monthly_equivalent()
    {
        var subs = new[]
        {
            Sub(300m, BillingCycle.Quarterly), // -> 100
            Sub(1200m, BillingCycle.Yearly),   // -> 100
            Sub(50m, BillingCycle.Monthly),    // -> 50
        };

        Assert.Equal(250m, Service().MonthlyTotal(subs));
    }

    [Fact]
    public void MonthlyTotal_excludes_cancelled_and_expired()
    {
        var subs = new[]
        {
            Sub(1000m, BillingCycle.Monthly, status: SubscriptionStatus.Active),
            Sub(2000m, BillingCycle.Monthly, status: SubscriptionStatus.Cancelled),
            Sub(3000m, BillingCycle.Monthly, status: SubscriptionStatus.Expired),
        };

        Assert.Equal(1000m, Service().MonthlyTotal(subs));
    }

    [Fact]
    public void MonthlyTotal_excludes_onetime_items()
    {
        var subs = new[]
        {
            Sub(1000m, BillingCycle.Monthly, costType: CostType.Recurring),
            Sub(9999m, BillingCycle.Monthly, costType: CostType.OneTime, status: SubscriptionStatus.Active),
        };

        Assert.Equal(1000m, Service().MonthlyTotal(subs));
    }

    [Fact]
    public void MonthlyTotal_null_status_is_excluded()
    {
        // Status must equal Active; null != Active so it is excluded.
        var subs = new[] { Sub(1000m, BillingCycle.Monthly, status: null) };

        Assert.Equal(0m, Service().MonthlyTotal(subs));
    }

    [Fact]
    public void MonthlyTotal_null_amount_treated_as_zero()
    {
        var subs = new[] { Sub(null, BillingCycle.Monthly) };

        Assert.Equal(0m, Service().MonthlyTotal(subs));
    }

    [Fact]
    public void MonthlyTotal_null_cycle_treated_as_monthly()
    {
        var subs = new[] { Sub(750m, null) };

        Assert.Equal(750m, Service().MonthlyTotal(subs));
    }

    [Fact]
    public void MonthlyTotal_quarterly_division_keeps_decimal_precision()
    {
        // 100 / 3 = 33.333... — verify no integer truncation (decimal math).
        var subs = new[] { Sub(100m, BillingCycle.Quarterly) };

        var result = Service().MonthlyTotal(subs);
        Assert.Equal(100m / 3m, result);
        Assert.True(result > 33m && result < 34m);
    }
}
