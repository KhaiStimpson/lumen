using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Shop.Web.Data;
using Shop.Web.Data.Entities;
using Shop.Web.Models.Customers;

namespace Shop.Web.Services;

public class CustomerNoteService
{
    private readonly ShopDbContext _db;
    private readonly IMapper _mapper;
    private readonly TimeProvider _clock;

    public CustomerNoteService(ShopDbContext db, IMapper mapper, TimeProvider clock)
    {
        _db = db;
        _mapper = mapper;
        _clock = clock;
    }

    public async Task<IReadOnlyList<CustomerNoteDto>> ListAsync(int customerId, CancellationToken cancellationToken = default)
    {
        var notes = await _db.CustomerNotes
            .Where(n => n.CustomerId == customerId)
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.CreatedOn)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return _mapper.Map<List<CustomerNoteDto>>(notes);
    }

    public async Task<CustomerNoteDto> AddAsync(int customerId, string body, string author, CancellationToken cancellationToken = default)
    {
        var note = new CustomerNote
        {
            CustomerId = customerId,
            Body = body.Trim(),
            Author = author,
            CreatedOn = _clock.GetUtcNow().UtcDateTime,
        };

        _db.CustomerNotes.Add(note);
        await _db.SaveChangesAsync(cancellationToken);
        return _mapper.Map<CustomerNoteDto>(note);
    }

    public async Task<bool> SetPinnedAsync(int noteId, bool pinned, CancellationToken cancellationToken = default)
    {
        var note = await _db.CustomerNotes.FirstOrDefaultAsync(n => n.Id == noteId, cancellationToken);
        if (note is null)
        {
            return false;
        }

        note.IsPinned = pinned;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(int noteId, CancellationToken cancellationToken = default)
    {
        var note = await _db.CustomerNotes.FirstOrDefaultAsync(n => n.Id == noteId, cancellationToken);
        if (note is null)
        {
            return false;
        }

        note.IsDeleted = true;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
