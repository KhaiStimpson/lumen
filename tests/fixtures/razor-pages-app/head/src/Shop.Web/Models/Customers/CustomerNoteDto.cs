using System.ComponentModel.DataAnnotations;

namespace Shop.Web.Models.Customers;

public class CustomerNoteDto
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    [Display(Name = "Note")]
    public string? Body { get; set; }

    [Display(Name = "Added by")]
    public string Author { get; set; } = "system";

    [Display(Name = "Added")]
    public DateTime CreatedOn { get; set; }

    public bool IsPinned { get; set; }
}
