using System.ComponentModel.DataAnnotations;

namespace ElevenX.Warehouse.Data;

public class Category
{
    public int Id { get; set; }

    [Required(ErrorMessage = "กรุณาระบุชื่อหมวดหมู่")]
    [MaxLength(120)]
    public string Name { get; set; } = "";

    public ICollection<Item> Items { get; set; } = new List<Item>();
}
