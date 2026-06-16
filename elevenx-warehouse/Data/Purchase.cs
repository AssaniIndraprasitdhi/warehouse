using System.ComponentModel.DataAnnotations;

namespace ElevenX.Warehouse.Data;

/// <summary>
/// Purchase = บันทึกค่าใช้จ่ายทุกชนิด (ซื้อครั้งเดียว / เติมสต็อก / ค่ารอบ subscription)
/// รายงาน "ใช้จ่ายรวม" = ผลรวม <see cref="TotalCost"/> ในช่วงเวลาที่เลือก
/// </summary>
public class Purchase
{
    public int Id { get; set; }

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    public string PurchasedById { get; set; } = "";          // คนซื้อ/คนบันทึก
    public ApplicationUser PurchasedBy { get; set; } = null!;

    public bool IsRecurringCharge { get; set; }              // true = ค่ารอบ subscription, false = ซื้อครั้งเดียว/เติมสต็อก
    public int Quantity { get; set; }                        // IoT: เข้าสต็อก / Server-Software: seat ที่เพิ่ม / recurring: 0
    public decimal UnitPrice { get; set; }
    public decimal TotalCost { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public DateTime? PeriodStart { get; set; }               // สำหรับ recurring charge
    public DateTime? PeriodEnd { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }
}
