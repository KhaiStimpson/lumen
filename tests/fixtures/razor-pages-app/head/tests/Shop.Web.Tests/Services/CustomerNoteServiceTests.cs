using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Moq;
using Shop.Web.Data;
using Shop.Web.Data.Entities;
using Shop.Web.Models.Customers;
using Shop.Web.Services;

namespace Shop.Web.Tests.Services;

public class CustomerNoteServiceTests
{
    private readonly Mock<IMapper> _mapper = new();

    private static ShopDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShopDbContext(options);
    }

    [Fact]
    public async Task AddAsync_trims_body_and_stamps_author()
    {
        await using var db = CreateDb();
        _mapper
            .Setup(m => m.Map<CustomerNoteDto>(It.IsAny<CustomerNote>()))
            .Returns((CustomerNote n) => new CustomerNoteDto { Id = n.Id, Body = n.Body, Author = n.Author });
        var sut = new CustomerNoteService(db, _mapper.Object, TimeProvider.System);

        var dto = await sut.AddAsync(customerId: 3, body: "  Prefers delivery before 10am  ", author: "khail");

        var saved = Assert.Single(db.CustomerNotes);
        Assert.Equal("Prefers delivery before 10am", saved.Body);
        Assert.Equal("khail", dto.Author);
    }

    [Fact]
    public async Task DeleteAsync_soft_deletes_so_the_query_filter_hides_the_note()
    {
        await using var db = CreateDb();
        var note = new CustomerNote { CustomerId = 3, Body = "Old note", Author = "khail" };
        db.CustomerNotes.Add(note);
        await db.SaveChangesAsync();
        var sut = new CustomerNoteService(db, _mapper.Object, TimeProvider.System);

        var deleted = await sut.DeleteAsync(note.Id);

        Assert.True(deleted);
        Assert.Empty(db.CustomerNotes);
        Assert.True(Assert.Single(db.CustomerNotes.Local).IsDeleted);
    }

    [Fact]
    public async Task SetPinnedAsync_returns_false_for_unknown_note()
    {
        await using var db = CreateDb();
        var sut = new CustomerNoteService(db, _mapper.Object, TimeProvider.System);

        Assert.False(await sut.SetPinnedAsync(404, pinned: true));
    }
}
