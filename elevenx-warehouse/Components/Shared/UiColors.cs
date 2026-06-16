namespace ElevenX.Warehouse.Components.Shared;

/// <summary>
/// คืน class ของ Tailwind เป็น "สตริงตายตัว" (literal) เพื่อให้ Tailwind scanner เก็บได้
/// — ห้าม interpolate class name ใน markup เพราะจะถูก purge ทิ้ง
/// </summary>
public static class UiColors
{
    // พื้นอ่อน + ตัวอักษรสีเดียวกัน (ใช้กับ icon chip)
    public static string SoftBg(string color) => color switch
    {
        "success" => "bg-success/15 text-success",
        "warning" => "bg-warning/15 text-warning",
        "info" => "bg-info/15 text-info",
        "purple" => "bg-purple/15 text-purple",
        "danger" => "bg-danger/15 text-danger",
        "neutral" => "bg-neutral/15 text-muted",
        "accent" => "bg-accent/15 text-accent-hover",
        _ => "bg-surface-2 text-muted",
    };

    // badge
    public static string Badge(string color) => color switch
    {
        "success" => "badge badge-success",
        "warning" => "badge badge-warning",
        "info" => "badge badge-info",
        "purple" => "badge badge-purple",
        "danger" => "badge badge-danger",
        "neutral" => "badge badge-neutral",
        "accent" => "badge badge-accent",
        _ => "badge badge-neutral",
    };

    // สีพื้นทึบ (legend dot / progress bar)
    public static string Solid(string color) => color switch
    {
        "success" => "bg-success",
        "warning" => "bg-warning",
        "info" => "bg-info",
        "purple" => "bg-purple",
        "danger" => "bg-danger",
        "neutral" => "bg-neutral",
        "accent" => "bg-accent",
        _ => "bg-neutral",
    };

    public static string Text(string color) => color switch
    {
        "success" => "text-success",
        "warning" => "text-warning",
        "info" => "text-info",
        "purple" => "text-purple",
        "danger" => "text-danger",
        "neutral" => "text-muted",
        "accent" => "text-accent-hover",
        _ => "text-muted",
    };

    // ===== แม็พประเภท item / สถานะ → สี =====
    public static string ItemTypeColor(Data.ItemType t) => t switch
    {
        Data.ItemType.IotMaterial => "info",
        Data.ItemType.Server => "purple",
        Data.ItemType.Software => "accent",
        _ => "neutral",
    };

    public static string ItemTypeIcon(Data.ItemType t) => t switch
    {
        Data.ItemType.IotMaterial => "package",
        Data.ItemType.Server => "server",
        Data.ItemType.Software => "software",
        _ => "tag",
    };

    public static string StatusColor(Data.SubscriptionStatus s) => s switch
    {
        Data.SubscriptionStatus.Active => "success",
        Data.SubscriptionStatus.Cancelled => "neutral",
        Data.SubscriptionStatus.Expired => "danger",
        _ => "neutral",
    };
}
