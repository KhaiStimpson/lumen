using System.ComponentModel.DataAnnotations;

namespace Shop.Web.Data.Entities;

public class Customer
{
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [EmailAddress]
    [MaxLength(320)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string Role { get; set; } = "Customer";

    [MaxLength(2000)]
    public string? Bio { get; set; }

    public bool IsDeleted { get; set; }

    public List<Order> Orders { get; set; } = new();
}
