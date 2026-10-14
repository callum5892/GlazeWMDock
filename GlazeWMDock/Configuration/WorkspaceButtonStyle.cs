using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GlazeWMDock.Configuration;

/// <summary>
/// A user's customisation of one workspace button, parsed from a line of the
/// "Custom workspace buttons" setting:
/// <code>
/// name = label | icon
/// </code>
/// Either side may be empty (<c>3 = Code</c>, <c>9 = | 🎵</c>). The icon is an
/// emoji / any literal text, or a Segoe Fluent glyph given as its hex code
/// point (<c>E774</c>, <c>U+E774</c>, <c></c>).
/// </summary>
internal sealed record WorkspaceButtonStyle(string Label, IconInfo? Icon)
{
    public static IReadOnlyDictionary<string, WorkspaceButtonStyle> ParseAll(string? text)
    {
        var result = new Dictionary<string, WorkspaceButtonStyle>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line.StartsWith('#') || eq <= 0)
            {
                continue;
            }

            var name = line[..eq].Trim();
            var rest = line[(eq + 1)..];
            var bar = rest.IndexOf('|', StringComparison.Ordinal);
            var label = (bar < 0 ? rest : rest[..bar]).Trim();
            var icon = bar < 0 ? null : ParseIcon(rest[(bar + 1)..].Trim());

            if (name.Length > 0 && (label.Length > 0 || icon is not null))
            {
                result[name] = new WorkspaceButtonStyle(label, icon);
            }
        }

        return result;
    }

    private static IconInfo? ParseIcon(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        var hex = value;
        if (hex.StartsWith("U+", StringComparison.OrdinalIgnoreCase) || hex.StartsWith("\\u", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        // Four or five hex digits in the Private Use Area is a Segoe Fluent /
        // MDL2 glyph code; anything else (emoji, a letter) is used verbatim.
        if (hex.Length is 4 or 5
            && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)
            && cp is >= 0xE000 and <= 0xF8FF)
        {
            return new IconInfo(char.ConvertFromUtf32(cp));
        }

        return new IconInfo(value);
    }
}
