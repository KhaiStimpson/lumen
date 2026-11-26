using System.ComponentModel.DataAnnotations;
using Shop.Web.Data.Entities;

namespace Shop.Web.Models.Orders;

public class OrderSummaryDto
{
    public int Id { get; init; }

    [Display(Name = "Order")]
    public string? Number { get; init; }

    [Display(Name = "Customer")]
    public string? CustomerName { get; init; }

    [Display(Name = "Placed")]
    public DateTime PlacedOn { get; init; }

    public OrderStatus Status { get; init; }

    [DataType(DataType.Currency)]
    public decimal Total { get; init; }
}
