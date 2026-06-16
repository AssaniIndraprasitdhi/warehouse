using ElevenX.Warehouse.Data;
using ElevenX.Warehouse.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ElevenX.Warehouse.Tests.Infrastructure;

/// <summary>
/// ฐานข้อมูลอิสระสำหรับ test หนึ่งตัว: สร้าง DB ใหม่บน container, สร้าง schema (EnsureCreated),
/// และมี service provider ของ Identity ผูกกับ DB นั้น พร้อม helper สำหรับ arrange ข้อมูล
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _dbName;
    private readonly string _adminConn;
    private readonly ServiceProvider _root;
    private readonly IServiceScope _scope;

    public IDbContextFactory<ApplicationDbContext> Factory { get; }
    public UserManager<ApplicationUser> UserManager { get; }
    public RoleManager<IdentityRole> RoleManager { get; }

    private TestDatabase(string dbName, string adminConn, ServiceProvider root, IServiceScope scope,
        IDbContextFactory<ApplicationDbContext> factory, UserManager<ApplicationUser> um, RoleManager<IdentityRole> rm)
    {
        _dbName = dbName;
        _adminConn = adminConn;
        _root = root;
        _scope = scope;
        Factory = factory;
        UserManager = um;
        RoleManager = rm;
    }

    public static async Task<TestDatabase> CreateAsync(string adminConn)
    {
        var dbName = "t_" + Guid.NewGuid().ToString("N");

        await using (var admin = new NpgsqlConnection(adminConn))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var conn = new NpgsqlConnectionStringBuilder(adminConn) { Database = dbName, Pooling = false }.ConnectionString;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();   // จำเป็นสำหรับ AddDefaultTokenProviders (เช่น reset-password token)
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(conn));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        services.AddIdentityCore<ApplicationUser>(o => o.SignIn.RequireConfirmedAccount = false)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        var root = services.BuildServiceProvider();
        var factory = root.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var scope = root.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var rm = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        var db = new TestDatabase(dbName, adminConn, root, scope, factory, um, rm);
        await db.EnsureRolesAsync();
        return db;
    }

    public ApplicationDbContext NewContext() => Factory.CreateDbContext();

    /// <summary>สร้าง CurrentUserAccessor ที่มี role ตามที่กำหนด (userId = "test-user")</summary>
    public CurrentUserAccessor Accessor(params string[] roles) => AccessorFor("test-user", roles);

    /// <summary>สร้าง CurrentUserAccessor ที่ระบุทั้ง userId และ role (จำลองผู้ใช้ที่ login อยู่)</summary>
    public CurrentUserAccessor AccessorFor(string? userId, params string[] roles)
        => new(new TestAuthStateProvider(userId, roles), UserManager);

    /// <summary>accessor ของผู้ใช้ที่ไม่ได้ login (ไม่มี role) — ใช้ทดสอบ Forbidden</summary>
    public CurrentUserAccessor Anonymous() => new(new TestAuthStateProvider(null), UserManager);

    // ===================== seed / arrange helpers =====================

    public async Task EnsureRolesAsync()
    {
        foreach (var role in AppRoles.All)
            if (!await RoleManager.RoleExistsAsync(role))
                await RoleManager.CreateAsync(new IdentityRole(role));
    }

    /// <summary>เพิ่ม ApplicationUser ตรง ๆ ผ่าน DbContext (เร็ว — ใช้เป็นเป้า FK ของ purchase/withdrawal/license)</summary>
    public async Task<ApplicationUser> AddUserAsync(string fullName = "ผู้ทดสอบ", string? email = null, string? id = null)
    {
        var user = new ApplicationUser
        {
            Id = id ?? Guid.NewGuid().ToString(),
            FullName = fullName,
            Email = email ?? $"{Guid.NewGuid():N}@test.local",
            UserName = email ?? $"{Guid.NewGuid():N}@test.local",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        await using var ctx = NewContext();
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    public async Task<Category> AddCategoryAsync(string name = "หมวดทดสอบ")
    {
        var cat = new Category { Name = name };
        await using var ctx = NewContext();
        ctx.Categories.Add(cat);
        await ctx.SaveChangesAsync();
        return cat;
    }

    public async Task<Supplier> AddSupplierAsync(string name = "ผู้ขายทดสอบ", string? contact = null)
    {
        var sup = new Supplier { Name = name, Contact = contact };
        await using var ctx = NewContext();
        ctx.Suppliers.Add(sup);
        await ctx.SaveChangesAsync();
        return sup;
    }

    /// <summary>เพิ่ม Item พร้อม category (สร้างให้ถ้าไม่ส่งมา) — ปรับแต่งได้ผ่าน configure</summary>
    public async Task<Item> AddItemAsync(Action<Item>? configure = null, int? categoryId = null)
    {
        categoryId ??= (await AddCategoryAsync($"หมวด-{Guid.NewGuid():N}")).Id;
        var item = new Item
        {
            Name = "รายการทดสอบ",
            CategoryId = categoryId.Value,
            Type = ItemType.IotMaterial,
            CostType = CostType.OneTime,
            Quantity = 0,
            MinQuantity = 0,
            Unit = "ชิ้น",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        configure?.Invoke(item);

        await using var ctx = NewContext();
        ctx.Items.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    /// <summary>เพิ่ม IoT material พร้อมสต็อก</summary>
    public Task<Item> AddIotItemAsync(int quantity = 100, int minQuantity = 10, int? categoryId = null, Action<Item>? configure = null)
        => AddItemAsync(i =>
        {
            i.Type = ItemType.IotMaterial;
            i.CostType = CostType.OneTime;
            i.Quantity = quantity;
            i.MinQuantity = minQuantity;
            configure?.Invoke(i);
        }, categoryId);

    /// <summary>เพิ่ม Server/Software ที่มี seat</summary>
    public Task<Item> AddSeatItemAsync(ItemType type = ItemType.Software, int totalSeats = 10, int? categoryId = null, Action<Item>? configure = null)
        => AddItemAsync(i =>
        {
            i.Type = type;
            i.CostType = CostType.OneTime;
            i.TotalSeats = totalSeats;
            configure?.Invoke(i);
        }, categoryId);

    /// <summary>เพิ่ม subscription (Recurring)</summary>
    public Task<Item> AddSubscriptionAsync(
        ItemType type = ItemType.Software,
        decimal amount = 1000m,
        BillingCycle cycle = BillingCycle.Monthly,
        SubscriptionStatus status = SubscriptionStatus.Active,
        DateTime? nextBillingDate = null,
        int? categoryId = null,
        Action<Item>? configure = null)
        => AddItemAsync(i =>
        {
            i.Type = type;
            i.CostType = CostType.Recurring;
            i.RecurringAmount = amount;
            i.BillingCycle = cycle;
            i.Status = status;
            i.NextBillingDate = nextBillingDate ?? DateTime.UtcNow.Date;
            configure?.Invoke(i);
        }, categoryId);

    public async Task<Purchase> AddPurchaseAsync(int itemId, string purchasedById, Action<Purchase>? configure = null)
    {
        var p = new Purchase
        {
            ItemId = itemId,
            PurchasedById = purchasedById,
            IsRecurringCharge = false,
            Quantity = 1,
            UnitPrice = 100m,
            TotalCost = 100m,
            Date = DateTime.UtcNow,
        };
        configure?.Invoke(p);
        await using var ctx = NewContext();
        ctx.Purchases.Add(p);
        await ctx.SaveChangesAsync();
        return p;
    }

    public async Task<Withdrawal> AddWithdrawalAsync(int itemId, string withdrawnById, Action<Withdrawal>? configure = null)
    {
        var w = new Withdrawal
        {
            ItemId = itemId,
            WithdrawnById = withdrawnById,
            Quantity = 1,
            WithdrawnAt = DateTime.UtcNow,
        };
        configure?.Invoke(w);
        await using var ctx = NewContext();
        ctx.Withdrawals.Add(w);
        await ctx.SaveChangesAsync();
        return w;
    }

    public async Task<LicenseAssignment> AddLicenseAsync(int itemId, string assignedToId, string assignedById, DateTime? releasedAt = null, Action<LicenseAssignment>? configure = null)
    {
        var l = new LicenseAssignment
        {
            ItemId = itemId,
            AssignedToId = assignedToId,
            AssignedById = assignedById,
            AssignedAt = DateTime.UtcNow,
            ReleasedAt = releasedAt,
        };
        configure?.Invoke(l);
        await using var ctx = NewContext();
        ctx.LicenseAssignments.Add(l);
        await ctx.SaveChangesAsync();
        return l;
    }

    /// <summary>นับจำนวนแถวของ entity (utility สำหรับ assert)</summary>
    public async Task<int> CountAsync<T>(Func<ApplicationDbContext, IQueryable<T>> set) where T : class
    {
        await using var ctx = NewContext();
        return await set(ctx).CountAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _root.DisposeAsync();
        try
        {
            await using var admin = new NpgsqlConnection(_adminConn);
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // best-effort cleanup — container ถูกทิ้งเมื่อจบ run อยู่แล้ว
        }
    }
}
