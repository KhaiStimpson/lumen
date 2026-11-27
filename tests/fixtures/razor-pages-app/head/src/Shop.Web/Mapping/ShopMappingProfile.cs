using AutoMapper;
using Shop.Web.Data.Entities;
using Shop.Web.Models.Customers;
using Shop.Web.Models.Invoices;

namespace Shop.Web.Mapping;

public class ShopMappingProfile : Profile
{
    public ShopMappingProfile()
    {
        CreateMap<Invoice, InvoiceExportRow>();
        CreateMap<CustomerNote, CustomerNoteDto>();
        CreateMap<CustomerNoteDto, CustomerNote>();
    }
}
