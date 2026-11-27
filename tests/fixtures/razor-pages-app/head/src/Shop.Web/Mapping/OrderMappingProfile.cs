using AutoMapper;
using Shop.Web.Data.Entities;
using Shop.Web.Models.Orders;

namespace Shop.Web.Mapping;

public class OrderMappingProfile : Profile
{
    public OrderMappingProfile()
    {
        CreateMap<Order, OrderSummaryDto>()
            .ForMember(d => d.CustomerName, o => o.Ignore());
        CreateMap<OrderSummaryDto, Order>();
    }
}
