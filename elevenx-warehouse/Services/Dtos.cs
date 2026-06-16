using ElevenX.Warehouse.Data;

namespace ElevenX.Warehouse.Services;

// ============ Dashboard ============
public record DashboardSummary
{
    public decimal IotStockValue { get; init; }
    public int IotItemCount { get; init; }
    public decimal ThisMonthSpend { get; init; }
    public decimal LastMonthSpend { get; init; }
    public decimal MonthlySubscriptionTotal { get; init; }
    public int ActiveSubscriptions { get; init; }
    public int LowStockCount { get; init; }
    public int SeatsNearFullCount { get; init; }
    public int UsedSeatsTotal { get; init; }
    public int TotalSeatsTotal { get; init; }
    public List<MonthlySpendPoint> MonthlySpendSeries { get; init; } = [];
    public List<UpcomingRenewal> UpcomingRenewals { get; init; } = [];
    public List<Item> LowStockItems { get; init; } = [];
    public List<SeatUsage> SeatsNearFull { get; init; } = [];
    public List<ActivityEntry> RecentActivity { get; init; } = [];
}

/// <summary>จุดข้อมูลกราฟค่าใช้จ่ายรายเดือน แยกตามประเภท item</summary>
public record MonthlySpendPoint(string Month, decimal Iot, decimal Server, decimal Software, decimal Other)
{
    public decimal Total => Iot + Server + Software + Other;
}

public record UpcomingRenewal(int ItemId, string Name, ItemType Type, DateTime NextBillingDate, decimal Amount, BillingCycle Cycle, int DaysUntil);

public record SeatUsage(int ItemId, string Name, ItemType Type, int Used, int Total)
{
    public int Available => Math.Max(0, Total - Used);
    public double UsedRatio => Total <= 0 ? 0 : (double)Used / Total;
}

public record ActivityEntry(DateTime When, string Kind, string Description, string? User, string Color);

// ============ Reports ============
public enum ReportGroupBy { Type, Category, Purchaser, Supplier, Month }

public record SpendReportRow(string Key, decimal OneTime, decimal Recurring, int Count)
{
    public decimal Total => OneTime + Recurring;
}

public record SpendReportResult(
    decimal GrandTotal,
    decimal OneTimeTotal,
    decimal RecurringTotal,
    int PurchaseCount,
    List<SpendReportRow> Rows);

public record WithdrawalReportRow(int ItemId, string ItemName, int TotalQuantity, int Count, DateTime LastWithdrawnAt);

public record LicenseUsageRow(int ItemId, string ItemName, ItemType Type, int Used, int Total, int EverAssigned);
