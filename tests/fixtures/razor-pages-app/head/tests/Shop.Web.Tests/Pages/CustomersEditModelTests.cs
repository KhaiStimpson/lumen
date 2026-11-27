using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Shop.Web.Data;
using Shop.Web.Data.Entities;
using Shop.Web.Pages.Customers;
using Shop.Web.Services;

namespace Shop.Web.Tests.Pages;

public class CustomersEditModelTests
{
    private readonly Mock<ICustomerService> _customerService = new();

    private static CustomerNoteService CreateNoteService()
    {
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var mapper = new Mock<IMapper>();
        return new CustomerNoteService(new ShopDbContext(options), mapper.Object, TimeProvider.System);
    }

    [Fact]
    public async Task OnPostAsync_copies_input_onto_customer()
    {
        var customer = new Customer { Id = 4, Name = "Old name", Role = "Customer" };
        var input = new EditModel.InputModel
        {
            Id = 4,
            Name = "Northwind Traders",
            Email = "orders@northwind.example",
            Bio = "Wholesale, net 30",
        };

        _customerService.Setup(s => s.GetAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(customer);
        var page = new EditModel(_customerService.Object, CreateNoteService()) { Input = input };

        var result = await page.OnPostAsync(4, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("Northwind Traders", customer.Name);
        Assert.Equal("orders@northwind.example", customer.Email);
        _customerService.Verify(s => s.UpdateAsync(customer, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnPostAddNoteAsync_rejects_empty_note()
    {
        var page = new EditModel(_customerService.Object, CreateNoteService());

        var result = await page.OnPostAddNoteAsync(4, "   ", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
