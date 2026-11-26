using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public class BankTransferPaymentGateway : IPaymentGateway
{
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

        var reference = $"BT-{order.Number}";
        return Task.FromResult(new PaymentResult(true, reference, null));
    }
}
