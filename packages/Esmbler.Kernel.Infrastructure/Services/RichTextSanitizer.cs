using Esmbler.Kernel.Records;
using Ganss.Xss;

namespace Esmbler.Kernel.Infrastructure.Services;

/// <summary>Allow-list HTML sanitizer for rich text and activity bodies (threat model: stored XSS).</summary>
public sealed class RichTextSanitizer : IRichTextSanitizer
{
    private readonly HtmlSanitizer _sanitizer;

    public RichTextSanitizer()
    {
        _sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(["p", "br", "strong", "b", "em", "i", "u", "s", "code", "pre", "blockquote", "ul", "ol", "li", "a", "h3", "h4"], StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(["href", "title"], StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(["https", "http", "mailto"], StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(["href"], StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        });
        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement { TagName: "A" } a)
            {
                a.SetAttribute("rel", "noopener noreferrer nofollow");
                a.SetAttribute("target", "_blank");
            }
        };
        _sanitizer.AllowedAttributes.Add("rel");
        _sanitizer.AllowedAttributes.Add("target");
    }

    public string Sanitize(string html) => _sanitizer.Sanitize(html);
}
