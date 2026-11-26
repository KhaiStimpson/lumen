using System.ComponentModel.DataAnnotations;

namespace Shop.Web.Data.Entities;

public class Product
{
    public int Id { get; set; }

    [Required]
    [MaxLength(40)]
    public string Sku { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public int StockLevel { get; set; }

    public bool IsActive { get; set; } = true;
}
