using System.ComponentModel.DataAnnotations;

namespace ElevenX.Warehouse.Data;

/// <summary>LicenseAssignment = การจ่าย license/seat ให้ผู้ใช้ (เฉพาะ Server / Software)</summary>
public class LicenseAssignment
{
    public int Id { get; set; }

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public string AssignedToId { get; set; } = "";           // ผู้ใช้ที่ได้ seat
    public ApplicationUser AssignedTo { get; set; } = null!;

    public string AssignedById { get; set; } = "";           // คนกำหนด
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReleasedAt { get; set; }                // null = ยังใช้อยู่

    [MaxLength(500)]
    public string? Note { get; set; }

    public bool IsActive => ReleasedAt is null;
}
