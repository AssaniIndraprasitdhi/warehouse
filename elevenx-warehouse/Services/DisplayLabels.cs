using ElevenX.Warehouse.Data;

namespace ElevenX.Warehouse.Services;

/// <summary>ป้ายชื่อภาษาไทยของ enum ต่าง ๆ ใช้ร่วมกันทั้ง service และ UI</summary>
public static class DisplayLabels
{
    public static string Type(ItemType t) => t switch
    {
        ItemType.IotMaterial => "วัสดุ IoT",
        ItemType.Server => "Server",
        ItemType.Software => "Software",
        ItemType.Other => "อื่น ๆ",
        _ => t.ToString(),
    };

    public static string CostType(CostType c) => c switch
    {
        Data.CostType.OneTime => "จ่ายครั้งเดียว",
        Data.CostType.Recurring => "Subscription",
        _ => c.ToString(),
    };

    public static string Status(SubscriptionStatus s) => s switch
    {
        SubscriptionStatus.Active => "ใช้งานอยู่",
        SubscriptionStatus.Cancelled => "ยกเลิกแล้ว",
        SubscriptionStatus.Expired => "หมดอายุ",
        _ => s.ToString(),
    };

    public static string Cycle(BillingCycle c) => BillingMath.CycleLabel(c);
}
