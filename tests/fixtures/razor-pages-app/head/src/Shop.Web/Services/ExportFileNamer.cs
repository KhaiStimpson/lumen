using System.Globalization;

namespace Shop.Web.Services;

public class ExportFileNamer
{
    private readonly TimeProvider _clock;

    public ExportFileNamer()
        : this(TimeProvider.System)
    {
    }

    public ExportFileNamer(TimeProvider clock)
    {
        _clock = clock;
    }

    public string ForInvoices(DateOnly from, DateOnly to, string extension)
    {
        var stamp = _clock.GetUtcNow().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"invoices_{from:yyyyMMdd}-{to:yyyyMMdd}_{stamp}.{extension.TrimStart('.')}");
    }
}
