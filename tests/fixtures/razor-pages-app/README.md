# razor-pages-app fixture

A synthetic .NET 10 Razor Pages + jQuery app ("Contoso Shop", an internal order desk) and one large AI-style
change on top of it. It is the Phase 0 fixture for the slicing, folding and red-flag work in
`docs/overhaul.md`, and `answer-key.json` is its ground truth.

## How base and head relate

- `base/` is the complete repository before the change (71 files).
- `head/` is the complete repository after the change (109 files). It is a full copy, not an overlay; no file
  was deleted, so every base file also exists in head.
- The change touches **75 files** with **+2,949 / −148 = 3,097 changed lines**, measured with
  `git -c core.autocrlf=false diff --no-index --numstat base head` from this folder. All files use LF.
- To make a PR-shaped or local-branch fixture, commit `base/` as the first commit and `head/` as the second
  (or check `head/` out as the working tree).

`answer-key.json` was written by hand from what was planted, applying the rules in `docs/overhaul.md`
("Feature slices", "Collapsing", "Red flags"). Every changed file appears exactly once across slice `files`,
`shared`, `unlinked.usedOutside`, `unlinked.nothingUses` and `mechanicalOnly`, except for files reached by
exactly two slices, which are listed in both. Line numbers are head-side for added lines and base-side for
removed lines.

## Features in the change

**Invoice export.** A new `Invoices/Export` page (`ExportModel`, `OnGet`, `OnPostPreviewAsync`,
`OnPostDownloadAsync`) with an `InvoiceExportRequest` bind model, an `IInvoiceExportService` with one
implementation (`InvoiceExportService`, using CsvHelper), an `ExportFileNamer` helper, an `InvoiceExportRow`
DTO, `InvoiceService.GetInvoicesForPeriodAsync`, a page script `invoices-export.js` that posts
`?handler=Preview`, and tests. `Invoices/Index` gets an `asp-page="./Export"` link. To "make the export
shareable" the change removes the `/Invoices` `AuthorizeFolder` convention and adds an allow-any-origin CORS
policy.

**Customer notes.** A `CustomerNote` entity, `DbSet`, query filter and EF migration; a concrete
`CustomerNoteService` (AutoMapper); a `CustomerNoteDto`; a `_CustomerNotes` partial with its own
`partials/customer-notes.js`; and new handlers on `Customers/Edit` (`OnGetNotesAsync`, `OnPostAddNoteAsync`,
`OnPostPinNoteAsync`). Along the way `Customers/Edit` gains a bindable `IsAdmin` checkbox and loses its
`ModelState.IsValid` check, and the notes are rendered with `@Html.Raw` and `.html(` concatenation.

**Order bulk edit.** A new `Orders/BulkEdit` page (`@page "{status?}"`) with a `BulkEditInput` bind model,
a `[BindProperty(SupportsGet = true)] Status`, `OnGetSummaryAsync` and `OnPostApplyAsync`; a page script
`orders-bulk-edit.js` that uses a newly vendored jQuery plugin (`wwwroot/lib/jquery-bulkselect/`, plus a
`libman.json` entry); `IOrderService.BulkUpdateStatusAsync`/`GetSummariesAsync`,
`IOrderRepository.GetByIdsAsync`, `PricingCalculator.ApplyBulkDiscount`, an `OrderSummaryDto`, and tests.

**Admin dashboard.** A new Area page `Areas/Admin/Pages/Dashboard` with a concrete `DashboardService`
(registered through a factory lambda), a `DashboardStats` DTO with a computed getter, two scripts pulled in by
`asp-src-include="~/js/pages/admin-dashboard*.js"`, and tests.

**Smaller edits.** `Orders/Edit` gets an `OnGetLinesAsync` handler and a "Refresh lines" button, and the
`_OrderLines` partial and its script gain a discount column and a `Shop.OrderLines.render` API.
`Orders/Checkout` gets an express-checkout handler, and the payment gateway is chosen by configuration.
`Admin/Products/Edit` gets an archive handler (`ProductRepository.ArchiveAsync`). A shared
`Infrastructure/AjaxResults` JSON helper is used by four page models. `site.js` gets new helpers and
`site.css` new styles. Two AutoMapper profiles are added, and so is an unused `SlugHelper`.

**Unrelated rewrite.** `Services/LegacyCsvImporter.cs` is rewritten from scratch on CsvHelper. Only the
unchanged `Areas/Admin/Pages/Import` page uses it, and no changed page reaches it.

**Provably mechanical changes.**
- Rename: `IOrderService.GetOrder` becomes `GetOrderAsync`. `Orders/Details.cshtml.cs` and
  `OrdersEditModelTests.cs` change only at the call sites; the declaration and other call sites sit in files
  with other changes.
- Move: `enum OrderStatus` moves unchanged from `Order.cs` to its own `OrderStatus.cs`.
- Formatting only: `CartService.cs` goes from K&R with 2-space indents to Allman with 4-space indents, with an
  identical token stream.
- Imports only: `CurrencyInputTagHelper.cs` drops an unused `using`.
- Comments only: `ICustomerService.cs` gains XML docs.
- Vendored: `jquery.bulkselect.js`, `jquery.bulkselect.min.js` and `libman.json`.
- Migration: `AddCustomerNotes` plus its Designer file and the model snapshot. The usual
  `#pragma warning disable 612, 618` was left out of the new Designer file on purpose, so generated code
  carries no flag.

## Planted red flags (18)

| Kind | Where |
|---|---|
| WeakenedTests | `OrderServiceTests.cs` head 42, `[Fact(Skip = ...)]` |
| AntiforgeryOff | `Orders/BulkEdit.cshtml.cs` head 11, `[IgnoreAntiforgeryToken]` |
| AntiforgeryOff | `orders-bulk-edit.js` head 37, `$.post` with an object literal and no token |
| AuthorisationLoosened | `Program.cs` base 13, `AuthorizeFolder("/Invoices")` removed |
| OverPosting | `Customers/Edit.cshtml.cs` head 105, new `IsAdmin` on the `[BindProperty]` InputModel |
| GuardRemoved | `Customers/Edit.cshtml.cs` base 50, `ModelState.IsValid` check removed |
| GuardRemoved | `OrderService.cs` base 31, null guard removed from `SaveAsync` |
| RawOutput | `_CustomerNotes.cshtml` head 12, `@Html.Raw(note.Body)` |
| RawOutput | `customer-notes.js` head 29, `.html(` built by concatenation |
| RawSql | `DashboardService.cs` head 70, `FromSqlRaw($"...{status}...")` |
| QuerySafety | `DashboardService.cs` head 40, `IgnoreQueryFilters()` |
| SwallowedErrors | `Orders/BulkEdit.cshtml.cs` head 64, empty `catch` |
| SwallowedErrors | `orders-bulk-edit.js` head 26, `.fail(function () {})` |
| WarningsSilenced | `DashboardService.cs` head 68, `#pragma warning disable CS8602` |
| DependenciesAndBuild | `Shop.Web.csproj` head 12, AutoMapper added |
| DependenciesAndBuild | `Shop.Web.csproj` head 13, CsvHelper added |
| SecurityConfig | `Program.cs` head 27, cookie `SameSite` from Strict to None |
| SecurityConfig | `Program.cs` head 37–43 and 91, allow-any-origin CORS policy |

## Near-misses

These must not be treated as their look-alike:

- **Duplicate names.** Three `EditModel` classes: `Pages/Orders`, `Pages/Customers` and
  `Areas/Admin/Pages/Products`. Each one changes and seeds its own slice. The tests `OrdersEditModelTests`
  and `CustomersEditModelTests` both say `new EditModel(...)` and are told apart only by their `using`.
- **Two implementations.** `IPaymentGateway` has two implementations (`CardPaymentGateway`,
  `BankTransferPaymentGateway`) and is injected into `Orders/Checkout`. The changed `BankTransferPaymentGateway`
  is only a possible link and lands in Unlinked (used outside).
- **URLs built from variables.**
  - `Url.Page(Model.DrilldownPage, ...)` on the dashboard and `Url.Page(Model.ReturnPage, ...)` on bulk edit.
  - The concatenated AJAX URL `'/Orders/' + ordersSection + '?handler=Summary'` in `admin-dashboard.js`.
  - `$.get($panel.data('url'))` in `customer-notes.js`.

  All four are possible links only.
- **Site-wide script.** `site.js` and `site.css` are loaded by the unchanged `_Layout`, and page scripts
  call `Shop.toast`, `Shop.debounce` and `Shop.antiforgeryToken`. They never join a page slice.
- **DTO with one initialiser.** `CustomerNoteDto` has `Author = "system"`, so it does not fold. Its sibling
  DTOs `InvoiceExportRow` and `OrderSummaryDto` do fold.
- **Mapping with one `Ignore`.** `OrderMappingProfile` has a `ForMember(... Ignore())` and does not fold.
  `ShopMappingProfile` has plain `CreateMap`s and does fold.
- **Guard converted to `?.`.** `CustomerService.GetDisplayName` now reads
  `return customer?.Name.Trim() ?? string.Empty;`. This is not GuardRemoved.
- **A catch that logs.** `Export.cshtml.cs` head 64 logs, and `LegacyCsvImporter.cs` head 124 logs and
  rethrows. Neither is SwallowedErrors.
- **Non-security appsettings.** The `appsettings.Development.json` edits (payment toggle, cache seconds,
  export row cap) are not SecurityConfig.
- **DI registrations that are not boilerplate.**
  - A registration inside `if/else` (`Program.cs` 71–78).
  - A registration with a lambda body (`Program.cs` 66–69).
  - The plain block at 62–64 does fold: 2 scoped, 1 singleton.
- **Test arrange with a mock `Setup`.** `InvoiceExportServiceTests` 70–78 and `BulkEditModelTests` 20–29 do
  not fold. The construction-only arranges at `InvoiceExportServiceTests` 34–51 and
  `CustomersEditModelTests` 28–35 do.
- **Not boilerplate despite looking like it.**
  - `[BindProperty(SupportsGet = true)] Status` on bulk edit.
  - The computed getter `DashboardStats.AverageOrderValue`.
  - The bind models `BulkEditInput` and `InvoiceExportRequest`, which look like validation-only DTOs.
  - The `CustomerNote` entity.
- **Mapping lookalikes.** `InvoiceExportService` lines 73–78 are plain `row.X = invoice.X` and fold. Lines
  80–84 right below them (`?.` chains, a computed value) and `OrderService.GetSummariesAsync`'s initialiser
  with `o.Customer?.Name` do not.
- **JS lookalikes.**
  - `$panel.html(markup)` is not concatenation.
  - `.append(cell(...))` and `$body.append($row)` append elements, not markup strings.
  - The notes POSTs send a `RequestVerificationToken` header.
  - The export preview POSTs a serialised form.

  None of these are flagged.
- **Shared threshold.** `AjaxResults`, `IOrderService` and `OrderService`, the order repository,
  `PricingCalculator`, `Order`, `OrderStatus`, `OrderSummaryDto`, `ShopDbContext` and `OrderServiceTests` are
  reached by three or more slices and go to Shared. `_OrderLines` with `order-lines.js`,
  `CurrencyInputTagHelper` and `InvoiceService` are each reached by exactly two slices and stay in both.
- **Unlinked.**
  - Used outside: `BankTransferPaymentGateway`, the rewritten `LegacyCsvImporter`, `site.js`, `site.css`.
  - Nothing uses it: `SlugHelper`, both AutoMapper profiles, plus `Program.cs`, the `.csproj` and
    `appsettings.Development.json`, which no code references.

`answer-key.json` also lists `debatableFlags` (bindable properties on brand-new pages, and `[EnableCors]` on a
PageModel), which should be left out of scoring, and `notes` recording where the doc's rules needed
interpreting.
