using System.ComponentModel.DataAnnotations;

namespace Shop.Web.Data.Entities;

public class Order
{
    public int Id { get; set; }

    [Required]
    [MaxLength(20)]
    public string Number { get; set; } = string.Empty;

    public int CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public DateTime PlacedOn { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Draft;

    public decimal ShippingCost { get; set; }

    public decimal Total { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<OrderLine> Lines { get; set; } = new();
}

public enum OrderStatus
{
    Draft = 0,
    Placed = 1,
    Paid = 2,
    Shipped = 3,
    Cancelled = 4
}
