using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Services;

namespace Shop.Web.Pages.Customers;

public class EditModel : PageModel
{
    private readonly ICustomerService _customerService;

    public EditModel(ICustomerService customerService)
    {
        _customerService = customerService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string DisplayName { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _customerService.GetAsync(id, cancellationToken);
        if (customer is null)
        {
            return NotFound();
        }

        DisplayName = _customerService.GetDisplayName(customer);
        Input = new InputModel
        {
            Id = customer.Id,
            Name = customer.Name,
            Email = customer.Email,
            Bio = customer.Bio,
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _customerService.GetAsync(id, cancellationToken);
        if (customer is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            DisplayName = _customerService.GetDisplayName(customer);
            return Page();
        }

        customer.Name = Input.Name;
        customer.Email = Input.Email;
        customer.Bio = Input.Bio;

        await _customerService.UpdateAsync(customer, cancellationToken);
        return RedirectToPage(new { id });
    }

    public class InputModel
    {
        public int Id { get; set; }

        [Required]
        [StringLength(200)]
        public string Name { get; set; } = string.Empty;

        [EmailAddress]
        public string? Email { get; set; }

        [StringLength(2000)]
        public string? Bio { get; set; }
    }
}
