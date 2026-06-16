using ElevenX.Warehouse.Data;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Services;

public interface IPurchaseService
{
    Task<List<Purchase>> GetPurchasesAsync(
        DateTime? from = null, DateTime? to = null, int? itemId = null,
        ItemType? type = null, bool? recurringOnly = null, string? search = null);

    /// <summary>บันทึกการซื้อแบบครั้งเดียว/เติมสต็อก (ไม่ใช่ค่ารอบ subscription)</summary>
    Task<OperationResult<Purchase>> RecordPurchaseAsync(
        int itemId, int quantity, decimal unitPrice, int? supplierId,
        string purchasedById, DateTime date, string? note);

    Task<OperationResult> DeleteAsync(int id);
}

public class PurchaseService(IDbContextFactory<ApplicationDbContext> dbFactory, CurrentUserAccessor currentUser) : IPurchaseService
{
    private async Task<bool> CanManageAsync() => await currentUser.IsInAnyRoleAsync(AppRoles.Admin, AppRoles.Purchaser);
    private const string Forbidden = "คุณไม่มีสิทธิ์ดำเนินการนี้";

    public async Task<List<Purchase>> GetPurchasesAsync(
        DateTime? from = null, DateTime? to = null, int? itemId = null,
        ItemType? type = null, bool? recurringOnly = null, string? search = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Purchases
            .Include(p => p.Item)
            .Include(p => p.Supplier)
            .Include(p => p.PurchasedBy)
            .AsQueryable();

        if (from is not null) q = q.Where(p => p.Date >= from);
        if (to is not null) q = q.Where(p => p.Date < to.Value.Date.AddDays(1));
        if (itemId is not null) q = q.Where(p => p.ItemId == itemId);
        if (type is not null) q = q.Where(p => p.Item.Type == type);
        if (recurringOnly is not null) q = q.Where(p => p.IsRecurringCharge == recurringOnly);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            q = q.Where(p => EF.Functions.ILike(p.Item.Name, pattern)
                          || (p.Note != null && EF.Functions.ILike(p.Note, pattern)));
        }

        return await q.OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).ToListAsync();
    }

    public async Task<OperationResult<Purchase>> RecordPurchaseAsync(
        int itemId, int quantity, decimal unitPrice, int? supplierId,
        string purchasedById, DateTime date, string? note)
    {
        if (!await CanManageAsync()) return OperationResult<Purchase>.Fail(Forbidden);
        if (quantity < 1)
            return OperationResult<Purchase>.Fail("จำนวนต้องมากกว่า 0");
        if (unitPrice < 0)
            return OperationResult<Purchase>.Fail("ราคาต่อหน่วยต้องไม่ติดลบ");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null)
            return OperationResult<Purchase>.Fail("ไม่พบรายการสินค้า");

        var purchase = new Purchase
        {
            ItemId = itemId,
            SupplierId = supplierId,
            PurchasedById = purchasedById,
            IsRecurringCharge = false,
            Quantity = quantity,
            UnitPrice = unitPrice,
            TotalCost = quantity * unitPrice,
            Date = date,
            Note = note,
        };
        db.Purchases.Add(purchase);

        // ปรับสต็อก/ที่นั่งตามประเภท
        if (item.Type == ItemType.IotMaterial)
        {
            item.Quantity += quantity;        // เติมสต็อก
        }
        else if (item.Type is ItemType.Server or ItemType.Software)
        {
            item.TotalSeats = (item.TotalSeats ?? 0) + quantity;   // เพิ่ม seat/license
        }
        item.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return OperationResult<Purchase>.Ok(purchase);
    }

    public async Task<OperationResult> DeleteAsync(int id)
    {
        if (!await CanManageAsync()) return OperationResult.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        var purchase = await db.Purchases.Include(p => p.Item).FirstOrDefaultAsync(p => p.Id == id);
        if (purchase is null)
            return OperationResult.Fail("ไม่พบรายการค่าใช้จ่าย");

        // ย้อนผลกระทบต่อสต็อก/ที่นั่ง เฉพาะการซื้อที่ไม่ใช่ค่ารอบ
        if (!purchase.IsRecurringCharge)
        {
            if (purchase.Item.Type == ItemType.IotMaterial)
                purchase.Item.Quantity = Math.Max(0, purchase.Item.Quantity - purchase.Quantity);
            else if (purchase.Item.Type is ItemType.Server or ItemType.Software)
                purchase.Item.TotalSeats = Math.Max(0, (purchase.Item.TotalSeats ?? 0) - purchase.Quantity);
        }

        db.Purchases.Remove(purchase);
        await db.SaveChangesAsync();
        return OperationResult.Ok();
    }
}
