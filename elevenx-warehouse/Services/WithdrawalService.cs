using ElevenX.Warehouse.Data;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Services;

public interface IWithdrawalService
{
    Task<List<Withdrawal>> GetWithdrawalsAsync(
        DateTime? from = null, DateTime? to = null, int? itemId = null, string? search = null);

    /// <summary>เบิกของออกจากสต็อก (เฉพาะ IoT material) — ตรวจสอบว่าคงเหลือพอ</summary>
    Task<OperationResult<Withdrawal>> RecordWithdrawalAsync(
        int itemId, int quantity, string withdrawnById, string? purpose, DateTime when, string? note);

    /// <summary>รายการ IoT ที่ยังมีสต็อกให้เบิก</summary>
    Task<List<Item>> GetWithdrawableItemsAsync();

    Task<OperationResult> DeleteAsync(int id);
}

public class WithdrawalService(IDbContextFactory<ApplicationDbContext> dbFactory, CurrentUserAccessor currentUser) : IWithdrawalService
{
    private async Task<bool> CanWithdrawAsync() => await currentUser.IsInAnyRoleAsync(AppRoles.Admin, AppRoles.Purchaser, AppRoles.Staff);
    private async Task<bool> CanManageAsync() => await currentUser.IsInAnyRoleAsync(AppRoles.Admin, AppRoles.Purchaser);
    private const string Forbidden = "คุณไม่มีสิทธิ์ดำเนินการนี้";

    public async Task<List<Withdrawal>> GetWithdrawalsAsync(
        DateTime? from = null, DateTime? to = null, int? itemId = null, string? search = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Withdrawals
            .Include(w => w.Item)
            .Include(w => w.WithdrawnBy)
            .AsQueryable();

        if (from is not null) q = q.Where(w => w.WithdrawnAt >= from);
        if (to is not null) q = q.Where(w => w.WithdrawnAt < to.Value.Date.AddDays(1));
        if (itemId is not null) q = q.Where(w => w.ItemId == itemId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            q = q.Where(w => EF.Functions.ILike(w.Item.Name, pattern)
                          || (w.Purpose != null && EF.Functions.ILike(w.Purpose, pattern)));
        }

        return await q.OrderByDescending(w => w.WithdrawnAt).ThenByDescending(w => w.Id).ToListAsync();
    }

    public async Task<OperationResult<Withdrawal>> RecordWithdrawalAsync(
        int itemId, int quantity, string withdrawnById, string? purpose, DateTime when, string? note)
    {
        if (!await CanWithdrawAsync()) return OperationResult<Withdrawal>.Fail(Forbidden);
        if (quantity < 1)
            return OperationResult<Withdrawal>.Fail("จำนวนที่เบิกต้องมากกว่า 0");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null)
            return OperationResult<Withdrawal>.Fail("ไม่พบรายการสินค้า");
        if (item.Type != ItemType.IotMaterial)
            return OperationResult<Withdrawal>.Fail("เบิกได้เฉพาะวัสดุ IoT เท่านั้น");
        if (item.Quantity < quantity)
            return OperationResult<Withdrawal>.Fail($"สต็อกคงเหลือไม่พอ (เหลือ {item.Quantity} {item.Unit})");

        var withdrawal = new Withdrawal
        {
            ItemId = itemId,
            WithdrawnById = withdrawnById,
            Quantity = quantity,
            Purpose = purpose,
            WithdrawnAt = when,
            Note = note,
        };
        db.Withdrawals.Add(withdrawal);

        item.Quantity -= quantity;
        item.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return OperationResult<Withdrawal>.Ok(withdrawal);
    }

    public async Task<List<Item>> GetWithdrawableItemsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Items
            .Where(i => i.Type == ItemType.IotMaterial)
            .OrderBy(i => i.Name)
            .ToListAsync();
    }

    public async Task<OperationResult> DeleteAsync(int id)
    {
        if (!await CanManageAsync()) return OperationResult.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        var w = await db.Withdrawals.Include(x => x.Item).FirstOrDefaultAsync(x => x.Id == id);
        if (w is null)
            return OperationResult.Fail("ไม่พบรายการเบิก");

        // คืนสต็อกกลับเมื่อลบรายการเบิก
        if (w.Item.Type == ItemType.IotMaterial)
            w.Item.Quantity += w.Quantity;

        db.Withdrawals.Remove(w);
        await db.SaveChangesAsync();
        return OperationResult.Ok();
    }
}
