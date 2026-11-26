using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Shop.Web.Models.Customers;
using Shop.Web.Services;

namespace Shop.Web.Pages.Customers;

public class EditModel : PageModel
{
    private readonly ICustomerService _customerService;
    private readonly CustomerNoteService _noteService;

    public EditModel(ICustomerService customerService, CustomerNoteService noteService)
    {
        _customerService = customerService;
        _noteService = noteService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string DisplayName { get; private set; } = string.Empty;

    public IReadOnlyList<CustomerNoteDto> Notes { get; private set; } = Array.Empty<CustomerNoteDto>();

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
            IsAdmin = customer.Role == "Admin",
        };
        Notes = await _noteService.ListAsync(id, cancellationToken);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _customerService.GetAsync(id, cancellationToken);
        if (customer is null)
        {
            return NotFound();
        }

        customer.Name = Input.Name;
        customer.Email = Input.Email;
        customer.Bio = Input.Bio;
        customer.Role = Input.IsAdmin ? "Admin" : "Customer";

        await _customerService.UpdateAsync(customer, cancellationToken);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetNotesAsync(int id, CancellationToken cancellationToken)
    {
        var notes = await _noteService.ListAsync(id, cancellationToken);
        return Partial("_CustomerNotes", notes);
    }

    public async Task<IActionResult> OnPostAddNoteAsync(int id, string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequest(new { message = "A note cannot be empty." });
        }

        var author = User.Identity?.Name ?? "unknown";
        var note = await _noteService.AddAsync(id, body, author, cancellationToken);
        return new JsonResult(note);
    }

    public async Task<IActionResult> OnPostPinNoteAsync(int noteId, bool pinned, CancellationToken cancellationToken)
    {
        var found = await _noteService.SetPinnedAsync(noteId, pinned, cancellationToken);
        return found ? new JsonResult(new { noteId, pinned }) : NotFound();
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

        [Display(Name = "Administrator")]
        public bool IsAdmin { get; set; }
    }
}
