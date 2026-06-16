using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

// =====================================================================================
// Pure unit tests (NO database). Each class targets one static helper / computed group.
// =====================================================================================

public class BillingMathTests
{
    // ---------------------------------------------------------------- Advance

    [Fact]
    public void Advance_Monthly_adds_one_month()
    {
        var start = new DateTime(2026, 1, 15);
        Assert.Equal(new DateTime(2026, 2, 15), BillingMath.Advance(start, BillingCycle.Monthly));
    }

    [Fact]
    public void Advance_Quarterly_adds_three_months()
    {
        var start = new DateTime(2026, 1, 15);
        Assert.Equal(new DateTime(2026, 4, 15), BillingMath.Advance(start, BillingCycle.Quarterly));
    }

    [Fact]
    public void Advance_Yearly_adds_one_year()
    {
        var start = new DateTime(2026, 1, 15);
        Assert.Equal(new DateTime(2027, 1, 15), BillingMath.Advance(start, BillingCycle.Yearly));
    }

    [Fact]
    public void Advance_unknown_cycle_falls_back_to_one_month()
    {
        var start = new DateTime(2026, 1, 15);
        // (BillingCycle)99 is not a defined value -> default branch (+1 month)
        Assert.Equal(new DateTime(2026, 2, 15), BillingMath.Advance(start, (BillingCycle)99));
    }

    [Fact]
    public void Advance_Monthly_from_Jan31_clamps_to_end_of_February()
    {
        // 2026 is NOT a leap year -> Feb has 28 days; AddMonths clamps to Feb 28.
        var start = new DateTime(2026, 1, 31);
        Assert.Equal(new DateTime(2026, 2, 28), BillingMath.Advance(start, BillingCycle.Monthly));
    }

    [Fact]
    public void Advance_Monthly_from_Jan31_in_leap_year_clamps_to_Feb29()
    {
        // 2024 is a leap year -> Feb has 29 days.
        var start = new DateTime(2024, 1, 31);
        Assert.Equal(new DateTime(2024, 2, 29), BillingMath.Advance(start, BillingCycle.Monthly));
    }

    [Fact]
    public void Advance_Quarterly_from_Nov30_clamps_to_end_of_February()
    {
        // Nov 30 2025 + 3 months -> Feb 30 invalid -> clamps to Feb 28 2026.
        var start = new DateTime(2025, 11, 30);
        Assert.Equal(new DateTime(2026, 2, 28), BillingMath.Advance(start, BillingCycle.Quarterly));
    }

    [Fact]
    public void Advance_Yearly_from_Feb29_clamps_to_Feb28_in_non_leap_year()
    {
        // Leap-day Feb 29 2024 + 1 year -> Feb 29 2025 invalid -> clamps to Feb 28 2025.
        var start = new DateTime(2024, 2, 29);
        Assert.Equal(new DateTime(2025, 2, 28), BillingMath.Advance(start, BillingCycle.Yearly));
    }

    [Fact]
    public void Advance_preserves_time_of_day()
    {
        var start = new DateTime(2026, 1, 15, 13, 45, 30);
        Assert.Equal(new DateTime(2026, 2, 15, 13, 45, 30), BillingMath.Advance(start, BillingCycle.Monthly));
    }

    // ---------------------------------------------------------------- MonthlyEquivalent

    [Fact]
    public void MonthlyEquivalent_Monthly_returns_amount_unchanged()
    {
        Assert.Equal(100m, BillingMath.MonthlyEquivalent(100m, BillingCycle.Monthly));
    }

    [Fact]
    public void MonthlyEquivalent_Quarterly_divides_by_three_with_full_decimal_precision()
    {
        // 100 / 3 in decimal expands to the maximum scale (the helper never rounds).
        // AUDIT[low]: per-month equivalents of indivisible amounts produce long-tail
        //   repeating decimals (33.3333...). Summing many of these can drift from the
        //   true annual total unless callers round; the helper itself never rounds.
        var result = BillingMath.MonthlyEquivalent(100m, BillingCycle.Quarterly);
        Assert.Equal(100m / 3m, result);
        // not rounded: the integer part plus a long repeating tail of 3s
        var s = result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith("33.33333333333", s);
        Assert.True(s.Length > 20, $"expected long-tail decimal, got '{s}'");
    }

    [Fact]
    public void MonthlyEquivalent_Quarterly_divides_evenly_when_divisible()
    {
        Assert.Equal(100m, BillingMath.MonthlyEquivalent(300m, BillingCycle.Quarterly));
    }

    [Fact]
    public void MonthlyEquivalent_Yearly_divides_by_twelve()
    {
        Assert.Equal(100m, BillingMath.MonthlyEquivalent(1200m, BillingCycle.Yearly));
    }

    [Fact]
    public void MonthlyEquivalent_Yearly_indivisible_keeps_full_precision()
    {
        // 100 / 12 = 8.3333...
        var result = BillingMath.MonthlyEquivalent(100m, BillingCycle.Yearly);
        Assert.Equal(100m / 12m, result);
        var s = result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith("8.33333333333", s);
        Assert.True(s.Length > 20, $"expected long-tail decimal, got '{s}'");
    }

    [Fact]
    public void MonthlyEquivalent_unknown_cycle_returns_amount_unchanged()
    {
        Assert.Equal(100m, BillingMath.MonthlyEquivalent(100m, (BillingCycle)99));
    }

    [Fact]
    public void MonthlyEquivalent_zero_amount_returns_zero_for_all_cycles()
    {
        Assert.Equal(0m, BillingMath.MonthlyEquivalent(0m, BillingCycle.Monthly));
        Assert.Equal(0m, BillingMath.MonthlyEquivalent(0m, BillingCycle.Quarterly));
        Assert.Equal(0m, BillingMath.MonthlyEquivalent(0m, BillingCycle.Yearly));
    }

    [Fact]
    public void MonthlyEquivalent_negative_amount_divides_correctly()
    {
        // AUDIT[low]: no guard against negative RecurringAmount; negative values flow through.
        Assert.Equal(-100m, BillingMath.MonthlyEquivalent(-300m, BillingCycle.Quarterly));
    }

    // ---------------------------------------------------------------- MonthsPerCycle

    [Theory]
    [InlineData(BillingCycle.Monthly, 1)]
    [InlineData(BillingCycle.Quarterly, 3)]
    [InlineData(BillingCycle.Yearly, 12)]
    public void MonthsPerCycle_returns_expected_months(BillingCycle cycle, int expected)
    {
        Assert.Equal(expected, BillingMath.MonthsPerCycle(cycle));
    }

    [Fact]
    public void MonthsPerCycle_unknown_cycle_falls_back_to_one()
    {
        Assert.Equal(1, BillingMath.MonthsPerCycle((BillingCycle)99));
    }

    // ---------------------------------------------------------------- CycleLabel

    [Theory]
    [InlineData(BillingCycle.Monthly, "รายเดือน")]
    [InlineData(BillingCycle.Quarterly, "ราย 3 เดือน")]
    [InlineData(BillingCycle.Yearly, "รายปี")]
    public void CycleLabel_returns_thai_string(BillingCycle cycle, string expected)
    {
        Assert.Equal(expected, BillingMath.CycleLabel(cycle));
    }

    [Fact]
    public void CycleLabel_unknown_cycle_falls_back_to_enum_ToString()
    {
        Assert.Equal("99", BillingMath.CycleLabel((BillingCycle)99));
    }
}

public class DisplayLabelsTests
{
    // ---------------------------------------------------------------- Type

    [Theory]
    [InlineData(ItemType.IotMaterial, "วัสดุ IoT")]
    [InlineData(ItemType.Server, "Server")]
    [InlineData(ItemType.Software, "Software")]
    [InlineData(ItemType.Other, "อื่น ๆ")]
    public void Type_returns_thai_label(ItemType type, string expected)
    {
        Assert.Equal(expected, DisplayLabels.Type(type));
    }

    [Fact]
    public void Type_unknown_falls_back_to_enum_ToString()
    {
        Assert.Equal("99", DisplayLabels.Type((ItemType)99));
    }

    // ---------------------------------------------------------------- CostType

    [Theory]
    [InlineData(CostType.OneTime, "จ่ายครั้งเดียว")]
    [InlineData(CostType.Recurring, "Subscription")]
    public void CostType_returns_thai_label(CostType cost, string expected)
    {
        Assert.Equal(expected, DisplayLabels.CostType(cost));
    }

    [Fact]
    public void CostType_unknown_falls_back_to_enum_ToString()
    {
        Assert.Equal("99", DisplayLabels.CostType((CostType)99));
    }

    // ---------------------------------------------------------------- Status

    [Theory]
    [InlineData(SubscriptionStatus.Active, "ใช้งานอยู่")]
    [InlineData(SubscriptionStatus.Cancelled, "ยกเลิกแล้ว")]
    [InlineData(SubscriptionStatus.Expired, "หมดอายุ")]
    public void Status_returns_thai_label(SubscriptionStatus status, string expected)
    {
        Assert.Equal(expected, DisplayLabels.Status(status));
    }

    [Fact]
    public void Status_unknown_falls_back_to_enum_ToString()
    {
        Assert.Equal("99", DisplayLabels.Status((SubscriptionStatus)99));
    }

    // ---------------------------------------------------------------- Cycle (delegates to BillingMath)

    [Theory]
    [InlineData(BillingCycle.Monthly, "รายเดือน")]
    [InlineData(BillingCycle.Quarterly, "ราย 3 เดือน")]
    [InlineData(BillingCycle.Yearly, "รายปี")]
    public void Cycle_delegates_to_BillingMath_CycleLabel(BillingCycle cycle, string expected)
    {
        Assert.Equal(expected, DisplayLabels.Cycle(cycle));
        // explicitly confirm the delegation contract
        Assert.Equal(BillingMath.CycleLabel(cycle), DisplayLabels.Cycle(cycle));
    }

    [Fact]
    public void Cycle_unknown_delegates_to_BillingMath_fallback()
    {
        Assert.Equal(BillingMath.CycleLabel((BillingCycle)99), DisplayLabels.Cycle((BillingCycle)99));
    }
}

public class AppRolesTests
{
    [Theory]
    [InlineData(AppRoles.Admin, "ผู้ดูแลระบบ")]
    [InlineData(AppRoles.Purchaser, "ฝ่ายจัดซื้อ")]
    [InlineData(AppRoles.Staff, "พนักงาน")]
    [InlineData(AppRoles.Viewer, "ผู้ชม")]
    public void DisplayName_returns_thai_label_for_known_roles(string role, string expected)
    {
        Assert.Equal(expected, AppRoles.DisplayName(role));
    }

    [Fact]
    public void DisplayName_unknown_role_returns_input_unchanged()
    {
        Assert.Equal("SOMETHING_ELSE", AppRoles.DisplayName("SOMETHING_ELSE"));
    }

    [Fact]
    public void DisplayName_is_case_sensitive_lowercase_role_not_matched()
    {
        // AUDIT[low]: DisplayName matches exact (uppercase) constants; a lowercase
        //   "admin" is treated as unknown and echoed back rather than localized.
        Assert.Equal("admin", AppRoles.DisplayName("admin"));
    }

    [Fact]
    public void DisplayName_empty_string_returns_empty()
    {
        Assert.Equal("", AppRoles.DisplayName(""));
    }

    [Fact]
    public void Role_constants_have_expected_values()
    {
        Assert.Equal("ADMIN", AppRoles.Admin);
        Assert.Equal("PURCHASER", AppRoles.Purchaser);
        Assert.Equal("STAFF", AppRoles.Staff);
        Assert.Equal("VIEWER", AppRoles.Viewer);
    }

    [Fact]
    public void All_contains_exactly_the_four_roles_in_order()
    {
        Assert.Equal(new[] { "ADMIN", "PURCHASER", "STAFF", "VIEWER" }, AppRoles.All);
    }

    [Fact]
    public void CanManage_is_comma_joined_admin_and_purchaser()
    {
        Assert.Equal("ADMIN,PURCHASER", AppRoles.CanManage);
    }

    [Fact]
    public void CanWithdraw_is_comma_joined_admin_purchaser_and_staff()
    {
        Assert.Equal("ADMIN,PURCHASER,STAFF", AppRoles.CanWithdraw);
    }

    [Fact]
    public void CanManage_does_not_include_staff_or_viewer()
    {
        var roles = AppRoles.CanManage.Split(',');
        Assert.DoesNotContain(AppRoles.Staff, roles);
        Assert.DoesNotContain(AppRoles.Viewer, roles);
    }

    [Fact]
    public void CanWithdraw_does_not_include_viewer()
    {
        var roles = AppRoles.CanWithdraw.Split(',');
        Assert.DoesNotContain(AppRoles.Viewer, roles);
    }
}

public class SeatUsageTests
{
    // ---------------------------------------------------------------- Available

    [Fact]
    public void Available_is_total_minus_used_when_used_below_total()
    {
        var u = new SeatUsage(1, "x", ItemType.Software, Used: 3, Total: 10);
        Assert.Equal(7, u.Available);
    }

    [Fact]
    public void Available_is_zero_when_used_equals_total()
    {
        var u = new SeatUsage(1, "x", ItemType.Software, Used: 10, Total: 10);
        Assert.Equal(0, u.Available);
    }

    [Fact]
    public void Available_clamps_to_zero_when_used_exceeds_total()
    {
        // Used > Total (oversubscribed) -> Max(0, ...) keeps it non-negative.
        var u = new SeatUsage(1, "x", ItemType.Software, Used: 15, Total: 10);
        Assert.Equal(0, u.Available);
    }

    [Fact]
    public void Available_with_zero_total_and_zero_used_is_zero()
    {
        var u = new SeatUsage(1, "x", ItemType.Software, Used: 0, Total: 0);
        Assert.Equal(0, u.Available);
    }

    // ---------------------------------------------------------------- UsedRatio

    [Fact]
    public void UsedRatio_is_used_over_total()
    {
        var u = new SeatUsage(1, "x", ItemType.Software, Used: 5, Total: 10);
        Assert.Equal(0.5, u.UsedRatio);
    }

    [Fact]
    public void UsedRatio_is_zero_when_total_is_zero()
    {
        var u = new SeatUsage(1, "x", ItemType.Software, Used: 5, Total: 0);
        Assert.Equal(0, u.UsedRatio);
    }

    [Fact]
    public void UsedRatio_is_zero_when_total_is_negative()
    {
        // Total <= 0 branch also covers negative totals.
        var u = new SeatUsage(1, "x", ItemType.Software, Used: 5, Total: -3);
        Assert.Equal(0, u.UsedRatio);
    }

    [Fact]
    public void UsedRatio_can_exceed_one_when_oversubscribed()
    {
        // AUDIT[low]: UsedRatio is uncapped; oversubscribed seats yield ratio > 1.0,
        //   which a progress bar UI may render past 100% unless it clamps.
        var u = new SeatUsage(1, "x", ItemType.Software, Used: 15, Total: 10);
        Assert.Equal(1.5, u.UsedRatio);
    }

    [Fact]
    public void UsedRatio_full_seats_is_one()
    {
        var u = new SeatUsage(1, "x", ItemType.Software, Used: 10, Total: 10);
        Assert.Equal(1.0, u.UsedRatio);
    }
}

public class MonthlySpendPointTests
{
    [Fact]
    public void Total_sums_all_four_categories()
    {
        var p = new MonthlySpendPoint("2026-06", Iot: 100m, Server: 200m, Software: 300m, Other: 400m);
        Assert.Equal(1000m, p.Total);
    }

    [Fact]
    public void Total_is_zero_when_all_categories_zero()
    {
        var p = new MonthlySpendPoint("2026-06", 0m, 0m, 0m, 0m);
        Assert.Equal(0m, p.Total);
    }

    [Fact]
    public void Total_handles_negative_components()
    {
        // refunds / adjustments could be negative -> Total is a plain sum.
        var p = new MonthlySpendPoint("2026-06", Iot: 100m, Server: -50m, Software: 0m, Other: 25m);
        Assert.Equal(75m, p.Total);
    }
}

public class SpendReportRowTests
{
    [Fact]
    public void Total_is_one_time_plus_recurring()
    {
        var r = new SpendReportRow("Software", OneTime: 1000m, Recurring: 500m, Count: 3);
        Assert.Equal(1500m, r.Total);
    }

    [Fact]
    public void Total_is_zero_when_both_components_zero()
    {
        var r = new SpendReportRow("Software", 0m, 0m, 0);
        Assert.Equal(0m, r.Total);
    }

    [Fact]
    public void Total_ignores_count()
    {
        // Count is informational only and must not affect the money Total.
        var r = new SpendReportRow("X", OneTime: 10m, Recurring: 20m, Count: 999);
        Assert.Equal(30m, r.Total);
    }
}

public class OperationResultTests
{
    [Fact]
    public void Ok_sets_success_true_and_no_error()
    {
        var r = OperationResult.Ok();
        Assert.True(r.Success);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Fail_sets_success_false_and_carries_error()
    {
        var r = OperationResult.Fail("เกิดข้อผิดพลาด");
        Assert.False(r.Success);
        Assert.Equal("เกิดข้อผิดพลาด", r.Error);
    }

    [Fact]
    public void Generic_Ok_sets_success_true_value_and_no_error()
    {
        var r = OperationResult<int>.Ok(42);
        Assert.True(r.Success);
        Assert.Equal(42, r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Generic_Ok_can_carry_a_null_reference_value()
    {
        // AUDIT[low]: OperationResult<T>.Ok(null) yields Success=true with Value=null,
        //   so a caller that checks Success but dereferences Value can still NRE.
        var r = OperationResult<string>.Ok(null!);
        Assert.True(r.Success);
        Assert.Null(r.Value);
    }

    [Fact]
    public void Generic_Fail_sets_success_false_default_value_and_error()
    {
        var r = OperationResult<int>.Fail("ไม่พบรายการ");
        Assert.False(r.Success);
        Assert.Equal(0, r.Value); // default(int)
        Assert.Equal("ไม่พบรายการ", r.Error);
    }

    [Fact]
    public void Generic_Fail_reference_type_has_null_value()
    {
        var r = OperationResult<string>.Fail("boom");
        Assert.False(r.Success);
        Assert.Null(r.Value);
        Assert.Equal("boom", r.Error);
    }

    [Fact]
    public void Records_with_same_data_are_value_equal()
    {
        Assert.Equal(OperationResult.Ok(), OperationResult.Ok());
        Assert.Equal(OperationResult.Fail("e"), OperationResult.Fail("e"));
        Assert.Equal(OperationResult<int>.Ok(5), OperationResult<int>.Ok(5));
    }
}

public class ItemComputedTests
{
    // ---------------------------------------------------------------- IsLowStock

    [Fact]
    public void IsLowStock_true_when_iot_quantity_below_min()
    {
        var item = new Item { Type = ItemType.IotMaterial, Quantity = 5, MinQuantity = 10 };
        Assert.True(item.IsLowStock);
    }

    [Fact]
    public void IsLowStock_true_when_iot_quantity_equals_min_boundary()
    {
        // Quantity <= MinQuantity is inclusive: equal counts as low stock.
        var item = new Item { Type = ItemType.IotMaterial, Quantity = 10, MinQuantity = 10 };
        Assert.True(item.IsLowStock);
    }

    [Fact]
    public void IsLowStock_false_when_iot_quantity_above_min()
    {
        var item = new Item { Type = ItemType.IotMaterial, Quantity = 11, MinQuantity = 10 };
        Assert.False(item.IsLowStock);
    }

    [Fact]
    public void IsLowStock_true_for_iot_with_zero_quantity_and_zero_min()
    {
        // AUDIT[low]: an IoT item with MinQuantity=0 and Quantity=0 is flagged low stock
        //   (0 <= 0). Items intentionally not stocked still raise the low-stock alert.
        var item = new Item { Type = ItemType.IotMaterial, Quantity = 0, MinQuantity = 0 };
        Assert.True(item.IsLowStock);
    }

    [Theory]
    [InlineData(ItemType.Server)]
    [InlineData(ItemType.Software)]
    [InlineData(ItemType.Other)]
    public void IsLowStock_false_for_non_iot_even_when_quantity_below_min(ItemType type)
    {
        // Only IotMaterial participates in stock alerts; others are always false.
        var item = new Item { Type = type, Quantity = 0, MinQuantity = 100 };
        Assert.False(item.IsLowStock);
    }

    // ---------------------------------------------------------------- TracksSeats

    [Theory]
    [InlineData(ItemType.Server)]
    [InlineData(ItemType.Software)]
    public void TracksSeats_true_for_server_or_software_with_total_seats(ItemType type)
    {
        var item = new Item { Type = type, TotalSeats = 5 };
        Assert.True(item.TracksSeats);
    }

    [Theory]
    [InlineData(ItemType.Server)]
    [InlineData(ItemType.Software)]
    public void TracksSeats_false_when_total_seats_null(ItemType type)
    {
        var item = new Item { Type = type, TotalSeats = null };
        Assert.False(item.TracksSeats);
    }

    [Fact]
    public void TracksSeats_true_when_total_seats_is_zero()
    {
        // AUDIT[low]: HasValue (not >0) is the gate, so TotalSeats=0 still "tracks seats"
        //   even though there are no seats to assign.
        var item = new Item { Type = ItemType.Software, TotalSeats = 0 };
        Assert.True(item.TracksSeats);
    }

    [Theory]
    [InlineData(ItemType.IotMaterial)]
    [InlineData(ItemType.Other)]
    public void TracksSeats_false_for_iot_or_other_even_with_total_seats(ItemType type)
    {
        var item = new Item { Type = type, TotalSeats = 5 };
        Assert.False(item.TracksSeats);
    }

    // ---------------------------------------------------------------- IsSubscription

    [Fact]
    public void IsSubscription_true_when_cost_type_recurring()
    {
        var item = new Item { CostType = CostType.Recurring };
        Assert.True(item.IsSubscription);
    }

    [Fact]
    public void IsSubscription_false_when_cost_type_one_time()
    {
        var item = new Item { CostType = CostType.OneTime };
        Assert.False(item.IsSubscription);
    }

    [Fact]
    public void IsSubscription_independent_of_item_type()
    {
        // Recurring cost on an IoT material still reports as a subscription.
        var item = new Item { Type = ItemType.IotMaterial, CostType = CostType.Recurring };
        Assert.True(item.IsSubscription);
    }
}

public class LicenseAssignmentComputedTests
{
    [Fact]
    public void IsActive_true_when_released_at_null()
    {
        var l = new LicenseAssignment { ReleasedAt = null };
        Assert.True(l.IsActive);
    }

    [Fact]
    public void IsActive_false_when_released_at_set()
    {
        var l = new LicenseAssignment { ReleasedAt = new DateTime(2026, 1, 1) };
        Assert.False(l.IsActive);
    }

    [Fact]
    public void IsActive_false_even_when_released_in_the_future()
    {
        // AUDIT[low]: IsActive is purely a null check on ReleasedAt; a future ReleasedAt
        //   marks the seat inactive immediately rather than on the release date.
        var l = new LicenseAssignment { ReleasedAt = new DateTime(2099, 1, 1) };
        Assert.False(l.IsActive);
    }

    [Fact]
    public void IsActive_false_when_released_at_default_datetime()
    {
        // default(DateTime) is non-null -> counts as released.
        var l = new LicenseAssignment { ReleasedAt = default(DateTime) };
        Assert.False(l.IsActive);
    }
}
