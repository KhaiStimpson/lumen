using System.ComponentModel.DataAnnotations;
using Shop.Web.Data.Entities;

namespace Shop.Web.Models.Orders;

public class BulkEditInput
{
    [Required]
    [MinLength(1, ErrorMessage = "Select at least one order.")]
    public List<int>? OrderIds { get; set; }

    [Required]
    [Display(Name = "New status")]
    public OrderStatus? NewStatus { get; set; }

    [Range(0, 25)]
    [Display(Name = "Discount %")]
    public decimal? DiscountPercent { get; set; }
}
