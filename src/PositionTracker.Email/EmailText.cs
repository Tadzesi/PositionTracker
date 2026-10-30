using System.Globalization;
using System.Text;

namespace PositionTracker.Email;

/// <summary>
/// Sanitising of user-supplied values for emails. Everything that can reach a header (display names, subject) or a
/// single line of a body goes through <see cref="SingleLine"/>; everything that goes into HTML goes through
/// <see cref="Html"/> afterwards.
/// </summary>
public static class EmailText
{
    public const int MaxNameLength = 100;
    public const int MaxSubjectLength = 150;
    public const int MaxValueLength = 120;
    public const int MaxAddressLength = 320;

    /// <summary>
    /// One line of at most <paramref name="maxLength"/> characters: control characters (CR, LF, TAB, NUL, ...) and the
    /// Unicode line/paragraph separators become spaces, invisible format characters (bidi overrides, zero-width) are
    /// removed, runs of white space collapse to one space, the ends are trimmed. A longer value is cut (never inside a
    /// surrogate pair) and ends with "…".
    /// </summary>
    public static string SingleLine(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            var category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Format) continue;
            if (char.IsControl(c) || char.IsWhiteSpace(c)
                || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
        }
        if (builder.Length <= maxLength) return builder.ToString();
        var cut = maxLength - 1;
        if (char.IsHighSurrogate(builder[cut - 1])) cut--;
        return builder.ToString(0, cut).TrimEnd() + "…";
    }

    /// <summary>
    /// HTML-escapes a value for element content and double- or single-quoted attributes (<c>&amp; &lt; &gt; " '</c>).
    /// Other characters, including Slovak letters, stay as they are (the document is UTF-8).
    /// </summary>
    public static string Html(string value)
    {
        if (value.AsSpan().IndexOfAny("&<>\"'") < 0) return value;
        var builder = new StringBuilder(value.Length + 16);
        foreach (var c in value)
            builder.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                (char)39 => "&#39;", // apostrophe
                _ => c.ToString(),
            });
        return builder.ToString();
    }

    /// <summary>
    /// A bare address <c>local@domain</c> without display name, white space, control characters, angle brackets or
    /// commas (so it cannot smuggle a second recipient or header), at most 320 characters.
    /// </summary>
    public static bool IsPlainAddress(string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > MaxAddressLength) return false;
        foreach (var c in address)
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or ',' or ';' or '"' or '(' or ')')
                return false;
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 && address.IndexOf('@') == at;
    }

    /// <summary>The domain part of an address in lower case (for logs), or <c>"(invalid)"</c>.</summary>
    public static string Domain(string? address) =>
        IsPlainAddress(address) ? address![(address!.LastIndexOf('@') + 1)..].ToLowerInvariant() : "(invalid)";
}
