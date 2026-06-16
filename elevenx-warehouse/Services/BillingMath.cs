using ElevenX.Warehouse.Data;

namespace ElevenX.Warehouse.Services;

/// <summary>ตัวช่วยคำนวณรอบบิลของ subscription</summary>
public static class BillingMath
{
    /// <summary>เลื่อนวันที่ไปอีก 1 รอบตาม cycle</summary>
    public static DateTime Advance(DateTime date, BillingCycle cycle) => cycle switch
    {
        BillingCycle.Monthly => date.AddMonths(1),
        BillingCycle.Quarterly => date.AddMonths(3),
        BillingCycle.Yearly => date.AddYears(1),
        _ => date.AddMonths(1),
    };

    /// <summary>แปลงค่าใช้จ่ายต่อรอบให้เป็น "ต่อเดือน" เพื่อรวมยอด subscription รายเดือน</summary>
    public static decimal MonthlyEquivalent(decimal amount, BillingCycle cycle) => cycle switch
    {
        BillingCycle.Monthly => amount,
        BillingCycle.Quarterly => amount / 3m,
        BillingCycle.Yearly => amount / 12m,
        _ => amount,
    };

    /// <summary>จำนวนเดือนต่อรอบ (ใช้คำนวณ PeriodEnd)</summary>
    public static int MonthsPerCycle(BillingCycle cycle) => cycle switch
    {
        BillingCycle.Monthly => 1,
        BillingCycle.Quarterly => 3,
        BillingCycle.Yearly => 12,
        _ => 1,
    };

    public static string CycleLabel(BillingCycle cycle) => cycle switch
    {
        BillingCycle.Monthly => "รายเดือน",
        BillingCycle.Quarterly => "ราย 3 เดือน",
        BillingCycle.Yearly => "รายปี",
        _ => cycle.ToString(),
    };
}
