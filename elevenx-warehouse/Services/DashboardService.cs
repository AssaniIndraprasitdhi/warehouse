using ElevenX.Warehouse.Data;
using Microsoft.EntityFrameworkCore;

namespace ElevenX.Warehouse.Services;

public interface IDashboardService
{
    Task<DashboardSummary> GetSummaryAsync();
}

public class DashboardService(IDbContextFactory<ApplicationDbContext> dbFactory) : IDashboardService
{
    public async Task<DashboardSummary> GetSummaryAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var today = DateTime.UtcNow.Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var lastMonthStart = monthStart.AddMonths(-1);
        var seriesStart = monthStart.AddMonths(-5);   // 6 เดือนย้อนหลัง

        // ----- มูลค่าสต็อก IoT (weighted average cost) -----
        var iotItems = await db.Items
            .Include(i => i.Category)
            .Where(i => i.Type == ItemType.IotMaterial)
            .ToListAsync();

        var costAgg = await db.Purchases
            .Where(p => !p.IsRecurringCharge && p.Item.Type == ItemType.IotMaterial && p.Quantity > 0)
            .GroupBy(p => p.ItemId)
            .Select(g => new { ItemId = g.Key, Spent = g.Sum(p => p.TotalCost), Qty = g.Sum(p => p.Quantity) })
            .ToDictionaryAsync(x => x.ItemId, x => new { x.Spent, x.Qty });

        decimal iotStockValue = 0;
        foreach (var i in iotItems)
        {
            var agg = costAgg.GetValueOrDefault(i.Id);
            var avg = agg is not null && agg.Qty > 0 ? agg.Spent / agg.Qty : 0;
            iotStockValue += i.Quantity * avg;
        }

        // ----- ค่าใช้จ่ายเดือนนี้ / เดือนก่อน -----
        var thisMonthSpend = await db.Purchases
            .Where(p => p.Date >= monthStart && p.Date < monthStart.AddMonths(1))
            .SumAsync(p => (decimal?)p.TotalCost) ?? 0;
        var lastMonthSpend = await db.Purchases
            .Where(p => p.Date >= lastMonthStart && p.Date < monthStart)
            .SumAsync(p => (decimal?)p.TotalCost) ?? 0;

        // ----- subscription -----
        var activeSubs = await db.Items
            .Where(i => i.CostType == CostType.Recurring && i.Status == SubscriptionStatus.Active)
            .ToListAsync();
        var monthlySubTotal = activeSubs.Sum(i =>
            BillingMath.MonthlyEquivalent(i.RecurringAmount ?? 0, i.BillingCycle ?? BillingCycle.Monthly));

        var upcoming = activeSubs
            .Where(i => i.NextBillingDate != null && i.NextBillingDate <= today.AddDays(30))
            .OrderBy(i => i.NextBillingDate)
            .Select(i => new UpcomingRenewal(
                i.Id, i.Name, i.Type, i.NextBillingDate!.Value,
                i.RecurringAmount ?? 0, i.BillingCycle ?? BillingCycle.Monthly,
                (int)Math.Ceiling((i.NextBillingDate!.Value.Date - today).TotalDays)))
            .ToList();

        // ----- สต็อกต่ำ -----
        var lowStock = iotItems
            .Where(i => i.Quantity <= i.MinQuantity)
            .OrderBy(i => i.Quantity)
            .ToList();

        // ----- seat usage -----
        var seatItems = await db.Items
            .Where(i => (i.Type == ItemType.Server || i.Type == ItemType.Software) && i.TotalSeats != null)
            .Select(i => new { i.Id, i.Name, i.Type, Total = i.TotalSeats!.Value })
            .ToListAsync();
        var usedMap = await db.LicenseAssignments
            .Where(l => l.ReleasedAt == null)
            .GroupBy(l => l.ItemId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var seatUsages = seatItems
            .Select(i => new SeatUsage(i.Id, i.Name, i.Type, usedMap.GetValueOrDefault(i.Id), i.Total))
            .ToList();
        var seatsNearFull = seatUsages
            .Where(s => s.Total > 0 && (s.Available <= 1 || s.UsedRatio >= 0.9))
            .OrderByDescending(s => s.UsedRatio)
            .ToList();

        // ----- กราฟค่าใช้จ่ายรายเดือนแยกตามประเภท (6 เดือน) -----
        var seriesPurchases = await db.Purchases
            .Include(p => p.Item)
            .Where(p => p.Date >= seriesStart)
            .Select(p => new { p.Date, p.Item.Type, p.TotalCost })
            .ToListAsync();

        var series = new List<MonthlySpendPoint>();
        for (int m = 0; m < 6; m++)
        {
            var ms = seriesStart.AddMonths(m);
            var me = ms.AddMonths(1);
            var inMonth = seriesPurchases.Where(p => p.Date >= ms && p.Date < me).ToList();
            series.Add(new MonthlySpendPoint(
                ms.ToString("MM/yyyy"),
                inMonth.Where(p => p.Type == ItemType.IotMaterial).Sum(p => p.TotalCost),
                inMonth.Where(p => p.Type == ItemType.Server).Sum(p => p.TotalCost),
                inMonth.Where(p => p.Type == ItemType.Software).Sum(p => p.TotalCost),
                inMonth.Where(p => p.Type == ItemType.Other).Sum(p => p.TotalCost)));
        }

        // ----- รายการเคลื่อนไหวล่าสุด -----
        var recentPurchases = await db.Purchases.Include(p => p.Item).Include(p => p.PurchasedBy)
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).Take(8).ToListAsync();
        var recentWithdrawals = await db.Withdrawals.Include(w => w.Item).Include(w => w.WithdrawnBy)
            .OrderByDescending(w => w.WithdrawnAt).ThenByDescending(w => w.Id).Take(8).ToListAsync();
        var recentLicenses = await db.LicenseAssignments.Include(l => l.Item).Include(l => l.AssignedTo)
            .OrderByDescending(l => l.AssignedAt).ThenByDescending(l => l.Id).Take(8).ToListAsync();

        var activity = new List<ActivityEntry>();
        activity.AddRange(recentPurchases.Select(p => new ActivityEntry(
            p.Date,
            p.IsRecurringCharge ? "ค่ารอบ" : "ซื้อ/เติม",
            p.IsRecurringCharge
                ? $"บันทึกค่ารอบ {p.Item.Name} {p.TotalCost:N0} ฿"
                : $"ซื้อ/เติม {p.Item.Name} จำนวน {p.Quantity} ({p.TotalCost:N0} ฿)",
            DisplayName(p.PurchasedBy),
            p.IsRecurringCharge ? "info" : "accent")));
        activity.AddRange(recentWithdrawals.Select(w => new ActivityEntry(
            w.WithdrawnAt, "เบิก",
            $"เบิก {w.Item.Name} จำนวน {w.Quantity}",
            DisplayName(w.WithdrawnBy), "warning")));
        activity.AddRange(recentLicenses.Select(l => new ActivityEntry(
            l.AssignedAt, "License",
            $"จ่าย Seat {l.Item.Name} ให้ {DisplayName(l.AssignedTo)}",
            null, "purple")));

        var recentActivity = activity.OrderByDescending(a => a.When).Take(12).ToList();

        return new DashboardSummary
        {
            IotStockValue = iotStockValue,
            IotItemCount = iotItems.Count,
            ThisMonthSpend = thisMonthSpend,
            LastMonthSpend = lastMonthSpend,
            MonthlySubscriptionTotal = monthlySubTotal,
            ActiveSubscriptions = activeSubs.Count,
            LowStockCount = lowStock.Count,
            SeatsNearFullCount = seatsNearFull.Count,
            UsedSeatsTotal = seatUsages.Sum(s => s.Used),
            TotalSeatsTotal = seatUsages.Sum(s => s.Total),
            MonthlySpendSeries = series,
            UpcomingRenewals = upcoming,
            LowStockItems = lowStock,
            SeatsNearFull = seatsNearFull,
            RecentActivity = recentActivity,
        };
    }

    private static string DisplayName(ApplicationUser u) =>
        string.IsNullOrWhiteSpace(u.FullName) ? (u.UserName ?? u.Email ?? "-") : u.FullName;
}
