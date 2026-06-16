using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using ElevenX.Warehouse.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ElevenX.Warehouse.Tests.Services;

/// <summary>
/// ครอบคลุม ItemService ทุก method และทุก branch: CRUD + Normalize + categories/suppliers
/// (DB-backed, รันบน PostgreSQL จริง — ILike, partial unique index, cascade ทำงานจริง)
/// </summary>
public class ItemServiceTests(PostgresFixture fixture) : DatabaseTestBase(fixture)
{
    private ItemService Svc(params string[] roles) => new(Db.Factory, Db.Accessor(roles));
    private ItemService AdminSvc() => new(Db.Factory, Db.Accessor(AppRoles.Admin));
    private ItemService PurchaserSvc() => new(Db.Factory, Db.Accessor(AppRoles.Purchaser));
    private ItemService AnonymousSvc() => new(Db.Factory, Db.Anonymous());

    private const string Forbidden = "คุณไม่มีสิทธิ์ดำเนินการนี้";

    // ============================================================
    // GetItemsAsync
    // ============================================================

    [Fact]
    public async Task GetItemsAsync_returns_all_items_with_category_included()
    {
        var cat = await Db.AddCategoryAsync("กล้อง");
        await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Name = "A");
        await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Name = "B");

        var items = await AdminSvc().GetItemsAsync();

        Assert.Equal(2, items.Count);
        // Include(i => i.Category) — Category navigation ต้องไม่เป็น null
        Assert.All(items, i => Assert.NotNull(i.Category));
        Assert.All(items, i => Assert.Equal("กล้อง", i.Category.Name));
    }

    [Fact]
    public async Task GetItemsAsync_filters_by_type()
    {
        await Db.AddIotItemAsync(configure: i => i.Name = "iot");
        await Db.AddSeatItemAsync(type: ItemType.Software, configure: i => i.Name = "sw");
        await Db.AddSeatItemAsync(type: ItemType.Server, configure: i => i.Name = "srv");

        var software = await AdminSvc().GetItemsAsync(type: ItemType.Software);

        Assert.Single(software);
        Assert.Equal("sw", software[0].Name);
    }

    [Fact]
    public async Task GetItemsAsync_filters_by_categoryId()
    {
        var c1 = await Db.AddCategoryAsync("c1");
        var c2 = await Db.AddCategoryAsync("c2");
        await Db.AddIotItemAsync(categoryId: c1.Id, configure: i => i.Name = "in-c1");
        await Db.AddIotItemAsync(categoryId: c2.Id, configure: i => i.Name = "in-c2");

        var inC1 = await AdminSvc().GetItemsAsync(categoryId: c1.Id);

        Assert.Single(inC1);
        Assert.Equal("in-c1", inC1[0].Name);
    }

    [Fact]
    public async Task GetItemsAsync_search_matches_name_substring()
    {
        await Db.AddIotItemAsync(configure: i => i.Name = "Temperature Sensor");
        await Db.AddIotItemAsync(configure: i => i.Name = "Humidity Probe");

        var found = await AdminSvc().GetItemsAsync(search: "Sensor");

        Assert.Single(found);
        Assert.Equal("Temperature Sensor", found[0].Name);
    }

    [Fact]
    public async Task GetItemsAsync_search_is_case_insensitive_on_name()
    {
        await Db.AddIotItemAsync(configure: i => i.Name = "Temperature Sensor");

        // EF.Functions.ILike — ค้นแบบ case-insensitive บน PostgreSQL จริง
        var lower = await AdminSvc().GetItemsAsync(search: "temperature");
        var upper = await AdminSvc().GetItemsAsync(search: "TEMPERATURE");

        Assert.Single(lower);
        Assert.Single(upper);
    }

    [Fact]
    public async Task GetItemsAsync_search_matches_sku_case_insensitively()
    {
        await Db.AddIotItemAsync(configure: i => { i.Name = "no-match-name"; i.Sku = "ABC-123"; });

        var byUpper = await AdminSvc().GetItemsAsync(search: "ABC-123");
        var byLower = await AdminSvc().GetItemsAsync(search: "abc-123");

        Assert.Single(byUpper);
        Assert.Single(byLower);
    }

    [Fact]
    public async Task GetItemsAsync_search_ignores_null_sku_and_does_not_throw()
    {
        // Sku == null — guard (i.Sku != null && ILike(...)) ต้องไม่พังและไม่ match
        await Db.AddIotItemAsync(configure: i => { i.Name = "plain"; i.Sku = null; });

        var found = await AdminSvc().GetItemsAsync(search: "zzz-not-present");

        Assert.Empty(found);
    }

    [Fact]
    public async Task GetItemsAsync_search_is_trimmed()
    {
        await Db.AddIotItemAsync(configure: i => i.Name = "Widget");

        var found = await AdminSvc().GetItemsAsync(search: "   Widget   ");

        Assert.Single(found);
    }

    [Fact]
    public async Task GetItemsAsync_whitespace_search_is_ignored_returns_all()
    {
        await Db.AddIotItemAsync(configure: i => i.Name = "x");
        await Db.AddIotItemAsync(configure: i => i.Name = "y");

        // string.IsNullOrWhiteSpace(search) → ข้าม filter
        var found = await AdminSvc().GetItemsAsync(search: "   ");

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task GetItemsAsync_orders_by_type_then_name()
    {
        // Type: IotMaterial=0, Server=1, Software=2, Other=3
        await Db.AddSeatItemAsync(type: ItemType.Software, configure: i => i.Name = "zsoft");
        await Db.AddSeatItemAsync(type: ItemType.Server, configure: i => i.Name = "bserver");
        await Db.AddSeatItemAsync(type: ItemType.Server, configure: i => i.Name = "aserver");
        await Db.AddIotItemAsync(configure: i => i.Name = "miot");

        var items = await AdminSvc().GetItemsAsync();

        var names = items.Select(i => i.Name).ToArray();
        // เรียง Type ก่อน (Iot, Server, Server, Software) แล้วชื่อภายใน type เดียวกัน
        Assert.Equal(new[] { "miot", "aserver", "bserver", "zsoft" }, names);
    }

    [Fact]
    public async Task GetItemsAsync_combined_filters_apply_together()
    {
        var cat = await Db.AddCategoryAsync("target");
        await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Name = "Match Me");
        await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Name = "Other");
        await Db.AddSeatItemAsync(type: ItemType.Software, categoryId: cat.Id, configure: i => i.Name = "Match Me");

        var found = await AdminSvc().GetItemsAsync(type: ItemType.IotMaterial, search: "match", categoryId: cat.Id);

        Assert.Single(found);
        Assert.Equal("Match Me", found[0].Name);
        Assert.Equal(ItemType.IotMaterial, found[0].Type);
    }

    [Fact]
    public async Task GetItemsAsync_empty_db_returns_empty_list()
    {
        var found = await AdminSvc().GetItemsAsync();
        Assert.Empty(found);
    }

    [Fact]
    public async Task GetItemsAsync_is_readable_without_manage_role()
    {
        await Db.AddIotItemAsync();
        // ไม่มี permission gate บน read — Staff/Anonymous อ่านได้
        var asStaff = await Svc(AppRoles.Staff).GetItemsAsync();
        var asAnon = await AnonymousSvc().GetItemsAsync();

        Assert.Single(asStaff);
        Assert.Single(asAnon);
    }

    // ============================================================
    // GetByIdAsync
    // ============================================================

    [Fact]
    public async Task GetByIdAsync_returns_item_with_category()
    {
        var cat = await Db.AddCategoryAsync("หมวดหา");
        var item = await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Name = "findme");

        var found = await AdminSvc().GetByIdAsync(item.Id);

        Assert.NotNull(found);
        Assert.Equal("findme", found!.Name);
        Assert.NotNull(found.Category);
        Assert.Equal("หมวดหา", found.Category.Name);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_when_missing()
    {
        var found = await AdminSvc().GetByIdAsync(999999);
        Assert.Null(found);
    }

    // ============================================================
    // GetLowStockAsync
    // ============================================================

    [Fact]
    public async Task GetLowStockAsync_includes_only_iot_at_or_below_min()
    {
        await Db.AddIotItemAsync(quantity: 5, minQuantity: 10, configure: i => i.Name = "below");
        await Db.AddIotItemAsync(quantity: 10, minQuantity: 10, configure: i => i.Name = "equal");
        await Db.AddIotItemAsync(quantity: 20, minQuantity: 10, configure: i => i.Name = "wellstocked");

        var low = await AdminSvc().GetLowStockAsync();

        Assert.Equal(2, low.Count);
        Assert.Contains(low, i => i.Name == "below");
        Assert.Contains(low, i => i.Name == "equal");      // Quantity <= MinQuantity คือ inclusive
        Assert.DoesNotContain(low, i => i.Name == "wellstocked");
    }

    [Fact]
    public async Task GetLowStockAsync_excludes_non_iot_even_when_quantity_low()
    {
        // Server/Software/Other ไม่ track stock — ต้องถูกกรองออกแม้ Quantity<=MinQuantity
        await Db.AddSeatItemAsync(type: ItemType.Server, configure: i => { i.Quantity = 0; i.MinQuantity = 10; i.Name = "srv"; });
        await Db.AddSeatItemAsync(type: ItemType.Software, configure: i => { i.Quantity = 0; i.MinQuantity = 10; i.Name = "sw"; });
        await Db.AddItemAsync(i => { i.Type = ItemType.Other; i.Quantity = 0; i.MinQuantity = 10; i.Name = "other"; });

        var low = await AdminSvc().GetLowStockAsync();

        Assert.Empty(low);
    }

    [Fact]
    public async Task GetLowStockAsync_orders_by_quantity_ascending()
    {
        await Db.AddIotItemAsync(quantity: 8, minQuantity: 10, configure: i => i.Name = "q8");
        await Db.AddIotItemAsync(quantity: 2, minQuantity: 10, configure: i => i.Name = "q2");
        await Db.AddIotItemAsync(quantity: 5, minQuantity: 10, configure: i => i.Name = "q5");

        var low = await AdminSvc().GetLowStockAsync();

        Assert.Equal(new[] { "q2", "q5", "q8" }, low.Select(i => i.Name).ToArray());
    }

    [Fact]
    public async Task GetLowStockAsync_includes_category_navigation()
    {
        var cat = await Db.AddCategoryAsync("lowcat");
        await Db.AddIotItemAsync(quantity: 0, minQuantity: 5, categoryId: cat.Id);

        var low = await AdminSvc().GetLowStockAsync();

        Assert.Single(low);
        Assert.NotNull(low[0].Category);
        Assert.Equal("lowcat", low[0].Category.Name);
    }

    [Fact]
    public async Task GetLowStockAsync_empty_when_all_well_stocked()
    {
        await Db.AddIotItemAsync(quantity: 100, minQuantity: 10);
        var low = await AdminSvc().GetLowStockAsync();
        Assert.Empty(low);
    }

    // ============================================================
    // CreateAsync
    // ============================================================

    [Fact]
    public async Task CreateAsync_forbidden_for_anonymous()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AnonymousSvc().CreateAsync(new Item { Name = "x", CategoryId = cat.Id });

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Items));
    }

    [Fact]
    public async Task CreateAsync_forbidden_for_staff()
    {
        var cat = await Db.AddCategoryAsync();
        // STAFF ไม่อยู่ใน CanManage (Admin/Purchaser เท่านั้น)
        var result = await Svc(AppRoles.Staff).CreateAsync(new Item { Name = "x", CategoryId = cat.Id });

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
    }

    [Fact]
    public async Task CreateAsync_forbidden_for_viewer()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await Svc(AppRoles.Viewer).CreateAsync(new Item { Name = "x", CategoryId = cat.Id });

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
    }

    [Fact]
    public async Task CreateAsync_allowed_for_purchaser()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await PurchaserSvc().CreateAsync(new Item
        {
            Name = "by purchaser",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
            CostType = CostType.OneTime,
        });

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task CreateAsync_success_sets_created_and_updated_timestamps()
    {
        var cat = await Db.AddCategoryAsync();
        var before = DateTime.UtcNow.AddSeconds(-2);

        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "T",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
            CostType = CostType.OneTime,
            // ตั้งค่า timestamp เก่าไว้เพื่อยืนยันว่า service เขียนทับด้วย UtcNow
            CreatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        Assert.True(result.Success, result.Error);
        var after = DateTime.UtcNow.AddSeconds(2);
        Assert.InRange(result.Value!.CreatedAt, before, after);
        Assert.InRange(result.Value!.UpdatedAt, before, after);
    }

    [Fact]
    public async Task CreateAsync_persists_and_returns_id()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "persisted",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
        });

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.Id > 0);
        Assert.Equal(1, await Db.CountAsync(c => c.Items));
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_sku()
    {
        var cat = await Db.AddCategoryAsync();
        await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Sku = "DUP-1");

        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "second",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
            Sku = "DUP-1",
        });

        Assert.False(result.Success);
        Assert.Equal("SKU \"DUP-1\" ถูกใช้ไปแล้ว", result.Error);
        Assert.Equal(1, await Db.CountAsync(c => c.Items));
    }

    [Fact]
    public async Task CreateAsync_allows_multiple_items_with_null_sku()
    {
        var cat = await Db.AddCategoryAsync();
        await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Sku = null);

        // SKU ว่าง/null ไม่ถูกตรวจซ้ำ — เพิ่มหลายตัวได้
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "another",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
            Sku = null,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, await Db.CountAsync(c => c.Items));
    }

    [Fact]
    public async Task CreateAsync_whitespace_sku_is_normalized_to_null_and_not_treated_as_duplicate()
    {
        var cat = await Db.AddCategoryAsync();

        var a = await AdminSvc().CreateAsync(new Item { Name = "a", CategoryId = cat.Id, Type = ItemType.IotMaterial, Sku = "   " });
        var b = await AdminSvc().CreateAsync(new Item { Name = "b", CategoryId = cat.Id, Type = ItemType.IotMaterial, Sku = "   " });

        Assert.True(a.Success, a.Error);
        Assert.True(b.Success, b.Error);
        Assert.Null(a.Value!.Sku);
        Assert.Null(b.Value!.Sku);
    }

    [Fact]
    public async Task CreateAsync_sku_duplicate_check_is_case_sensitive()
    {
        var cat = await Db.AddCategoryAsync();
        await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Sku = "abc");

        // AUDIT[medium]: การตรวจ SKU ซ้ำใช้ i.Sku == item.Sku (case-sensitive/exact) ขณะที่ค้นหาใช้ ILike
        // ทำให้ "abc" และ "ABC" ถือเป็นคนละ SKU และสร้างซ้ำกันได้ ผู้ใช้อาจเข้าใจว่า SKU ควร unique แบบ case-insensitive
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "upper",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
            Sku = "ABC",
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, await Db.CountAsync(c => c.Items));
    }

    [Fact]
    public async Task CreateAsync_padded_sku_bypasses_friendly_dup_check_then_throws_db_exception()
    {
        var cat = await Db.AddCategoryAsync();
        await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Sku = "PAD");

        // AUDIT[high]: การตรวจ SKU ซ้ำเกิดขึ้น "ก่อน" Normalize (ที่ trim) จึงเทียบกับค่าที่ยังมีช่องว่าง:
        // " PAD " != "PAD" → ลอดด่านตรวจซ้ำที่เป็นมิตร แล้ว Normalize trim เป็น "PAD" → ชน unique index ตอน SaveChanges
        // ผลลัพธ์จริง: โยน DbUpdateException ที่ไม่ถูก catch (กลายเป็น HTTP 500) แทนข้อความ "SKU ถูกใช้ไปแล้ว"
        await Assert.ThrowsAsync<DbUpdateException>(() => AdminSvc().CreateAsync(new Item
        {
            Name = "padded",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
            Sku = " PAD ",
        }));

        // ยืนยันว่าไม่มีแถวที่สองถูกบันทึก (transaction ล้มเหลว)
        Assert.Equal(1, await Db.CountAsync(c => c.Items.Where(i => i.Sku == "PAD")));
    }

    [Fact]
    public async Task CreateAsync_normalize_trims_name_and_sku()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "  Padded Name  ",
            Sku = "  SKU-9  ",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal("Padded Name", result.Value!.Name);
        Assert.Equal("SKU-9", result.Value!.Sku);
    }

    [Fact]
    public async Task CreateAsync_non_iot_clears_unit_minquantity_location()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "server",
            CategoryId = cat.Id,
            Type = ItemType.Server,
            CostType = CostType.OneTime,
            Unit = "ชิ้น",
            MinQuantity = 7,
            Location = "rack-1",
        });

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.Unit);
        Assert.Equal(0, result.Value!.MinQuantity);
        Assert.Null(result.Value!.Location);
    }

    [Fact]
    public async Task CreateAsync_iot_keeps_unit_minquantity_location()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "iot",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
            CostType = CostType.OneTime,
            Unit = "ม้วน",
            MinQuantity = 3,
            Location = "shelf-A",
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal("ม้วน", result.Value!.Unit);
        Assert.Equal(3, result.Value!.MinQuantity);
        Assert.Equal("shelf-A", result.Value!.Location);
    }

    [Theory]
    [InlineData(ItemType.Server)]
    [InlineData(ItemType.Software)]
    public async Task CreateAsync_server_or_software_keeps_total_seats(ItemType type)
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "seated",
            CategoryId = cat.Id,
            Type = type,
            CostType = CostType.OneTime,
            TotalSeats = 25,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(25, result.Value!.TotalSeats);
    }

    [Theory]
    [InlineData(ItemType.IotMaterial)]
    [InlineData(ItemType.Other)]
    public async Task CreateAsync_non_seat_types_clear_total_seats(ItemType type)
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "noseat",
            CategoryId = cat.Id,
            Type = type,
            CostType = CostType.OneTime,
            TotalSeats = 99,
        });

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.TotalSeats);
    }

    [Fact]
    public async Task CreateAsync_non_recurring_clears_all_subscription_fields()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "onetime",
            CategoryId = cat.Id,
            Type = ItemType.Software,
            CostType = CostType.OneTime,
            RecurringAmount = 500m,
            BillingCycle = BillingCycle.Monthly,
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddYears(1),
            NextBillingDate = DateTime.UtcNow,
            Status = SubscriptionStatus.Active,
        });

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.RecurringAmount);
        Assert.Null(result.Value!.BillingCycle);
        Assert.Null(result.Value!.StartDate);
        Assert.Null(result.Value!.EndDate);
        Assert.Null(result.Value!.NextBillingDate);
        Assert.Null(result.Value!.Status);
    }

    [Fact]
    public async Task CreateAsync_recurring_defaults_status_to_active_when_null()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "sub",
            CategoryId = cat.Id,
            Type = ItemType.Software,
            CostType = CostType.Recurring,
            RecurringAmount = 1000m,
            BillingCycle = BillingCycle.Monthly,
            Status = null,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(SubscriptionStatus.Active, result.Value!.Status);
    }

    [Fact]
    public async Task CreateAsync_recurring_active_null_nextbilling_defaults_to_startdate()
    {
        var cat = await Db.AddCategoryAsync();
        var start = new DateTime(2026, 3, 10, 8, 30, 0, DateTimeKind.Utc);
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "sub",
            CategoryId = cat.Id,
            Type = ItemType.Software,
            CostType = CostType.Recurring,
            RecurringAmount = 1000m,
            BillingCycle = BillingCycle.Monthly,
            Status = SubscriptionStatus.Active,
            StartDate = start,
            NextBillingDate = null,
        });

        Assert.True(result.Success, result.Error);
        // ใช้ StartDate.Date เป็น NextBillingDate
        Assert.Equal(start.Date, result.Value!.NextBillingDate);
    }

    [Fact]
    public async Task CreateAsync_recurring_active_null_nextbilling_and_null_start_defaults_to_today()
    {
        var cat = await Db.AddCategoryAsync();
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "sub",
            CategoryId = cat.Id,
            Type = ItemType.Software,
            CostType = CostType.Recurring,
            RecurringAmount = 1000m,
            BillingCycle = BillingCycle.Monthly,
            Status = SubscriptionStatus.Active,
            StartDate = null,
            NextBillingDate = null,
        });

        Assert.True(result.Success, result.Error);
        // (StartDate ?? UtcNow).Date → วันนี้
        Assert.Equal(DateTime.UtcNow.Date, result.Value!.NextBillingDate);
    }

    [Fact]
    public async Task CreateAsync_recurring_active_keeps_explicit_nextbilling()
    {
        var cat = await Db.AddCategoryAsync();
        var explicitNext = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "sub",
            CategoryId = cat.Id,
            Type = ItemType.Software,
            CostType = CostType.Recurring,
            RecurringAmount = 1000m,
            BillingCycle = BillingCycle.Yearly,
            Status = SubscriptionStatus.Active,
            NextBillingDate = explicitNext,
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(explicitNext, result.Value!.NextBillingDate);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Expired)]
    public async Task CreateAsync_recurring_non_active_does_not_get_default_nextbilling(SubscriptionStatus status)
    {
        var cat = await Db.AddCategoryAsync();
        // AUDIT[low]: เฉพาะ Status==Active เท่านั้นที่ได้ NextBillingDate อัตโนมัติ
        // subscription ที่ Cancelled/Expired แต่ผู้ใช้ลืมกรอก NextBillingDate จะมีค่าเป็น null
        var result = await AdminSvc().CreateAsync(new Item
        {
            Name = "sub",
            CategoryId = cat.Id,
            Type = ItemType.Software,
            CostType = CostType.Recurring,
            RecurringAmount = 1000m,
            BillingCycle = BillingCycle.Monthly,
            Status = status,
            NextBillingDate = null,
        });

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.NextBillingDate);
    }

    // ============================================================
    // UpdateAsync
    // ============================================================

    [Fact]
    public async Task UpdateAsync_forbidden_for_anonymous()
    {
        var item = await Db.AddIotItemAsync();
        var result = await AnonymousSvc().UpdateAsync(new Item { Id = item.Id, Name = "new", CategoryId = item.CategoryId });

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
    }

    [Fact]
    public async Task UpdateAsync_forbidden_for_staff()
    {
        var item = await Db.AddIotItemAsync();
        var result = await Svc(AppRoles.Staff).UpdateAsync(new Item { Id = item.Id, Name = "new", CategoryId = item.CategoryId });

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
    }

    [Fact]
    public async Task UpdateAsync_returns_not_found_for_missing_item()
    {
        var result = await AdminSvc().UpdateAsync(new Item { Id = 424242, Name = "ghost", CategoryId = 1 });

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการที่ต้องการแก้ไข", result.Error);
    }

    [Fact]
    public async Task UpdateAsync_copies_all_editable_fields()
    {
        var cat1 = await Db.AddCategoryAsync("c1");
        var cat2 = await Db.AddCategoryAsync("c2");
        var item = await Db.AddIotItemAsync(categoryId: cat1.Id);

        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = item.Id,
            Name = "renamed",
            Sku = "NEW-SKU",
            CategoryId = cat2.Id,
            Type = ItemType.IotMaterial,
            CostType = CostType.OneTime,
            Note = "a note",
            Unit = "เมตร",
            MinQuantity = 12,
            Location = "loc-9",
        });

        Assert.True(result.Success, result.Error);

        await using var ctx = Db.NewContext();
        var saved = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal("NEW-SKU", saved.Sku);
        Assert.Equal(cat2.Id, saved.CategoryId);
        Assert.Equal("a note", saved.Note);
        Assert.Equal("เมตร", saved.Unit);
        Assert.Equal(12, saved.MinQuantity);
        Assert.Equal("loc-9", saved.Location);
    }

    [Fact]
    public async Task UpdateAsync_does_not_change_quantity()
    {
        var item = await Db.AddIotItemAsync(quantity: 50, minQuantity: 5);

        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = item.Id,
            Name = "renamed",
            CategoryId = item.CategoryId,
            Type = ItemType.IotMaterial,
            // พยายามเปลี่ยน Quantity เป็น 0 — ต้องถูกเพิกเฉย (สต็อกเปลี่ยนผ่าน Purchase/Withdrawal เท่านั้น)
            Quantity = 0,
        });

        Assert.True(result.Success, result.Error);

        await using var ctx = Db.NewContext();
        var saved = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(50, saved.Quantity);
    }

    [Fact]
    public async Task UpdateAsync_updates_updatedat_timestamp()
    {
        var item = await Db.AddItemAsync(i =>
        {
            i.UpdatedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        });
        var before = DateTime.UtcNow.AddSeconds(-2);

        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = item.Id,
            Name = "x",
            CategoryId = item.CategoryId,
            Type = ItemType.IotMaterial,
        });

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var saved = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.InRange(saved.UpdatedAt, before, DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public async Task UpdateAsync_same_sku_on_same_item_is_allowed()
    {
        var item = await Db.AddIotItemAsync(configure: i => i.Sku = "SAME-1");

        // SKU ซ้ำไม่ถูกบล็อกถ้าเป็น item เดียวกัน (i.Id != item.Id excludes self)
        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = item.Id,
            Name = "renamed but same sku",
            CategoryId = item.CategoryId,
            Type = ItemType.IotMaterial,
            Sku = "SAME-1",
        });

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task UpdateAsync_rejects_sku_used_by_another_item()
    {
        var cat = await Db.AddCategoryAsync();
        await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Sku = "TAKEN");
        var target = await Db.AddIotItemAsync(categoryId: cat.Id, configure: i => i.Sku = "MINE");

        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = target.Id,
            Name = "x",
            CategoryId = cat.Id,
            Type = ItemType.IotMaterial,
            Sku = "TAKEN",
        });

        Assert.False(result.Success);
        Assert.Equal("SKU \"TAKEN\" ถูกใช้ไปแล้ว", result.Error);
    }

    [Fact]
    public async Task UpdateAsync_allows_setting_sku_to_null()
    {
        var item = await Db.AddIotItemAsync(configure: i => i.Sku = "HAD-ONE");
        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = item.Id,
            Name = "x",
            CategoryId = item.CategoryId,
            Type = ItemType.IotMaterial,
            Sku = null,
        });

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var saved = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Null(saved.Sku);
    }

    [Fact]
    public async Task UpdateAsync_applies_normalize_clearing_iot_fields_when_switching_to_server()
    {
        var item = await Db.AddIotItemAsync(quantity: 30, minQuantity: 5, configure: i =>
        {
            i.Unit = "ชิ้น";
            i.Location = "L1";
        });

        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = item.Id,
            Name = "now a server",
            CategoryId = item.CategoryId,
            Type = ItemType.Server,
            CostType = CostType.OneTime,
            Unit = "ชิ้น",
            MinQuantity = 5,
            Location = "L1",
            TotalSeats = 8,
        });

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var saved = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Null(saved.Unit);
        Assert.Equal(0, saved.MinQuantity);
        Assert.Null(saved.Location);
        Assert.Equal(8, saved.TotalSeats);
        // Quantity ไม่เปลี่ยนแม้เปลี่ยน type
        Assert.Equal(30, saved.Quantity);
    }

    [Fact]
    public async Task UpdateAsync_applies_normalize_clearing_subscription_fields_when_switching_to_onetime()
    {
        var item = await Db.AddSubscriptionAsync(
            type: ItemType.Software,
            amount: 999m,
            cycle: BillingCycle.Yearly,
            status: SubscriptionStatus.Active);

        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = item.Id,
            Name = "now onetime",
            CategoryId = item.CategoryId,
            Type = ItemType.Software,
            CostType = CostType.OneTime,
            RecurringAmount = 999m,
            BillingCycle = BillingCycle.Yearly,
            Status = SubscriptionStatus.Active,
            NextBillingDate = DateTime.UtcNow,
        });

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var saved = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Null(saved.RecurringAmount);
        Assert.Null(saved.BillingCycle);
        Assert.Null(saved.StartDate);
        Assert.Null(saved.EndDate);
        Assert.Null(saved.NextBillingDate);
        Assert.Null(saved.Status);
    }

    [Fact]
    public async Task UpdateAsync_normalize_trims_name()
    {
        var item = await Db.AddIotItemAsync();
        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = item.Id,
            Name = "   spaced   ",
            CategoryId = item.CategoryId,
            Type = ItemType.IotMaterial,
        });

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var saved = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal("spaced", saved.Name);
    }

    [Fact]
    public async Task UpdateAsync_recurring_active_null_nextbilling_gets_default()
    {
        var item = await Db.AddIotItemAsync();
        var result = await AdminSvc().UpdateAsync(new Item
        {
            Id = item.Id,
            Name = "becomes sub",
            CategoryId = item.CategoryId,
            Type = ItemType.Software,
            CostType = CostType.Recurring,
            RecurringAmount = 500m,
            BillingCycle = BillingCycle.Monthly,
            Status = SubscriptionStatus.Active,
            NextBillingDate = null,
        });

        Assert.True(result.Success, result.Error);
        await using var ctx = Db.NewContext();
        var saved = await ctx.Items.FirstAsync(i => i.Id == item.Id);
        Assert.Equal(DateTime.UtcNow.Date, saved.NextBillingDate);
    }

    // ============================================================
    // DeleteAsync
    // ============================================================

    [Fact]
    public async Task DeleteAsync_forbidden_for_anonymous()
    {
        var item = await Db.AddIotItemAsync();
        var result = await AnonymousSvc().DeleteAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
        Assert.Equal(1, await Db.CountAsync(c => c.Items));
    }

    [Fact]
    public async Task DeleteAsync_forbidden_for_staff()
    {
        var item = await Db.AddIotItemAsync();
        var result = await Svc(AppRoles.Staff).DeleteAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
    }

    [Fact]
    public async Task DeleteAsync_returns_not_found_for_missing_item()
    {
        var result = await AdminSvc().DeleteAsync(777777);

        Assert.False(result.Success);
        Assert.Equal("ไม่พบรายการที่ต้องการลบ", result.Error);
    }

    [Fact]
    public async Task DeleteAsync_succeeds_when_no_history()
    {
        var item = await Db.AddIotItemAsync();
        var result = await AdminSvc().DeleteAsync(item.Id);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Items));
    }

    [Fact]
    public async Task DeleteAsync_blocked_when_item_has_purchase_history()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddPurchaseAsync(item.Id, user.Id);

        var result = await AdminSvc().DeleteAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("ลบไม่ได้ เพราะรายการนี้มีประวัติการซื้อ/เบิก/License อยู่ในระบบ", result.Error);
        Assert.Equal(1, await Db.CountAsync(c => c.Items));
    }

    [Fact]
    public async Task DeleteAsync_blocked_when_item_has_withdrawal_history()
    {
        var user = await Db.AddUserAsync();
        var item = await Db.AddIotItemAsync();
        await Db.AddWithdrawalAsync(item.Id, user.Id);

        var result = await AdminSvc().DeleteAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("ลบไม่ได้ เพราะรายการนี้มีประวัติการซื้อ/เบิก/License อยู่ในระบบ", result.Error);
    }

    [Fact]
    public async Task DeleteAsync_blocked_when_item_has_license_history()
    {
        var assignedTo = await Db.AddUserAsync("ผู้รับ");
        var assignedBy = await Db.AddUserAsync("ผู้มอบ");
        var item = await Db.AddSeatItemAsync(type: ItemType.Software, totalSeats: 5);
        await Db.AddLicenseAsync(item.Id, assignedTo.Id, assignedBy.Id);

        var result = await AdminSvc().DeleteAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("ลบไม่ได้ เพราะรายการนี้มีประวัติการซื้อ/เบิก/License อยู่ในระบบ", result.Error);
    }

    [Fact]
    public async Task DeleteAsync_blocked_even_when_license_already_released()
    {
        var assignedTo = await Db.AddUserAsync("ผู้รับ");
        var assignedBy = await Db.AddUserAsync("ผู้มอบ");
        var item = await Db.AddSeatItemAsync(type: ItemType.Software, totalSeats: 5);
        // license ที่คืนแล้ว (ReleasedAt != null) ก็ยังนับเป็นประวัติ — ตรวจแค่ ItemId
        await Db.AddLicenseAsync(item.Id, assignedTo.Id, assignedBy.Id, releasedAt: DateTime.UtcNow);

        var result = await AdminSvc().DeleteAsync(item.Id);

        Assert.False(result.Success);
        Assert.Equal("ลบไม่ได้ เพราะรายการนี้มีประวัติการซื้อ/เบิก/License อยู่ในระบบ", result.Error);
    }

    [Fact]
    public async Task DeleteAsync_allowed_for_purchaser_without_history()
    {
        var item = await Db.AddIotItemAsync();
        var result = await PurchaserSvc().DeleteAsync(item.Id);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Items));
    }

    // ============================================================
    // GetCategoriesAsync / GetSuppliersAsync
    // ============================================================

    [Fact]
    public async Task GetCategoriesAsync_orders_by_name()
    {
        await Db.AddCategoryAsync("Zebra");
        await Db.AddCategoryAsync("Apple");
        await Db.AddCategoryAsync("Mango");

        var cats = await AdminSvc().GetCategoriesAsync();

        Assert.Equal(new[] { "Apple", "Mango", "Zebra" }, cats.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task GetCategoriesAsync_empty_when_none()
    {
        var cats = await AdminSvc().GetCategoriesAsync();
        Assert.Empty(cats);
    }

    [Fact]
    public async Task GetSuppliersAsync_orders_by_name()
    {
        await Db.AddSupplierAsync("Omega");
        await Db.AddSupplierAsync("Alpha");
        await Db.AddSupplierAsync("Delta");

        var sups = await AdminSvc().GetSuppliersAsync();

        Assert.Equal(new[] { "Alpha", "Delta", "Omega" }, sups.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task GetSuppliersAsync_empty_when_none()
    {
        var sups = await AdminSvc().GetSuppliersAsync();
        Assert.Empty(sups);
    }

    // ============================================================
    // CreateCategoryAsync
    // ============================================================

    [Fact]
    public async Task CreateCategoryAsync_forbidden_for_anonymous()
    {
        var result = await AnonymousSvc().CreateCategoryAsync("ใหม่");

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Categories));
    }

    [Fact]
    public async Task CreateCategoryAsync_forbidden_for_staff()
    {
        var result = await Svc(AppRoles.Staff).CreateCategoryAsync("ใหม่");

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
    }

    [Fact]
    public async Task CreateCategoryAsync_trims_name()
    {
        var result = await AdminSvc().CreateCategoryAsync("   เซ็นเซอร์   ");

        Assert.True(result.Success, result.Error);
        Assert.Equal("เซ็นเซอร์", result.Value!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task CreateCategoryAsync_rejects_empty_or_whitespace_name(string name)
    {
        var result = await AdminSvc().CreateCategoryAsync(name);

        Assert.False(result.Success);
        Assert.Equal("กรุณาระบุชื่อหมวดหมู่", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Categories));
    }

    [Fact]
    public async Task CreateCategoryAsync_rejects_duplicate_name()
    {
        await Db.AddCategoryAsync("ซ้ำ");
        var result = await AdminSvc().CreateCategoryAsync("ซ้ำ");

        Assert.False(result.Success);
        Assert.Equal("มีหมวดหมู่นี้อยู่แล้ว", result.Error);
        Assert.Equal(1, await Db.CountAsync(c => c.Categories));
    }

    [Fact]
    public async Task CreateCategoryAsync_duplicate_check_is_after_trim()
    {
        await Db.AddCategoryAsync("trimcat");
        // ค่า input มีช่องว่าง ถูก trim ก่อนเทียบซ้ำ → ตรวจเจอว่าซ้ำ
        var result = await AdminSvc().CreateCategoryAsync("  trimcat  ");

        Assert.False(result.Success);
        Assert.Equal("มีหมวดหมู่นี้อยู่แล้ว", result.Error);
    }

    [Fact]
    public async Task CreateCategoryAsync_duplicate_check_is_case_sensitive()
    {
        await Db.AddCategoryAsync("camera");
        // AUDIT[low]: การตรวจชื่อหมวดซ้ำใช้ c.Name == name (case-sensitive) ทำให้ "camera" และ "Camera"
        // อยู่ร่วมกันได้ ต่างจากความคาดหวังทั่วไปว่าชื่อหมวดควร unique แบบไม่สนตัวพิมพ์
        var result = await AdminSvc().CreateCategoryAsync("Camera");

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, await Db.CountAsync(c => c.Categories));
    }

    [Fact]
    public async Task CreateCategoryAsync_success_persists()
    {
        var result = await AdminSvc().CreateCategoryAsync("ใหม่ล่าสุด");

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.Id > 0);
        Assert.Equal(1, await Db.CountAsync(c => c.Categories));
    }

    [Fact]
    public async Task CreateCategoryAsync_throws_on_null_name()
    {
        // AUDIT[medium]: CreateCategoryAsync เรียก name.Trim() ก่อนตรวจ null/whitespace
        // ถ้า caller ส่ง null จะเกิด NullReferenceException แทนที่จะคืน OperationResult.Fail อย่างสุภาพ
        await Assert.ThrowsAsync<NullReferenceException>(() => AdminSvc().CreateCategoryAsync(null!));
    }

    // ============================================================
    // CreateSupplierAsync
    // ============================================================

    [Fact]
    public async Task CreateSupplierAsync_forbidden_for_anonymous()
    {
        var result = await AnonymousSvc().CreateSupplierAsync("ผู้ขาย", null);

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Suppliers));
    }

    [Fact]
    public async Task CreateSupplierAsync_forbidden_for_staff()
    {
        var result = await Svc(AppRoles.Staff).CreateSupplierAsync("ผู้ขาย", null);

        Assert.False(result.Success);
        Assert.Equal(Forbidden, result.Error);
    }

    [Fact]
    public async Task CreateSupplierAsync_trims_name_and_contact()
    {
        var result = await AdminSvc().CreateSupplierAsync("  ACME Co  ", "  02-123-4567  ");

        Assert.True(result.Success, result.Error);
        Assert.Equal("ACME Co", result.Value!.Name);
        Assert.Equal("02-123-4567", result.Value!.Contact);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateSupplierAsync_rejects_empty_name(string name)
    {
        var result = await AdminSvc().CreateSupplierAsync(name, "contact");

        Assert.False(result.Success);
        Assert.Equal("กรุณาระบุชื่อผู้ขาย", result.Error);
        Assert.Equal(0, await Db.CountAsync(c => c.Suppliers));
    }

    [Fact]
    public async Task CreateSupplierAsync_allows_null_contact()
    {
        var result = await AdminSvc().CreateSupplierAsync("NoContact Co", null);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.Contact);
    }

    [Fact]
    public async Task CreateSupplierAsync_whitespace_contact_becomes_empty_string_not_null()
    {
        // AUDIT[low]: contact?.Trim() บนค่า "   " ให้ "" (string ว่าง) ไม่ใช่ null
        // ทำให้เกิดความไม่สอดคล้องกับ contact ที่เป็น null จริง ๆ (อาจอยากแปลง whitespace เป็น null ด้วย)
        var result = await AdminSvc().CreateSupplierAsync("WS Co", "   ");

        Assert.True(result.Success, result.Error);
        Assert.Equal("", result.Value!.Contact);
    }

    [Fact]
    public async Task CreateSupplierAsync_allows_duplicate_names()
    {
        await Db.AddSupplierAsync("DupSup");
        // ไม่มีการตรวจชื่อ supplier ซ้ำ (ต่างจาก category)
        var result = await AdminSvc().CreateSupplierAsync("DupSup", null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, await Db.CountAsync(c => c.Suppliers));
    }

    [Fact]
    public async Task CreateSupplierAsync_throws_on_null_name()
    {
        // AUDIT[medium]: CreateSupplierAsync เรียก name.Trim() ก่อนตรวจ — null name ทำให้ NullReferenceException
        await Assert.ThrowsAsync<NullReferenceException>(() => AdminSvc().CreateSupplierAsync(null!, null));
    }

    [Fact]
    public async Task CreateSupplierAsync_success_persists()
    {
        var result = await AdminSvc().CreateSupplierAsync("Persisted Co", "x@y.z");

        Assert.True(result.Success, result.Error);
        Assert.True(result.Value!.Id > 0);
        Assert.Equal(1, await Db.CountAsync(c => c.Suppliers));
    }
}
