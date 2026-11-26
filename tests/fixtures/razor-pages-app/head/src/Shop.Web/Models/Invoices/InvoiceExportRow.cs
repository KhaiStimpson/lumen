using System.ComponentModel.DataAnnotations;

namespace Shop.Web.Models.Invoices;

public class InvoiceExportRow
{
    [Display(Name = "Invoice")]
    public string? Number { get; set; }

    [Display(Name = "Order id")]
    public int OrderId { get; set; }

    [Display(Name = "Order")]
    public string? OrderNumber { get; set; }

    [Display(Name = "Customer")]
    public string? CustomerName { get; set; }

    [Display(Name = "Issued")]
    public DateOnly IssuedOn { get; set; }

    [Display(Name = "Due")]
    public DateOnly DueOn { get; set; }

    [Display(Name = "Amount")]
    [DataType(DataType.Currency)]
    public decimal Amount { get; set; }

    [Display(Name = "Paid")]
    public bool IsPaid { get; set; }

    [Display(Name = "Days overdue")]
    public int DaysOverdue { get; set; }
}
