using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Platform.Api.Services.Email;

/// <summary>
/// Builds the text/plain alternative from our own HTML templates (multipart/alternative). Links keep their URL,
/// table rows become lines and the hidden preheader is dropped. Not a general HTML converter: our templates only.
/// </summary>
public static partial class EmailPlainText
{
    public static string FromHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var s = HeadOrStyle().Replace(html, string.Empty);
        s = Preheader().Replace(s, string.Empty);
        s = Comment().Replace(s, string.Empty);
        s = ImgAlt().Replace(s, string.Empty);
        s = Anchor().Replace(s, m =>
        {
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value).Trim();
            var text = WebUtility.HtmlDecode(Tag().Replace(m.Groups["text"].Value, string.Empty)).Trim();
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                return text;
            if (text.Length == 0 || string.Equals(text, href, StringComparison.Ordinal))
                return href;
            return $"{text}: {href}";
        });
        s = LineBreak().Replace(s, "\n");
        s = CellEnd().Replace(s, "  ");
        s = BlockEnd().Replace(s, "\n");
        s = Tag().Replace(s, string.Empty);
        s = WebUtility.HtmlDecode(s);
        // Zero-width/padding characters used by preheaders and button spacing.
        s = s.Replace("\u034F", string.Empty).Replace("\u200C", string.Empty).Replace('\u00A0', ' ');

        var sb = new StringBuilder();
        var blank = 0;
        foreach (var raw in s.Split('\n'))
        {
            var line = Spaces().Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                if (sb.Length > 0 && ++blank == 1)
                    sb.Append('\n');
                continue;
            }
            blank = 0;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().Trim() + "\n";
    }

    [GeneratedRegex(@"<(head|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadOrStyle();

    [GeneratedRegex(@"<div[^>]*data-preheader[^>]*>.*?</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Preheader();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgAlt();

    [GeneratedRegex(@"<a\b[^>]*?href=""(?<href>[^""]*)""[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEnd();

    [GeneratedRegex(@"</(p|div|tr|h[1-6]|li|table)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();
}
