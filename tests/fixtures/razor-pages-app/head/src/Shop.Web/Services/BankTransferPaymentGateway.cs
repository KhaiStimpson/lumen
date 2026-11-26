using System.Globalization;
using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public class BankTransferPaymentGateway : IPaymentGateway
{
    private const int MaxReferenceLength = 18;

    private readonly IConfiguration _configuration;

    public BankTransferPaymentGateway(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Name => "Bank transfer";

    public Task<PaymentResult> ChargeAsync(Order order, CancellationToken cancellationToken = default)
    {
        var account = _configuration["Payments:BankTransferAccount"];
        if (string.IsNullOrEmpty(account))
        {
            return Task.FromResult(new PaymentResult(false, null, "No bank account configured"));
        }

        var reference = BuildReference(order);
        return Task.FromResult(new PaymentResult(true, reference, null));
    }

    // Banks truncate references at 18 characters, so keep the order number and a short date stamp only.
    private static string BuildReference(Order order)
    {
        var stamp = DateTime.UtcNow.ToString("MMdd", CultureInfo.InvariantCulture);
        var reference = $"BT-{order.Number}-{stamp}";
        return reference.Length <= MaxReferenceLength ? reference : reference[..MaxReferenceLength];
    }
}
