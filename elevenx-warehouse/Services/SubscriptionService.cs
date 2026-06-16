using ElevenX.Warehouse.Data;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Services;

public interface ISubscriptionService
{
    Task<List<Item>> GetSubscriptionsAsync(SubscriptionStatus? status = null);
    Task<List<UpcomingRenewal>> GetUpcomingRenewalsAsync(int withinDays = 30);

    /// <summary>บันทึกค่ารอบนี้ → สร้าง Purchase (IsRecurringCharge=true) แล้วเลื่อน NextBillingDate ไป 1 รอบ</summary>
    Task<OperationResult<Purchase>> RecordRecurringChargeAsync(int itemId, string recordedById, DateTime? chargeDate = null, string? note = null);

    Task<OperationResult> CancelAsync(int itemId);
    Task<OperationResult> ReactivateAsync(int itemId);

    /// <summary>ยอดรวม subscription ที่ active แปลงเป็นต่อเดือน</summary>
    decimal MonthlyTotal(IEnumerable<Item> subs);
}

public class SubscriptionService(IDbContextFactory<ApplicationDbContext> dbFactory, CurrentUserAccessor currentUser) : ISubscriptionService
{
    private async Task<bool> CanManageAsync() => await currentUser.IsInAnyRoleAsync(AppRoles.Admin, AppRoles.Purchaser);
    private const string Forbidden = "คุณไม่มีสิทธิ์ดำเนินการนี้";

    public async Task<List<Item>> GetSubscriptionsAsync(SubscriptionStatus? status = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.Items.Include(i => i.Category)
            .Where(i => i.CostType == CostType.Recurring);
        if (status is not null)
            q = q.Where(i => i.Status == status);
        return await q.OrderBy(i => i.Status).ThenBy(i => i.NextBillingDate).ToListAsync();
    }

    public async Task<List<UpcomingRenewal>> GetUpcomingRenewalsAsync(int withinDays = 30)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = DateTime.UtcNow.Date;
        var limit = today.AddDays(withinDays);

        var items = await db.Items
            .Where(i => i.CostType == CostType.Recurring
                     && i.Status == SubscriptionStatus.Active
                     && i.NextBillingDate != null
                     && i.NextBillingDate <= limit)
            .OrderBy(i => i.NextBillingDate)
            .ToListAsync();

        return items.Select(i => new UpcomingRenewal(
            i.Id, i.Name, i.Type, i.NextBillingDate!.Value,
            i.RecurringAmount ?? 0, i.BillingCycle ?? BillingCycle.Monthly,
            (int)Math.Ceiling((i.NextBillingDate!.Value.Date - today).TotalDays))).ToList();
    }

    public async Task<OperationResult<Purchase>> RecordRecurringChargeAsync(int itemId, string recordedById, DateTime? chargeDate = null, string? note = null)
    {
        if (!await CanManageAsync()) return OperationResult<Purchase>.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null)
            return OperationResult<Purchase>.Fail("ไม่พบรายการสินค้า");
        if (item.CostType != CostType.Recurring)
            return OperationResult<Purchase>.Fail("รายการนี้ไม่ใช่ subscription");
        if (item.Status != SubscriptionStatus.Active)
            return OperationResult<Purchase>.Fail("subscription นี้ไม่อยู่ในสถานะ Active");
        if (item.RecurringAmount is null || item.BillingCycle is null)
            return OperationResult<Purchase>.Fail("ยังไม่ได้กำหนดค่าใช้จ่าย/รอบบิลของ subscription");

        var cycle = item.BillingCycle.Value;
        // ใช้รอบบิลถัดไปเป็นต้นรอบ; ถ้าไม่มี ค่อย fallback เป็นวันเริ่ม/วันที่บันทึก
        var periodStart = (item.NextBillingDate ?? item.StartDate ?? (chargeDate ?? DateTime.UtcNow)).Date;
        var periodEnd = BillingMath.Advance(periodStart, cycle);

        var purchase = new Purchase
        {
            ItemId = item.Id,
            SupplierId = null,
            PurchasedById = recordedById,
            IsRecurringCharge = true,
            Quantity = 0,
            UnitPrice = item.RecurringAmount.Value,
            TotalCost = item.RecurringAmount.Value,
            Date = chargeDate ?? DateTime.UtcNow,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Note = note ?? $"ค่ารอบ {BillingMath.CycleLabel(cycle)}",
        };
        db.Purchases.Add(purchase);

        // เลื่อนรอบบิลถัดไป
        item.NextBillingDate = periodEnd;
        if (item.EndDate is not null && periodEnd > item.EndDate.Value)
            item.Status = SubscriptionStatus.Expired;
        item.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return OperationResult<Purchase>.Ok(purchase);
    }

    public async Task<OperationResult> CancelAsync(int itemId)
    {
        if (!await CanManageAsync()) return OperationResult.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return OperationResult.Fail("ไม่พบรายการสินค้า");
        if (item.CostType != CostType.Recurring) return OperationResult.Fail("รายการนี้ไม่ใช่ subscription");

        item.Status = SubscriptionStatus.Cancelled;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return OperationResult.Ok();
    }

    public async Task<OperationResult> ReactivateAsync(int itemId)
    {
        if (!await CanManageAsync()) return OperationResult.Fail(Forbidden);
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return OperationResult.Fail("ไม่พบรายการสินค้า");
        if (item.CostType != CostType.Recurring) return OperationResult.Fail("รายการนี้ไม่ใช่ subscription");

        item.Status = SubscriptionStatus.Active;
        var today = DateTime.UtcNow.Date;
        if (item.NextBillingDate is null || item.NextBillingDate < today)
            item.NextBillingDate = item.BillingCycle is null ? today : BillingMath.Advance(today, item.BillingCycle.Value);
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return OperationResult.Ok();
    }

    public decimal MonthlyTotal(IEnumerable<Item> subs) =>
        subs.Where(i => i.CostType == CostType.Recurring && i.Status == SubscriptionStatus.Active)
            .Sum(i => BillingMath.MonthlyEquivalent(i.RecurringAmount ?? 0, i.BillingCycle ?? BillingCycle.Monthly));
}
