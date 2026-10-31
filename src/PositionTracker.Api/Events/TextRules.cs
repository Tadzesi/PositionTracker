using System.Text;

namespace PositionTracker.Api.Events;

/// <summary>
/// Single-line text entered by organizers (event names, locations, category, wave and checkpoint names). The value is
/// trimmed and then <b>rejected</b> (never silently stripped, consistent with organizer names in Task 4) when it is
/// empty, longer than the limit in Unicode scalar values, or contains a control character (C0, C1, so also CR, LF and
/// tab), a line or paragraph separator, or a bidirectional formatting character (U+061C, U+200E, U+200F,
/// U+202A..U+202E, U+2066..U+2069), which could reorder or hide text shown to others.
/// </summary>
public static class TextRules
{
    public static bool TryLine(string? input, int maxLength, out string value)
    {
        value = (input ?? "").Trim();
        if (value.Length == 0) return false;
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || IsForbiddenFormat(rune.Value)) return false;
            if (++count > maxLength) return false;
        }
        return true;
    }

    private static bool IsForbiddenFormat(int c) =>
        c is 0x2028 or 0x2029 or 0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);
}
