using Shop.Web.Data.Entities;

namespace Shop.Web.Services;

public class CardPaymentGateway : IPaymentGateway
{
    private readonly HttpClient _http;
    private readonly ILogger<CardPaymentGateway> _logger;

    public CardPaymentGateway(IHttpClientFactory httpClientFactory, ILogger<CardPaymentGateway> logger)
    {
        _http = httpClientFactory.CreateClient("card-processor");
        _logger = logger;
    }

    public string Name => "Card";

    public async Task<PaymentResult> ChargeAsync(Order order, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
            "charges",
            new { reference = order.Number, amount = order.Total, currency = "GBP" },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Card charge for {OrderNumber} failed with {Status}", order.Number, response.StatusCode);
            return new PaymentResult(false, null, $"Card processor returned {(int)response.StatusCode}");
        }

        var reference = await response.Content.ReadAsStringAsync(cancellationToken);
        return new PaymentResult(true, reference, null);
    }
}
