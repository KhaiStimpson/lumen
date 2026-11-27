using System.ComponentModel.DataAnnotations;

namespace Shop.Web.Data.Entities;

public class CustomerNote
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    [Required]
    [MaxLength(4000)]
    public string Body { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Author { get; set; } = string.Empty;

    public DateTime CreatedOn { get; set; }

    public bool IsPinned { get; set; }

    public bool IsDeleted { get; set; }
}
