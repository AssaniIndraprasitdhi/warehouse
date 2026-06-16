using ElevenX.Warehouse.Data;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Services;

public interface IItemService
{
    Task<List<Item>> GetItemsAsync(ItemType? type = null, string? search = null, int? categoryId = null);
    Task<Item?> GetByIdAsync(int id);
    Task<List<Item>> GetLowStockAsync();
    Task<OperationResult<Item>> CreateAsync(Item item);
    Task<OperationResult> UpdateAsync(Item item);
    Task<OperationResult> DeleteAsync(int id);
    Task<List<Category>> GetCategoriesAsync();
    Task<List<Supplier>> GetSuppliersAsync();
    Task<OperationResult<Category>> CreateCategoryAsync(string name);
    Task<OperationResult<Supplier>> CreateSupplierAsync(string name, string? contact);
}

public class ItemService(IDbContextFactory<ApplicationDbContext> dbFactory, CurrentUserAccessor currentUser) : IItemService
{
    // บังคับสิทธิ์ฝั่ง server: เฉพาะ ADMIN/PURCHASER จัดการ item ได้
    private async Task<bool> CanManageAsync() => await currentUser.IsInAnyRoleAsync(AppRoles.Admin, AppRoles.Purchaser);
    private const string Forbidden = "คุณไม่มีสิทธิ์ดำเนินการนี้";

    public async Task<List<Item>> GetItemsAsync(ItemType? type = null, string? search = null, int? categoryId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Items.Include(i => i.Category).AsQueryable();

        if (type is not null)
            q = q.Where(i => i.Type == type);
        if (categoryId is not null)
            q = q.Where(i => i.CategoryId == categoryId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            q = q.Where(i => EF.Functions.ILike(i.Name, pattern)
                          || (i.Sku != null && EF.Functions.ILike(i.Sku, pattern)));
        }

        return await q.OrderBy(i => i.Type).ThenBy(i => i.Name).ToListAsync();
    }

    public async Task<Item?> GetByIdAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Items.Include(i => i.Category).FirstOrDefaultAsync(i => i.Id == id);
    }

    public async Task<List<Item>> GetLowStockAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Items
            .Include(i => i.Category)
            .Where(i => i.Type == ItemType.IotMaterial && i.Quantity <= i.MinQuantity)
            .OrderBy(i => i.Quantity)
            .ToListAsync();
    }

    public async Task<OperationResult<Item>> CreateAsync(Item item)
    {
        if (!await CanManageAsync()) return OperationResult<Item>.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();

        if (!string.IsNullOrWhiteSpace(item.Sku) &&
            await db.Items.AnyAsync(i => i.Sku == item.Sku))
            return OperationResult<Item>.Fail($"SKU \"{item.Sku}\" ถูกใช้ไปแล้ว");

        Normalize(item);
        item.CreatedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;

        db.Items.Add(item);
        await db.SaveChangesAsync();
        return OperationResult<Item>.Ok(item);
    }

    public async Task<OperationResult> UpdateAsync(Item item)
    {
        if (!await CanManageAsync()) return OperationResult.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();

        var existing = await db.Items.FirstOrDefaultAsync(i => i.Id == item.Id);
        if (existing is null)
            return OperationResult.Fail("ไม่พบรายการที่ต้องการแก้ไข");

        if (!string.IsNullOrWhiteSpace(item.Sku) &&
            await db.Items.AnyAsync(i => i.Sku == item.Sku && i.Id != item.Id))
            return OperationResult.Fail($"SKU \"{item.Sku}\" ถูกใช้ไปแล้ว");

        Normalize(item);

        existing.Name = item.Name;
        existing.Sku = item.Sku;
        existing.CategoryId = item.CategoryId;
        existing.Type = item.Type;
        existing.CostType = item.CostType;
        existing.Note = item.Note;
        existing.Unit = item.Unit;
        existing.MinQuantity = item.MinQuantity;
        existing.Location = item.Location;
        existing.TotalSeats = item.TotalSeats;
        existing.RecurringAmount = item.RecurringAmount;
        existing.BillingCycle = item.BillingCycle;
        existing.StartDate = item.StartDate;
        existing.EndDate = item.EndDate;
        existing.NextBillingDate = item.NextBillingDate;
        existing.Status = item.Status;
        existing.UpdatedAt = DateTime.UtcNow;
        // หมายเหตุ: ไม่แก้ Quantity ผ่านหน้านี้ — สต็อกเปลี่ยนผ่าน Purchase/Withdrawal เท่านั้น

        await db.SaveChangesAsync();
        return OperationResult.Ok();
    }

    public async Task<OperationResult> DeleteAsync(int id)
    {
        if (!await CanManageAsync()) return OperationResult.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null)
            return OperationResult.Fail("ไม่พบรายการที่ต้องการลบ");

        // กันลบรายการที่มีประวัติ เพื่อรักษาความถูกต้องของข้อมูลการเงิน/การเบิก/License
        var hasHistory = await db.Purchases.AnyAsync(p => p.ItemId == id)
                      || await db.Withdrawals.AnyAsync(w => w.ItemId == id)
                      || await db.LicenseAssignments.AnyAsync(l => l.ItemId == id);
        if (hasHistory)
            return OperationResult.Fail("ลบไม่ได้ เพราะรายการนี้มีประวัติการซื้อ/เบิก/License อยู่ในระบบ");

        db.Items.Remove(item);
        await db.SaveChangesAsync();
        return OperationResult.Ok();
    }

    public async Task<List<Category>> GetCategoriesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Categories.OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<List<Supplier>> GetSuppliersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Suppliers.OrderBy(s => s.Name).ToListAsync();
    }

    public async Task<OperationResult<Category>> CreateCategoryAsync(string name)
    {
        if (!await CanManageAsync()) return OperationResult<Category>.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return OperationResult<Category>.Fail("กรุณาระบุชื่อหมวดหมู่");
        if (await db.Categories.AnyAsync(c => c.Name == name))
            return OperationResult<Category>.Fail("มีหมวดหมู่นี้อยู่แล้ว");

        var cat = new Category { Name = name };
        db.Categories.Add(cat);
        await db.SaveChangesAsync();
        return OperationResult<Category>.Ok(cat);
    }

    public async Task<OperationResult<Supplier>> CreateSupplierAsync(string name, string? contact)
    {
        if (!await CanManageAsync()) return OperationResult<Supplier>.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return OperationResult<Supplier>.Fail("กรุณาระบุชื่อผู้ขาย");

        var sup = new Supplier { Name = name, Contact = contact?.Trim() };
        db.Suppliers.Add(sup);
        await db.SaveChangesAsync();
        return OperationResult<Supplier>.Ok(sup);
    }

    /// <summary>ล้างฟิลด์ที่ไม่เกี่ยวกับ Type/CostType ที่เลือก เพื่อไม่ให้มีข้อมูลค้าง</summary>
    private static void Normalize(Item item)
    {
        item.Name = item.Name.Trim();
        item.Sku = string.IsNullOrWhiteSpace(item.Sku) ? null : item.Sku.Trim();

        if (item.Type != ItemType.IotMaterial)
        {
            item.Unit = null;
            item.MinQuantity = 0;
            item.Location = null;
        }

        if (item.Type is not (ItemType.Server or ItemType.Software))
        {
            item.TotalSeats = null;
        }

        if (item.CostType != CostType.Recurring)
        {
            item.RecurringAmount = null;
            item.BillingCycle = null;
            item.StartDate = null;
            item.EndDate = null;
            item.NextBillingDate = null;
            item.Status = null;
        }
        else
        {
            item.Status ??= SubscriptionStatus.Active;
            // subscription ที่ active ต้องมีรอบบิลถัดไปเสมอ เพื่อให้บันทึกค่ารอบได้ถูกต้อง
            if (item.Status == SubscriptionStatus.Active && item.NextBillingDate is null)
                item.NextBillingDate = (item.StartDate ?? DateTime.UtcNow).Date;
        }
    }
}
