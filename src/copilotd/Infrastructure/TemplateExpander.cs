using System.Buffers;
using System.Text;

namespace Copilotd.Infrastructure;

internal delegate string? TemplateTokenResolver(ReadOnlySpan<char> token);

internal static class TemplateExpander
{
    private static readonly SearchValues<char> TokenStartMarkers = SearchValues.Create("$");

    public static string Expand(string template, TemplateTokenResolver resolveToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(resolveToken);

        StringBuilder? builder = null;
        var copyStart = 0;
        var scanStart = 0;

        while (scanStart < template.Length)
        {
            var tokenStartMarkerOffset = template.AsSpan(scanStart).IndexOfAny(TokenStartMarkers);
            if (tokenStartMarkerOffset < 0)
                break;

            var tokenStartMarker = scanStart + tokenStartMarkerOffset;
            var tokenStart = tokenStartMarker + 2;
            if (tokenStart > template.Length)
                break;

            if (template[tokenStartMarker + 1] != '(')
            {
                scanStart = tokenStartMarker + 1;
                continue;
            }

            var tokenEnd = template.IndexOf(')', tokenStart);
            if (tokenEnd < 0)
                break;

            var token = template.AsSpan(tokenStart, tokenEnd - tokenStart);
            var replacement = resolveToken(token);
            if (replacement is null)
            {
                if (builder is not null)
                {
                    builder.Append(template.AsSpan(copyStart, tokenStart - copyStart));
                    copyStart = tokenStart;
                }

                scanStart = tokenStart;
                continue;
            }

            builder ??= new StringBuilder(template.Length);
            builder.Append(template.AsSpan(copyStart, tokenStartMarker - copyStart));
            builder.Append(replacement);

            copyStart = tokenEnd + 1;
            scanStart = copyStart;
        }

        if (builder is null)
            return template;

        builder.Append(template.AsSpan(copyStart));
        return builder.ToString();
    }
}
