using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>
/// ครอบคลุม ReportService ทุกฟังก์ชัน/ทุก branch:
/// GetSpendReportAsync (date range, filters, grouping ทั้ง 5 แบบ, totals),
/// GetWithdrawalReportAsync (range + type filter, grouping per item, ordering),
/// GetLicenseUsageReportAsync (Server/Software, used/everAssigned/total, ordering),
/// GetPurchasersAsync (all users ordered by FullName).
/// ReportService รับเฉพาะ Db.Factory (read-only — ไม่มี accessor / ไม่มี permission gating)
/// </summary>
public class ReportServiceTests(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    private ReportService NewService() => new(Db.Factory);

    // ============================================================
    // GetSpendReportAsync — empty / happy path / totals
    // ============================================================

    [Fact]
    public async Task GetSpendReport_empty_db_returns_zero_totals_and_no_rows()
    {
        var svc = NewService();

        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type);

        Assert.Equal(0m, result.GrandTotal);
        Assert.Equal(0m, result.OneTimeTotal);
        Assert.Equal(0m, result.RecurringTotal);
        Assert.Equal(0, result.PurchaseCount);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task GetSpendReport_totals_split_onetime_and_recurring_correctly()
    {
        var user = await Db.AddUserAsync("ผู้ซื้อ");
        var item = await Db.AddIotItemAsync();
        // 2 one-time (300 + 200 = 500) + 1 recurring (1000)
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 300m; p.IsRecurringCharge = false; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 200m; p.IsRecurringCharge = false; p.Date = new DateTime(2026, 3, 2); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 1000m; p.IsRecurringCharge = true; p.Date = new DateTime(2026, 3, 3); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type);

        Assert.Equal(1500m, result.GrandTotal);
        Assert.Equal(500m, result.OneTimeTotal);
        Assert.Equal(1000m, result.RecurringTotal);
        Assert.Equal(3, result.PurchaseCount);
    }

    // ============================================================
    // GetSpendReportAsync — date range boundaries
    // ============================================================

    [Fact]
    public async Task GetSpendReport_includes_purchase_on_from_date_midnight()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        // exactly at from.Date midnight => included (>= from.Date)
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 10, 0, 0, 0); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 3, 10), new DateTime(2026, 3, 10), ReportGroupBy.Type);

        Assert.Equal(1, result.PurchaseCount);
        Assert.Equal(100m, result.GrandTotal);
    }

    [Fact]
    public async Task GetSpendReport_includes_purchase_late_on_to_date_because_to_is_inclusive_whole_day()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        // 23:59 on the `to` day must be included because toExclusive = to.Date.AddDays(1)
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 10, 23, 59, 0); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 3, 1), new DateTime(2026, 3, 10), ReportGroupBy.Type);

        Assert.Equal(1, result.PurchaseCount);
        Assert.Equal(100m, result.GrandTotal);
    }

    [Fact]
    public async Task GetSpendReport_excludes_purchase_at_midnight_of_day_after_to()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        // exactly midnight of the day AFTER `to` => excluded (< toExclusive is false)
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 11, 0, 0, 0); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 3, 1), new DateTime(2026, 3, 10), ReportGroupBy.Type);

        Assert.Equal(0, result.PurchaseCount);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task GetSpendReport_excludes_purchase_before_from_date()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        // 23:59 the day BEFORE from.Date => excluded
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 9, 23, 59, 0); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 3, 10), new DateTime(2026, 3, 20), ReportGroupBy.Type);

        Assert.Equal(0, result.PurchaseCount);
    }

    [Fact]
    public async Task GetSpendReport_from_time_component_is_ignored_uses_date_only()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        // purchase at 08:00 on 2026-03-10; caller passes from with a LATER time of day (15:00)
        // because service uses from.Date, the 08:00 purchase is still included.
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 10, 8, 0, 0); });

        var svc = NewService();
        // AUDIT[low]: from.Date strips the caller-supplied time-of-day; a 15:00 `from` still
        // captures the 08:00 purchase on the same date (date-granularity, not timestamp).
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 3, 10, 15, 0, 0), new DateTime(2026, 3, 20), ReportGroupBy.Type);

        Assert.Equal(1, result.PurchaseCount);
    }

    [Fact]
    public async Task GetSpendReport_inverted_range_to_before_from_returns_nothing()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 10); });

        var svc = NewService();
        // AUDIT[low]: when to < from the service silently returns an empty report
        // (no validation / no error) — caller cannot distinguish "no data" from "bad range".
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 3, 20), new DateTime(2026, 3, 10), ReportGroupBy.Type);

        Assert.Equal(0, result.PurchaseCount);
        Assert.Empty(result.Rows);
    }

    // ============================================================
    // GetSpendReportAsync — filters: type / category / purchaser / recurringOnly
    // ============================================================

    [Fact]
    public async Task GetSpendReport_type_filter_keeps_only_matching_item_type()
    {
        var user = await Db.AddUserAsync();
        var iot = await Db.AddIotItemAsync();
        var sw = await Db.AddSeatItemAsync(ItemType.Software);
        await Db.AddPurchaseAsync(iot.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(sw.Id, user.Id, p => { p.TotalCost = 999m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type, type: ItemType.Software);

        Assert.Equal(1, result.PurchaseCount);
        Assert.Equal(999m, result.GrandTotal);
    }

    [Fact]
    public async Task GetSpendReport_category_filter_keeps_only_matching_category()
    {
        var user = await Db.AddUserAsync();
        var catA = await Db.AddCategoryAsync("หมวด A");
        var catB = await Db.AddCategoryAsync("หมวด B");
        var itemA = await Db.AddIotItemAsync(categoryId: catA.Id);
        var itemB = await Db.AddIotItemAsync(categoryId: catB.Id);
        await Db.AddPurchaseAsync(itemA.Id, user.Id, p => { p.TotalCost = 50m; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(itemB.Id, user.Id, p => { p.TotalCost = 70m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Category, categoryId: catB.Id);

        Assert.Equal(1, result.PurchaseCount);
        Assert.Equal(70m, result.GrandTotal);
        Assert.Single(result.Rows);
        Assert.Equal("หมวด B", result.Rows[0].Key);
    }

    [Fact]
    public async Task GetSpendReport_purchaser_filter_keeps_only_matching_purchaser()
    {
        var u1 = await Db.AddUserAsync("คนหนึ่ง");
        var u2 = await Db.AddUserAsync("คนสอง");
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, u1.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, u2.Id, p => { p.TotalCost = 200m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Purchaser, purchaserId: u2.Id);

        Assert.Equal(1, result.PurchaseCount);
        Assert.Equal(200m, result.GrandTotal);
    }

    [Fact]
    public async Task GetSpendReport_empty_string_purchaserId_is_treated_as_no_filter()
    {
        var u1 = await Db.AddUserAsync("คนหนึ่ง");
        var u2 = await Db.AddUserAsync("คนสอง");
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, u1.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, u2.Id, p => { p.TotalCost = 200m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        // string.IsNullOrEmpty("") is true => filter skipped => both purchases counted
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Purchaser, purchaserId: "");

        Assert.Equal(2, result.PurchaseCount);
        Assert.Equal(300m, result.GrandTotal);
    }

    [Fact]
    public async Task GetSpendReport_unknown_purchaserId_returns_empty()
    {
        var u1 = await Db.AddUserAsync("คนหนึ่ง");
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, u1.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Purchaser, purchaserId: "no-such-user");

        Assert.Equal(0, result.PurchaseCount);
    }

    [Fact]
    public async Task GetSpendReport_recurringOnly_true_keeps_only_recurring_charges()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.IsRecurringCharge = false; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 500m; p.IsRecurringCharge = true; p.Date = new DateTime(2026, 3, 2); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type, recurringOnly: true);

        Assert.Equal(1, result.PurchaseCount);
        Assert.Equal(500m, result.GrandTotal);
        Assert.Equal(500m, result.RecurringTotal);
        Assert.Equal(0m, result.OneTimeTotal);
    }

    [Fact]
    public async Task GetSpendReport_recurringOnly_false_keeps_only_onetime_charges()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.IsRecurringCharge = false; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 500m; p.IsRecurringCharge = true; p.Date = new DateTime(2026, 3, 2); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type, recurringOnly: false);

        Assert.Equal(1, result.PurchaseCount);
        Assert.Equal(100m, result.GrandTotal);
        Assert.Equal(100m, result.OneTimeTotal);
        Assert.Equal(0m, result.RecurringTotal);
    }

    [Fact]
    public async Task GetSpendReport_recurringOnly_null_keeps_both()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.IsRecurringCharge = false; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 500m; p.IsRecurringCharge = true; p.Date = new DateTime(2026, 3, 2); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type, recurringOnly: null);

        Assert.Equal(2, result.PurchaseCount);
        Assert.Equal(600m, result.GrandTotal);
    }

    [Fact]
    public async Task GetSpendReport_all_filters_combine_with_AND_semantics()
    {
        var u1 = await Db.AddUserAsync("ตรงเงื่อนไข");
        var u2 = await Db.AddUserAsync("ไม่ตรง");
        var catA = await Db.AddCategoryAsync("หมวด A");
        var swA = await Db.AddSeatItemAsync(ItemType.Software, categoryId: catA.Id);
        var iotA = await Db.AddIotItemAsync(categoryId: catA.Id);

        // Matches all filters: type=Software, category=A, purchaser=u1, recurring=true
        await Db.AddPurchaseAsync(swA.Id, u1.Id, p => { p.TotalCost = 777m; p.IsRecurringCharge = true; p.Date = new DateTime(2026, 3, 5); });
        // Fails type filter (IoT)
        await Db.AddPurchaseAsync(iotA.Id, u1.Id, p => { p.TotalCost = 11m; p.IsRecurringCharge = true; p.Date = new DateTime(2026, 3, 5); });
        // Fails purchaser filter
        await Db.AddPurchaseAsync(swA.Id, u2.Id, p => { p.TotalCost = 22m; p.IsRecurringCharge = true; p.Date = new DateTime(2026, 3, 5); });
        // Fails recurring filter (one-time)
        await Db.AddPurchaseAsync(swA.Id, u1.Id, p => { p.TotalCost = 33m; p.IsRecurringCharge = false; p.Date = new DateTime(2026, 3, 5); });
        // Fails date filter
        await Db.AddPurchaseAsync(swA.Id, u1.Id, p => { p.TotalCost = 44m; p.IsRecurringCharge = true; p.Date = new DateTime(2025, 3, 5); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type,
            type: ItemType.Software, categoryId: catA.Id, purchaserId: u1.Id, recurringOnly: true);

        Assert.Equal(1, result.PurchaseCount);
        Assert.Equal(777m, result.GrandTotal);
    }

    // ============================================================
    // GetSpendReportAsync — grouping (Type / Category / Purchaser / Supplier / Month)
    // ============================================================

    [Fact]
    public async Task GetSpendReport_groupBy_Type_uses_thai_display_labels_as_key()
    {
        var user = await Db.AddUserAsync();
        var iot = await Db.AddIotItemAsync();
        var server = await Db.AddSeatItemAsync(ItemType.Server);
        await Db.AddPurchaseAsync(iot.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(server.Id, user.Id, p => { p.TotalCost = 200m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type);

        Assert.Equal(2, result.Rows.Count);
        var keys = result.Rows.Select(r => r.Key).ToList();
        Assert.Contains("วัสดุ IoT", keys);   // DisplayLabels.Type(IotMaterial)
        Assert.Contains("Server", keys);        // DisplayLabels.Type(Server)
    }

    [Fact]
    public async Task GetSpendReport_groupBy_Category_uses_category_name_as_key_and_aggregates()
    {
        var user = await Db.AddUserAsync();
        var cat = await Db.AddCategoryAsync("กล้องวงจรปิด");
        var item = await Db.AddIotItemAsync(categoryId: cat.Id);
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.IsRecurringCharge = false; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 250m; p.IsRecurringCharge = true; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Category);

        Assert.Single(result.Rows);
        var row = result.Rows[0];
        Assert.Equal("กล้องวงจรปิด", row.Key);
        Assert.Equal(100m, row.OneTime);
        Assert.Equal(250m, row.Recurring);
        Assert.Equal(350m, row.Total);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public async Task GetSpendReport_groupBy_Purchaser_uses_FullName_when_present()
    {
        var user = await Db.AddUserAsync("สมชาย ใจดี");
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Purchaser);

        Assert.Single(result.Rows);
        Assert.Equal("สมชาย ใจดี", result.Rows[0].Key);
    }

    [Fact]
    public async Task GetSpendReport_groupBy_Purchaser_falls_back_to_email_when_fullname_blank()
    {
        // FullName "" is whitespace => string.IsNullOrWhiteSpace true => use Email
        var user = await Db.AddUserAsync(fullName: "", email: "fallback@test.local");
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Purchaser);

        Assert.Single(result.Rows);
        Assert.Equal("fallback@test.local", result.Rows[0].Key);
    }

    [Fact]
    public async Task GetSpendReport_groupBy_Purchaser_falls_back_to_whitespace_fullname_to_email()
    {
        // FullName "   " is whitespace too
        var user = await Db.AddUserAsync(fullName: "   ", email: "ws@test.local");
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Purchaser);

        Assert.Single(result.Rows);
        Assert.Equal("ws@test.local", result.Rows[0].Key);
    }

    [Fact]
    public async Task GetSpendReport_groupBy_Supplier_uses_supplier_name()
    {
        var user = await Db.AddUserAsync();
        var sup = await Db.AddSupplierAsync("บริษัทผู้ขาย จำกัด");
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.SupplierId = sup.Id; p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Supplier);

        Assert.Single(result.Rows);
        Assert.Equal("บริษัทผู้ขาย จำกัด", result.Rows[0].Key);
    }

    [Fact]
    public async Task GetSpendReport_groupBy_Supplier_null_supplier_uses_placeholder()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        // no SupplierId => Supplier navigation null => "ไม่ระบุผู้ขาย"
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Supplier);

        Assert.Single(result.Rows);
        Assert.Equal("ไม่ระบุผู้ขาย", result.Rows[0].Key);
    }

    [Fact]
    public async Task GetSpendReport_groupBy_Supplier_mixed_null_and_named_produce_two_rows()
    {
        var user = await Db.AddUserAsync();
        var sup = await Db.AddSupplierAsync("ผู้ขาย X");
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.SupplierId = sup.Id; p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 50m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Supplier);

        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, r => r.Key == "ผู้ขาย X");
        Assert.Contains(result.Rows, r => r.Key == "ไม่ระบุผู้ขาย");
    }

    [Fact]
    public async Task GetSpendReport_groupBy_Month_uses_yyyy_MM_keys()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 1, 15); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 200m; p.Date = new DateTime(2026, 2, 15); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 50m; p.Date = new DateTime(2026, 2, 20); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Month);

        Assert.Equal(2, result.Rows.Count);
        var feb = result.Rows.Single(r => r.Key == "2026-02");
        Assert.Equal(250m, feb.Total);
        Assert.Equal(2, feb.Count);
        var jan = result.Rows.Single(r => r.Key == "2026-01");
        Assert.Equal(100m, jan.Total);
    }

    [Fact]
    public async Task GetSpendReport_rows_ordered_by_total_descending()
    {
        var user = await Db.AddUserAsync();
        var catLow = await Db.AddCategoryAsync("น้อย");
        var catHigh = await Db.AddCategoryAsync("มาก");
        var catMid = await Db.AddCategoryAsync("กลาง");
        var itemLow = await Db.AddIotItemAsync(categoryId: catLow.Id);
        var itemHigh = await Db.AddIotItemAsync(categoryId: catHigh.Id);
        var itemMid = await Db.AddIotItemAsync(categoryId: catMid.Id);
        await Db.AddPurchaseAsync(itemLow.Id, user.Id, p => { p.TotalCost = 10m; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(itemHigh.Id, user.Id, p => { p.TotalCost = 900m; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(itemMid.Id, user.Id, p => { p.TotalCost = 100m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Category);

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("มาก", result.Rows[0].Key);
        Assert.Equal("กลาง", result.Rows[1].Key);
        Assert.Equal("น้อย", result.Rows[2].Key);
        // descending by Total
        Assert.True(result.Rows[0].Total >= result.Rows[1].Total);
        Assert.True(result.Rows[1].Total >= result.Rows[2].Total);
    }

    [Fact]
    public async Task GetSpendReport_negative_total_cost_is_summed_as_is()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        // a refund / credit modelled as negative TotalCost (one-time)
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 100m; p.IsRecurringCharge = false; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = -30m; p.IsRecurringCharge = false; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        // AUDIT[low]: there is no guard against negative TotalCost; refunds (if any) net into totals,
        // which may or may not be intended for a "spend" report.
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type);

        Assert.Equal(70m, result.GrandTotal);
        Assert.Equal(70m, result.OneTimeTotal);
        Assert.Equal(2, result.PurchaseCount);
    }

    [Fact]
    public async Task GetSpendReport_decimal_with_cents_preserved_to_two_places()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 10.33m; p.Date = new DateTime(2026, 3, 1); });
        await Db.AddPurchaseAsync(item.Id, user.Id, p => { p.TotalCost = 0.34m; p.Date = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var result = await svc.GetSpendReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), ReportGroupBy.Type);

        Assert.Equal(10.67m, result.GrandTotal);
    }

    // ============================================================
    // GetWithdrawalReportAsync
    // ============================================================

    [Fact]
    public async Task GetWithdrawalReport_empty_db_returns_empty_list()
    {
        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetWithdrawalReport_groups_per_item_sums_quantity_and_counts()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.Quantity = 3; w.WithdrawnAt = new DateTime(2026, 3, 1); });
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.Quantity = 5; w.WithdrawnAt = new DateTime(2026, 3, 5); });

        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(item.Id, row.ItemId);
        Assert.Equal("รายการทดสอบ", row.ItemName);
        Assert.Equal(8, row.TotalQuantity);
        Assert.Equal(2, row.Count);
        Assert.Equal(new DateTime(2026, 3, 5), row.LastWithdrawnAt);
    }

    [Fact]
    public async Task GetWithdrawalReport_LastWithdrawnAt_is_max_date_regardless_of_insert_order()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        // insert the latest date first to ensure Max (not last) is used
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.Quantity = 1; w.WithdrawnAt = new DateTime(2026, 4, 30); });
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.Quantity = 1; w.WithdrawnAt = new DateTime(2026, 1, 2); });

        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        Assert.Single(rows);
        Assert.Equal(new DateTime(2026, 4, 30), rows[0].LastWithdrawnAt);
    }

    [Fact]
    public async Task GetWithdrawalReport_rows_ordered_by_total_quantity_descending()
    {
        var user = await Db.AddUserAsync();
        var small = await Db.AddIotItemAsync(configure: i => i.Name = "น้อย");
        var big = await Db.AddIotItemAsync(configure: i => i.Name = "มาก");
        await Db.AddWithdrawalAsync(small.Id, user.Id, w => { w.Quantity = 2; w.WithdrawnAt = new DateTime(2026, 3, 1); });
        await Db.AddWithdrawalAsync(big.Id, user.Id, w => { w.Quantity = 50; w.WithdrawnAt = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        Assert.Equal(2, rows.Count);
        Assert.Equal("มาก", rows[0].ItemName);
        Assert.Equal(50, rows[0].TotalQuantity);
        Assert.Equal("น้อย", rows[1].ItemName);
    }

    [Fact]
    public async Task GetWithdrawalReport_type_filter_keeps_only_matching_item_type()
    {
        var user = await Db.AddUserAsync();
        var iot = await Db.AddIotItemAsync(configure: i => i.Name = "ไอโอที");
        // a Software item that (oddly) has a withdrawal — exercise the type filter against it
        var sw = await Db.AddSeatItemAsync(ItemType.Software, configure: i => i.Name = "ซอฟต์แวร์");
        await Db.AddWithdrawalAsync(iot.Id, user.Id, w => { w.Quantity = 4; w.WithdrawnAt = new DateTime(2026, 3, 1); });
        await Db.AddWithdrawalAsync(sw.Id, user.Id, w => { w.Quantity = 9; w.WithdrawnAt = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), type: ItemType.IotMaterial);

        Assert.Single(rows);
        Assert.Equal("ไอโอที", rows[0].ItemName);
        Assert.Equal(4, rows[0].TotalQuantity);
    }

    [Fact]
    public async Task GetWithdrawalReport_includes_withdrawal_late_on_to_date()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.Quantity = 1; w.WithdrawnAt = new DateTime(2026, 3, 10, 23, 30, 0); });

        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(new DateTime(2026, 3, 1), new DateTime(2026, 3, 10));

        Assert.Single(rows);
    }

    [Fact]
    public async Task GetWithdrawalReport_excludes_withdrawal_at_midnight_after_to()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.Quantity = 1; w.WithdrawnAt = new DateTime(2026, 3, 11, 0, 0, 0); });

        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(new DateTime(2026, 3, 1), new DateTime(2026, 3, 10));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetWithdrawalReport_excludes_withdrawal_before_from_date()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.Quantity = 1; w.WithdrawnAt = new DateTime(2026, 2, 28, 23, 59, 0); });

        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetWithdrawalReport_same_name_different_items_stay_separate_rows()
    {
        // grouping key is { ItemId, Name } so two items sharing a name are NOT merged.
        var user = await Db.AddUserAsync();
        var a = await Db.AddIotItemAsync(configure: i => i.Name = "ซ้ำชื่อ");
        var b = await Db.AddIotItemAsync(configure: i => i.Name = "ซ้ำชื่อ");
        await Db.AddWithdrawalAsync(a.Id, user.Id, w => { w.Quantity = 1; w.WithdrawnAt = new DateTime(2026, 3, 1); });
        await Db.AddWithdrawalAsync(b.Id, user.Id, w => { w.Quantity = 2; w.WithdrawnAt = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("ซ้ำชื่อ", r.ItemName));
        Assert.Contains(rows, r => r.ItemId == a.Id);
        Assert.Contains(rows, r => r.ItemId == b.Id);
    }

    [Fact]
    public async Task GetWithdrawalReport_zero_quantity_withdrawal_counts_but_adds_no_quantity()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddWithdrawalAsync(item.Id, user.Id, w => { w.Quantity = 0; w.WithdrawnAt = new DateTime(2026, 3, 1); });

        var svc = NewService();
        var rows = await svc.GetWithdrawalReportAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        Assert.Single(rows);
        Assert.Equal(0, rows[0].TotalQuantity);
        Assert.Equal(1, rows[0].Count);
    }

    // ============================================================
    // GetLicenseUsageReportAsync
    // ============================================================

    [Fact]
    public async Task GetLicenseUsage_empty_db_returns_empty_list()
    {
        var svc = NewService();
        var rows = await svc.GetLicenseUsageReportAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetLicenseUsage_ignores_iot_and_other_item_types()
    {
        await Db.AddIotItemAsync(configure: i => i.Name = "iot");
        await Db.AddItemAsync(i => { i.Type = ItemType.Other; i.Name = "other"; });
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5, configure: i => i.Name = "sw");

        var svc = NewService();
        var rows = await svc.GetLicenseUsageReportAsync();

        Assert.Single(rows);
        Assert.Equal("sw", rows[0].ItemName);
        Assert.Equal(ItemType.Software, rows[0].Type);
    }

    [Fact]
    public async Task GetLicenseUsage_item_without_assignments_shows_zero_used_and_zero_ever()
    {
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10, configure: i => i.Name = "ว่าง");

        var svc = NewService();
        var rows = await svc.GetLicenseUsageReportAsync();

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(0, row.Used);
        Assert.Equal(0, row.EverAssigned);
        Assert.Equal(10, row.Total);
    }

    [Fact]
    public async Task GetLicenseUsage_used_counts_only_active_ever_counts_all()
    {
        var assignedTo1 = await Db.AddUserAsync("ผู้ใช้ 1");
        var assignedTo2 = await Db.AddUserAsync("ผู้ใช้ 2");
        var assignedTo3 = await Db.AddUserAsync("ผู้ใช้ 3");
        var admin = await Db.AddUserAsync("แอดมิน");
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10);

        // 2 active (ReleasedAt null), 1 released
        await Db.AddLicenseAsync(sw.Id, assignedTo1.Id, admin.Id);
        await Db.AddLicenseAsync(sw.Id, assignedTo2.Id, admin.Id);
        await Db.AddLicenseAsync(sw.Id, assignedTo3.Id, admin.Id, releasedAt: new DateTime(2026, 5, 1));

        var svc = NewService();
        var rows = await svc.GetLicenseUsageReportAsync();

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(2, row.Used);          // active only
        Assert.Equal(3, row.EverAssigned);  // including released
        Assert.Equal(10, row.Total);
    }

    [Fact]
    public async Task GetLicenseUsage_total_seats_null_reported_as_zero()
    {
        // Server item with TotalSeats left null
        var server = await Db.AddItemAsync(i => { i.Type = ItemType.Server; i.Name = "no-seats"; i.TotalSeats = null; });
        var assignee = await Db.AddUserAsync("ผู้ใช้");
        var admin = await Db.AddUserAsync("แอดมิน");
        await Db.AddLicenseAsync(server.Id, assignee.Id, admin.Id);

        var svc = NewService();
        var rows = await svc.GetLicenseUsageReportAsync();

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(0, row.Total);   // TotalSeats ?? 0
        Assert.Equal(1, row.Used);
        // AUDIT[medium]: Used (1) can exceed Total (0) when TotalSeats is null;
        // the report exposes an over-allocated/inconsistent state with no guard.
        Assert.True(row.Used > row.Total);
    }

    [Fact]
    public async Task GetLicenseUsage_used_can_exceed_total_when_more_active_than_seats()
    {
        var a1 = await Db.AddUserAsync("a1");
        var a2 = await Db.AddUserAsync("a2");
        var a3 = await Db.AddUserAsync("a3");
        var admin = await Db.AddUserAsync("แอดมิน");
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 1);
        await Db.AddLicenseAsync(sw.Id, a1.Id, admin.Id);
        await Db.AddLicenseAsync(sw.Id, a2.Id, admin.Id);
        await Db.AddLicenseAsync(sw.Id, a3.Id, admin.Id);

        var svc = NewService();
        var rows = await svc.GetLicenseUsageReportAsync();

        Assert.Single(rows);
        // AUDIT[medium]: report does not flag over-allocation — 3 active assignments against 1 seat
        // is returned as Used=3 / Total=1 without any warning.
        Assert.Equal(3, rows[0].Used);
        Assert.Equal(1, rows[0].Total);
    }

    [Fact]
    public async Task GetLicenseUsage_rows_ordered_by_used_descending()
    {
        var admin = await Db.AddUserAsync("แอดมิน");
        var u1 = await Db.AddUserAsync("u1");
        var u2 = await Db.AddUserAsync("u2");
        var u3 = await Db.AddUserAsync("u3");

        var low = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 10, configure: i => i.Name = "ใช้น้อย");
        var high = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 10, configure: i => i.Name = "ใช้มาก");

        await Db.AddLicenseAsync(low.Id, u1.Id, admin.Id);
        await Db.AddLicenseAsync(high.Id, u2.Id, admin.Id);
        await Db.AddLicenseAsync(high.Id, u3.Id, admin.Id);

        var svc = NewService();
        var rows = await svc.GetLicenseUsageReportAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal("ใช้มาก", rows[0].ItemName);
        Assert.Equal(2, rows[0].Used);
        Assert.Equal("ใช้น้อย", rows[1].ItemName);
        Assert.Equal(1, rows[1].Used);
    }

    [Fact]
    public async Task GetLicenseUsage_item_all_released_shows_zero_used_but_nonzero_ever()
    {
        var assignee = await Db.AddUserAsync("ผู้ใช้");
        var admin = await Db.AddUserAsync("แอดมิน");
        var sw = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 5);
        await Db.AddLicenseAsync(sw.Id, assignee.Id, admin.Id, releasedAt: new DateTime(2026, 1, 1));

        var svc = NewService();
        var rows = await svc.GetLicenseUsageReportAsync();

        Assert.Single(rows);
        Assert.Equal(0, rows[0].Used);
        Assert.Equal(1, rows[0].EverAssigned);
        Assert.Equal(5, rows[0].Total);
    }

    [Fact]
    public async Task GetLicenseUsage_includes_both_server_and_software()
    {
        var server = await Db.AddSeatItemAsync(ItemType.Server, totalSeats: 3, configure: i => i.Name = "เซิร์ฟเวอร์");
        var software = await Db.AddSeatItemAsync(ItemType.Software, totalSeats: 7, configure: i => i.Name = "ซอฟต์แวร์");

        var svc = NewService();
        var rows = await svc.GetLicenseUsageReportAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Type == ItemType.Server && r.Total == 3);
        Assert.Contains(rows, r => r.Type == ItemType.Software && r.Total == 7);
    }

    // ============================================================
    // GetPurchasersAsync
    // ============================================================

    [Fact]
    public async Task GetPurchasers_empty_db_returns_empty_list()
    {
        var svc = NewService();
        var users = await svc.GetPurchasersAsync();
        Assert.Empty(users);
    }

    [Fact]
    public async Task GetPurchasers_returns_all_users_ordered_by_fullname()
    {
        await Db.AddUserAsync("Charlie");
        await Db.AddUserAsync("Alice");
        await Db.AddUserAsync("Bob");

        var svc = NewService();
        var users = await svc.GetPurchasersAsync();

        Assert.Equal(3, users.Count);
        Assert.Equal(new[] { "Alice", "Bob", "Charlie" }, users.Select(u => u.FullName).ToArray());
    }

    [Fact]
    public async Task GetPurchasers_returns_every_user_not_just_those_who_purchased()
    {
        // user who never made a purchase is still returned (it's "all users")
        await Db.AddUserAsync("ไม่เคยซื้อ");

        var svc = NewService();
        var users = await svc.GetPurchasersAsync();

        Assert.Single(users);
        Assert.Equal("ไม่เคยซื้อ", users[0].FullName);
    }

    [Fact]
    public async Task GetPurchasers_blank_fullnames_sort_before_named_users()
    {
        await Db.AddUserAsync("Zeta");
        await Db.AddUserAsync("");

        var svc = NewService();
        var users = await svc.GetPurchasersAsync();

        Assert.Equal(2, users.Count);
        // empty string sorts first in OrderBy
        Assert.Equal("", users[0].FullName);
        Assert.Equal("Zeta", users[1].FullName);
    }
}
