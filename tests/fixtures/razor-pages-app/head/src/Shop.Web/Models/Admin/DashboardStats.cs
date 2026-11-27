using System.ComponentModel.DataAnnotations;

namespace Shop.Web.Models.Admin;

public class DashboardStats
{
    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    [Display(Name = "Orders")]
    public int OrderCount { get; set; }

    [Display(Name = "Revenue")]
    [DataType(DataType.Currency)]
    public decimal Revenue { get; set; }

    [Display(Name = "Customers")]
    public int CustomerCount { get; set; }

    [Display(Name = "Unpaid invoices")]
    [DataType(DataType.Currency)]
    public decimal UnpaidInvoiceTotal { get; set; }

    public Dictionary<string, int>? OrdersByStatus { get; set; }

    [Display(Name = "Average order")]
    public decimal AverageOrderValue => OrderCount == 0 ? 0m : Math.Round(Revenue / OrderCount, 2);
}
