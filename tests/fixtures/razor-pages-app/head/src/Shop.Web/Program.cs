using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Shop.Web.Data;
using Shop.Web.Data.Repositories;
using Shop.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Orders");
    options.Conventions.AuthorizeFolder("/Customers");
    options.Conventions.AuthorizeAreaFolder("Admin", "/", "AdminOnly");
});

builder.Services.AddDbContext<ShopDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Shop")));

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.Cookie.Name = "ContosoShop.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.None;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("InvoiceExport", policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .WithMethods("GET", "POST"));
});

builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");
builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options => options.Cookie.IsEssential = true);
builder.Services.AddHttpClient("card-processor", client =>
    client.BaseAddress = new Uri(builder.Configuration["Payments:CardProcessorUrl"]!));
builder.Services.AddAutoMapper(typeof(Program));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<ProductRepository>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<PricingCalculator>();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<LegacyCsvImporter>();
builder.Services.AddScoped<IInvoiceExportService, InvoiceExportService>();
builder.Services.AddScoped<CustomerNoteService>();
builder.Services.AddSingleton<ExportFileNamer>();

builder.Services.AddScoped(sp => new DashboardService(
    sp.GetRequiredService<ShopDbContext>(),
    sp.GetRequiredService<ILogger<DashboardService>>(),
    builder.Configuration.GetValue("Dashboard:CacheSeconds", 60)));

if (builder.Configuration.GetValue<bool>("Payments:BankTransferEnabled"))
{
    builder.Services.AddScoped<IPaymentGateway, BankTransferPaymentGateway>();
}
else
{
    builder.Services.AddScoped<IPaymentGateway, CardPaymentGateway>();
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
