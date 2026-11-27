using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public class PricingCalculator
{
    public const decimal FreeShippingThreshold = 500m;

    public const decimal MaxBulkDiscountPercent = 25m;

    public decimal CalculateLineTotal(OrderLine line)
    {
        var gross = line.UnitPrice * line.Quantity;
        return Math.Round(gross - line.Discount, 2, MidpointRounding.AwayFromZero);
    }

    public decimal CalculateOrderTotal(Order order)
    {
        var lines = order.Lines.Sum(CalculateLineTotal);
        var shipping = lines >= FreeShippingThreshold ? 0m : order.ShippingCost;
        return lines + shipping;
    }

    public void ApplyBulkDiscount(Order order, decimal percent)
    {
        var capped = Math.Clamp(percent, 0m, MaxBulkDiscountPercent);
        foreach (var line in order.Lines)
        {
            var gross = line.UnitPrice * line.Quantity;
            line.Discount = Math.Round(gross * capped / 100m, 2, MidpointRounding.AwayFromZero);
        }
    }
}
