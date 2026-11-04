using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc.Razor;

namespace Commonage.Web.Forms;

public static class FieldErrorIds
{
    public static string For(string fieldName) => TagBuilder.CreateSanitizedId(fieldName, "_") + "-error";
}

/// <summary>Links a field to its error message, so a screen reader reads the message when the field gets focus.</summary>
[HtmlTargetElement("input", Attributes = "asp-for")]
[HtmlTargetElement("textarea", Attributes = "asp-for")]
[HtmlTargetElement("select", Attributes = "asp-for")]
public class FieldErrorLinkTagHelper : TagHelper
{
    [HtmlAttributeName("asp-for")]
    public ModelExpression For { get; set; } = null!;

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    // After ASP.NET's own tag helpers, which write the field's name and id.
    public override int Order => 1000;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var name = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        if (!ViewContext.ViewData.ModelState.TryGetValue(name, out var entry) || entry.Errors.Count == 0)
            return;

        var help = output.Attributes["aria-describedby"]?.Value?.ToString();
        output.Attributes.SetAttribute("aria-describedby", string.IsNullOrEmpty(help) ? FieldErrorIds.For(name) : $"{help} {FieldErrorIds.For(name)}");
        output.Attributes.SetAttribute("aria-invalid", "true");
    }
}

/// <summary>Gives a field's error message the id that <see cref="FieldErrorLinkTagHelper"/> and site.js link to.</summary>
[HtmlTargetElement("span", Attributes = "asp-validation-for")]
public class FieldErrorIdTagHelper : TagHelper
{
    [HtmlAttributeName("asp-validation-for")]
    public ModelExpression For { get; set; } = null!;

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output) =>
        output.Attributes.SetAttribute("id", FieldErrorIds.For(ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name)));
}

/// <summary>Tells a person how a date they are typing will be understood, before they send the form.</summary>
public static class DateCheck
{
    public const string Path = "/date-check";

    public static void MapDateCheck(this WebApplication app) =>
        app.MapGet(Path, (string? text, IStringLocalizer<SharedResource> localizer) =>
        {
            var understood = text is { Length: <= Domain.DateRange.MaxTextLength } && Domain.DateRange.TryParse(text, out var range);
            if (!understood)
                return Results.Json(new
                {
                    understood = false,
                    message = localizer["This date could not be understood. Please include a year, for example: 1953, 12.03.1953, circa 1950, 1950s, 1950–1955."].Value,
                });

            Domain.DateRange.TryParse(text, out var parsed);
            return Results.Json(new { understood = true, message = localizer["Understood as: {0}", parsed].Value });
        });
}
