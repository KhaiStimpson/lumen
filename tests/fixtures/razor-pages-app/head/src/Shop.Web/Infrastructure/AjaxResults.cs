using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Shop.Web.Infrastructure;

/// <summary>
/// Consistent JSON envelopes for page handlers called from jQuery.
/// </summary>
public static class AjaxResults
{
    public static JsonResult Ok(object? data = null, string? message = null)
    {
        return new JsonResult(new { ok = true, message, data });
    }

    public static JsonResult Fail(string message, int statusCode = StatusCodes.Status400BadRequest)
    {
        return new JsonResult(new { ok = false, message }) { StatusCode = statusCode };
    }

    public static JsonResult Invalid(ModelStateDictionary modelState)
    {
        var errors = modelState
            .Where(e => e.Value is { Errors.Count: > 0 })
            .ToDictionary(
                e => e.Key,
                e => e.Value!.Errors.Select(x => x.ErrorMessage).ToArray());

        return new JsonResult(new { ok = false, message = "Please correct the highlighted fields.", errors })
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
        };
    }
}
