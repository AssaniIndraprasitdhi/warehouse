using ElevenX.Warehouse.Data;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Services;

public interface IReportService
{
    Task<SpendReportResult> GetSpendReportAsync(
        DateTime from, DateTime to, ReportGroupBy groupBy,
        ItemType? type = null, int? categoryId = null, string? purchaserId = null, bool? recurringOnly = null);

    Task<List<WithdrawalReportRow>> GetWithdrawalReportAsync(DateTime from, DateTime to, ItemType? type = null);
    Task<List<LicenseUsageRow>> GetLicenseUsageReportAsync();
    Task<List<ApplicationUser>> GetPurchasersAsync();
}

public class ReportService(IDbContextFactory<ApplicationDbContext> dbFactory) : IReportService
{
    public async Task<SpendReportResult> GetSpendReportAsync(
        DateTime from, DateTime to, ReportGroupBy groupBy,
        ItemType? type = null, int? categoryId = null, string? purchaserId = null, bool? recurringOnly = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var toExclusive = to.Date.AddDays(1);

        var q = db.Purchases
            .Include(p => p.Item).ThenInclude(i => i.Category)
            .Include(p => p.PurchasedBy)
            .Include(p => p.Supplier)
            .Where(p => p.Date >= from.Date && p.Date < toExclusive);

        if (type is not null) q = q.Where(p => p.Item.Type == type);
        if (categoryId is not null) q = q.Where(p => p.Item.CategoryId == categoryId);
        if (!string.IsNullOrEmpty(purchaserId)) q = q.Where(p => p.PurchasedById == purchaserId);
        if (recurringOnly is not null) q = q.Where(p => p.IsRecurringCharge == recurringOnly);

        var purchases = await q.ToListAsync();

        Func<Purchase, string> keySelector = groupBy switch
        {
            ReportGroupBy.Type => p => DisplayLabels.Type(p.Item.Type),
            ReportGroupBy.Category => p => p.Item.Category.Name,
            ReportGroupBy.Purchaser => p => string.IsNullOrWhiteSpace(p.PurchasedBy.FullName) ? (p.PurchasedBy.Email ?? "-") : p.PurchasedBy.FullName,
            ReportGroupBy.Supplier => p => p.Supplier?.Name ?? "ไม่ระบุผู้ขาย",
            ReportGroupBy.Month => p => p.Date.ToString("yyyy-MM"),
            _ => p => "-",
        };

        var rows = purchases
            .GroupBy(keySelector)
            .Select(g => new SpendReportRow(
                g.Key,
                g.Where(p => !p.IsRecurringCharge).Sum(p => p.TotalCost),
                g.Where(p => p.IsRecurringCharge).Sum(p => p.TotalCost),
                g.Count()))
            .OrderByDescending(r => r.Total)
            .ToList();

        return new SpendReportResult(
            purchases.Sum(p => p.TotalCost),
            purchases.Where(p => !p.IsRecurringCharge).Sum(p => p.TotalCost),
            purchases.Where(p => p.IsRecurringCharge).Sum(p => p.TotalCost),
            purchases.Count,
            rows);
    }

    public async Task<List<WithdrawalReportRow>> GetWithdrawalReportAsync(DateTime from, DateTime to, ItemType? type = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var toExclusive = to.Date.AddDays(1);

        var q = db.Withdrawals.Include(w => w.Item)
            .Where(w => w.WithdrawnAt >= from.Date && w.WithdrawnAt < toExclusive);
        if (type is not null) q = q.Where(w => w.Item.Type == type);

        var data = await q.ToListAsync();

        return data
            .GroupBy(w => new { w.ItemId, w.Item.Name })
            .Select(g => new WithdrawalReportRow(
                g.Key.ItemId, g.Key.Name,
                g.Sum(w => w.Quantity), g.Count(),
                g.Max(w => w.WithdrawnAt)))
            .OrderByDescending(r => r.TotalQuantity)
            .ToList();
    }

    public async Task<List<LicenseUsageRow>> GetLicenseUsageReportAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var items = await db.Items
            .Where(i => i.Type == ItemType.Server || i.Type == ItemType.Software)
            .Select(i => new { i.Id, i.Name, i.Type, Total = i.TotalSeats ?? 0 })
            .ToListAsync();

        var grouped = await db.LicenseAssignments
            .GroupBy(l => l.ItemId)
            .Select(g => new
            {
                ItemId = g.Key,
                Active = g.Count(l => l.ReleasedAt == null),
                Ever = g.Count(),
            })
            .ToDictionaryAsync(x => x.ItemId);

        return items
            .Select(i =>
            {
                var stat = grouped.GetValueOrDefault(i.Id);
                return new LicenseUsageRow(i.Id, i.Name, i.Type, stat?.Active ?? 0, i.Total, stat?.Ever ?? 0);
            })
            .OrderByDescending(r => r.Used)
            .ToList();
    }

    public async Task<List<ApplicationUser>> GetPurchasersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.OrderBy(u => u.FullName).ToListAsync();
    }
}
