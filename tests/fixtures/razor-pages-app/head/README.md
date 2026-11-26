# Contoso Shop

Internal order desk for Contoso's trade customers. ASP.NET Core Razor Pages on .NET 10, EF Core on SQL Server,
and jQuery on the front end.

## Running

```
dotnet tool restore
libman restore --root src/Shop.Web
dotnet ef database update --project src/Shop.Web
dotnet run --project src/Shop.Web
```

## Layout

- `src/Shop.Web/Pages` — the order desk (orders, customers, invoices).
- `src/Shop.Web/Areas/Admin` — product catalogue and CSV import, admin role only.
- `src/Shop.Web/wwwroot/js` — `site.js` is loaded by the layout; `pages/` holds one script per page and
  `partials/` one script per partial.
- `tests/Shop.Web.Tests` — xUnit tests for services and page models.
