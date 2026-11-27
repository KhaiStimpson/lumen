using System.ComponentModel.DataAnnotations;

namespace Shop.Web.Data.Entities;

public class Invoice
{
    public int Id { get; set; }

    [Required]
    [MaxLength(20)]
    public string Number { get; set; } = string.Empty;

    public int OrderId { get; set; }

    public Order? Order { get; set; }

    public DateOnly IssuedOn { get; set; }

    public DateOnly DueOn { get; set; }

    public decimal Amount { get; set; }

    public bool IsPaid { get; set; }
}
