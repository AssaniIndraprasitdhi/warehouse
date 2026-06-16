using System.ComponentModel.DataAnnotations;

namespace ElevenX.Warehouse.Data;

/// <summary>Withdrawal = เบิกของออกจากสต็อก (เฉพาะ IoT material)</summary>
public class Withdrawal
{
    public int Id { get; set; }

    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public string WithdrawnById { get; set; } = "";          // คนเบิก
    public ApplicationUser WithdrawnBy { get; set; } = null!;

    public int Quantity { get; set; }

    [MaxLength(300)]
    public string? Purpose { get; set; }

    public DateTime WithdrawnAt { get; set; } = DateTime.UtcNow;

    [MaxLength(500)]
    public string? Note { get; set; }
}
