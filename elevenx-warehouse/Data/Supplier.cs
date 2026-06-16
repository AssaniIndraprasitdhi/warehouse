using System.ComponentModel.DataAnnotations;

namespace ElevenX.Warehouse.Data;

public class Supplier
{
    public int Id { get; set; }

    [Required(ErrorMessage = "กรุณาระบุชื่อผู้ขาย")]
    [MaxLength(160)]
    public string Name { get; set; } = "";

    [MaxLength(200)]
    public string? Contact { get; set; }

    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
}
