using System.Globalization;

namespace PositionTracker.Api.Accounts;

/// <summary>
/// A conservative email address check: one bare <c>local@domain</c> address (no display name, comments, quoting,
/// white space, control characters or lists), at most 254 characters after trimming. The local part is dot-atom
/// ASCII (at most 64 characters, no leading, trailing or double dots); the domain has at least two labels of letters,
/// digits and hyphens (internationalized labels are checked in their punycode form), each 1–63 characters, not
/// starting or ending with a hyphen.
/// </summary>
public static class EmailAddress
{
    public const int MaxLength = 254;
    private const int MaxLocalLength = 64;
    private const string LocalSpecials = "!#$%&'*+/=?^_`{|}~-";
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };

    /// <summary>Trims surrounding white space; returns false when the rest is not a single plain address.</summary>
    public static bool TryParse(string? input, out string address)
    {
        address = (input ?? "").Trim();
        if (address.Length is 0 or > MaxLength) return false;
        var at = address.IndexOf('@');
        if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1) return false;
        return IsLocalPart(address[..at]) && IsDomain(address[(at + 1)..]);
    }

    /// <summary>The lookup and uniqueness form: trimmed, lower case (invariant).</summary>
    public static string Normalize(string address) => address.Trim().ToLowerInvariant();

    private static bool IsLocalPart(string local)
    {
        if (local.Length > MaxLocalLength || local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var c in local)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || LocalSpecials.Contains(c)))
                return false;
        return true;
    }

    private static bool IsDomain(string domain)
    {
        foreach (var c in domain)
            if (char.IsControl(c) || char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                return false;
        string ascii;
        try
        {
            ascii = Idn.GetAscii(domain);
        }
        catch (ArgumentException)
        {
            return false;
        }
        var labels = ascii.Split('.');
        if (labels.Length < 2) return false;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            foreach (var c in label)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return false;
        }
        return !labels[^1].All(char.IsAsciiDigit); // not an IP-like "1.2.3.4"
    }
}
