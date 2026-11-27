using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Shop.Web.TagHelpers;

/// <summary>
/// Renders <c>&lt;currency-input asp-for="..." /&gt;</c> as a formatted money input and pulls in
/// <c>js/taghelpers/currency-input.js</c> once per request.
/// </summary>
[HtmlTargetElement("currency-input", TagStructure = TagStructure.WithoutEndTag)]
public class CurrencyInputTagHelper : TagHelper
{
    private const string ScriptRenderedKey = "__CurrencyInputScriptRendered";

    [HtmlAttributeName("asp-for")]
    public ModelExpression For { get; set; } = default!;

    [HtmlAttributeName("currency")]
    public string Currency { get; set; } = "GBP";

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var value = For.Model is decimal amount
            ? amount.ToString("0.00", CultureInfo.InvariantCulture)
            : string.Empty;

        output.TagName = "input";
        output.TagMode = TagMode.SelfClosing;
        output.Attributes.SetAttribute("type", "text");
        output.Attributes.SetAttribute("inputmode", "decimal");
        output.Attributes.SetAttribute("name", For.Name);
        output.Attributes.SetAttribute("id", TagBuilder.CreateSanitizedId(For.Name, "_"));
        output.Attributes.SetAttribute("value", value);
        output.Attributes.SetAttribute("data-currency", Currency);
        output.AddClass("form-control", HtmlEncoder.Default);
        output.AddClass("currency-input", HtmlEncoder.Default);

        var items = ViewContext.HttpContext.Items;
        if (!items.ContainsKey(ScriptRenderedKey))
        {
            items[ScriptRenderedKey] = true;
            output.PostElement.AppendHtml("<script src=\"/js/taghelpers/currency-input.js\"></script>");
        }
    }
}
