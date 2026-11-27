using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public interface IPaymentGateway
{
    string Name { get; }

    Task<PaymentResult> ChargeAsync(Order order, CancellationToken cancellationToken = default);
}

public sealed record PaymentResult(bool Succeeded, string? Reference, string? Error);
