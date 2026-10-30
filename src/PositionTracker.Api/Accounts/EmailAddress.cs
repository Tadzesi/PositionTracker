using System.Globalization;
using System.Text;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// A conservative email address check: one bare <c>local@domain</c> address (no display name, comments, quoting,
/// white space, control characters or lists), at most 254 characters after trimming, both as entered and in its
/// ASCII form. The local part is dot-atom ASCII (at most 64 characters, no leading, trailing or double dots); the
/// domain is mapped to its IDNA ASCII form (NFKC, so fullwidth letters and full stops become ASCII; the ideographic
/// full stops <c>。 ｡</c> separate labels; then <see cref="IdnMapping"/>), which must have at least two labels of
/// letters, digits and hyphens, each 1–63 characters, not starting or ending with a hyphen. Unmappable domains are
/// rejected.
/// <para>The normalized form (lookup and uniqueness) is the lower-case local part, <c>@</c>, the lower-case ASCII
/// domain: <c>x@bücher.de</c>, <c>x@xn--bcher-kva.de</c>, <c>X@BÜCHER.de</c> and <c>x@ｂüｃｈｅｒ．ｄｅ</c> are one
/// mailbox. Plus-addresses (<c>x+tag@…</c>) stay distinct.</para>
/// </summary>
public static class EmailAddress
{
    public const int MaxLength = 254;
    private const int MaxLocalLength = 64;
    private const string LocalSpecials = "!#$%&'*+/=?^_`{|}~-";
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true, AllowUnassigned = false };

    /// <summary>
    /// Trims surrounding white space into <paramref name="address"/> (the display form) and computes
    /// <paramref name="normalized"/>; false when the input is not a single plain address.
    /// </summary>
    public static bool TryParse(string? input, out string address, out string normalized)
    {
        address = (input ?? "").Trim();
        normalized = "";
        if (address.Length is 0 or > MaxLength) return false;
        var at = address.IndexOf('@');
        if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1) return false;
        var local = address[..at];
        if (!IsLocalPart(local) || AsciiDomain(address[(at + 1)..]) is not { } domain) return false;
        normalized = local.ToLowerInvariant() + "@" + domain;
        return normalized.Length <= MaxLength;
    }

    private static bool IsLocalPart(string local)
    {
        if (local.Length > MaxLocalLength || local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var c in local)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || LocalSpecials.Contains(c)))
                return false;
        return true;
    }

    /// <summary>The lower-case IDNA ASCII form of <paramref name="domain"/>, or null.</summary>
    private static string? AsciiDomain(string domain)
    {
        foreach (var c in domain)
            if (char.IsControl(c) || char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                return null;
        string ascii;
        try
        {
            var mapped = domain.Normalize(NormalizationForm.FormKC).Replace('。', '.').Replace('｡', '.');
            ascii = Idn.GetAscii(mapped.ToLowerInvariant()).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
        var labels = ascii.Split('.');
        if (labels.Length < 2) return null;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return null;
            foreach (var c in label)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return null;
        }
        return labels[^1].All(char.IsAsciiDigit) ? null : ascii; // not an IP-like "1.2.3.4"
    }
}
