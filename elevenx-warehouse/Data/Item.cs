using System.ComponentModel.DataAnnotations;

namespace ElevenX.Warehouse.Data;

/// <summary>
/// Item เดียวครอบคลุมทั้ง IoT material / Server / Software โดยใช้ <see cref="ItemType"/> แยกพฤติกรรม
/// และ <see cref="CostType"/> แยกว่าจ่ายครั้งเดียวหรือเป็นรอบ (subscription)
/// </summary>
public class Item
{
    public int Id { get; set; }

    [Required(ErrorMessage = "กรุณาระบุชื่อรายการ")]
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(80)]
    public string? Sku { get; set; }                 // unique ถ้ามี

    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;

    public ItemType Type { get; set; }
    public CostType CostType { get; set; }

    [MaxLength(1000)]
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ===== ใช้เมื่อ Type = IotMaterial (ของจับต้องได้ นับสต็อก) =====
    [MaxLength(40)]
    public string? Unit { get; set; }                // ชิ้น, ม้วน, เมตร
    public int Quantity { get; set; }                // คงเหลือ
    public int MinQuantity { get; set; }             // จุดเตือนสต็อกต่ำ
    [MaxLength(120)]
    public string? Location { get; set; }

    // ===== ใช้เมื่อ Type = Server / Software (นับ license/seat) =====
    public int? TotalSeats { get; set; }             // จำนวน license/seat ทั้งหมด
    // UsedSeats = นับจาก LicenseAssignment ที่ยัง active (compute เสมอ ไม่เก็บซ้ำ)

    // ===== ใช้เมื่อ CostType = Recurring (subscription) =====
    public decimal? RecurringAmount { get; set; }    // ค่าใช้จ่ายต่อรอบ
    public BillingCycle? BillingCycle { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public SubscriptionStatus? Status { get; set; }

    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
    public ICollection<Withdrawal> Withdrawals { get; set; } = new List<Withdrawal>();
    public ICollection<LicenseAssignment> LicenseAssignments { get; set; } = new List<LicenseAssignment>();

    // ===== Computed helpers (ไม่ map ลง DB) =====
    public bool IsLowStock => Type == ItemType.IotMaterial && Quantity <= MinQuantity;
    public bool TracksSeats => Type is ItemType.Server or ItemType.Software && TotalSeats.HasValue;
    public bool IsSubscription => CostType == CostType.Recurring;
}
