using System.ComponentModel.DataAnnotations;

namespace Shop.Web.Models.Invoices;

public class InvoiceExportRequest
{
    [Required]
    [Display(Name = "From")]
    [DataType(DataType.Date)]
    public DateOnly From { get; set; }

    [Required]
    [Display(Name = "To")]
    [DataType(DataType.Date)]
    public DateOnly To { get; set; }

    [Display(Name = "Unpaid only")]
    public bool UnpaidOnly { get; set; }

    [Display(Name = "Include header row")]
    public bool IncludeHeader { get; set; }

    [Required]
    [StringLength(1, MinimumLength = 1)]
    public string Delimiter { get; set; } = ",";
}
