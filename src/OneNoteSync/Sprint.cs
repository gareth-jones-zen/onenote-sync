using System.Globalization;
using System.Text.RegularExpressions;
using PyCompat;

namespace OneNoteSync;

public static partial class Sprint
{
    [GeneratedRegex(@"Sprint[\s\x1c-\x1f]+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    /// <summary>A yyyy-mm-dd date. Python's date.fromisoformat also took a few other forms (20261109,
    /// 2026-W45-1); this takes only yyyy-MM-dd, which is all the tool ever writes or reads from a title.</summary>
    public static DateOnly ParseDate(string text)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new UsageException($"Invalid isoformat string: {Py.ReprString(text)}");
    }

    /// <summary>The sprint number for a new week's page, from the pages before it, and how it was worked out.
    ///
    /// Sprints run sprintWeeks weeks, so a sprint that has already had that many weekly pages is over and the new
    /// page is the next one; otherwise it's the same sprint. Throws UsageException if the earlier titles don't
    /// say, or the last page isn't the week before (a gap is refused, never guessed).</summary>
    public static (int Number, string Why) For(string date, IEnumerable<PageInfo> earlier, int sprintWeeks = 2)
    {
        var numbered = new List<(string Date, int Number)>();
        foreach (var page in earlier)
        {
            var match = Number().Match(page.Title);
            if (match.Success && string.CompareOrdinal(page.Date, date) < 0)
                numbered.Add((page.Date, checked((int)PyStr.Int(match.Groups[1].Value))));
        }
        if (numbered.Count == 0)
            throw new UsageException("No earlier page has a sprint number in its title, so the new one can't be worked out.");
        numbered.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date) is var c and not 0 ? c : a.Number.CompareTo(b.Number));
        var (lastDate, last) = numbered[^1];
        var run = Py.From(numbered, -sprintWeeks).Where(n => n.Number == last).ToList();
        var gap = ParseDate(date).DayNumber - ParseDate(lastDate).DayNumber;
        if (gap != 7)
            throw new UsageException($"The last page is {lastDate}, {gap} days before {date}, so the sprint can't be worked out "
                                     + "from it. Create the page by hand, or the missing week's first.");
        if (run.Count >= sprintWeeks)
            return (last + 1, $"{lastDate} was week {run.Count} of {sprintWeeks} of Sprint {last}, so this is Sprint {last + 1}");
        return (last, $"{lastDate} was week {run.Count} of {sprintWeeks} of Sprint {last}, so this is week {run.Count + 1}");
    }

    /// <summary>The title of a new week's page: "yyyy-mm-dd – Sprint N", with an en dash (U+2013) between spaces.</summary>
    public static string Title(string week, int number) => $"{week} – Sprint {number}";
}
