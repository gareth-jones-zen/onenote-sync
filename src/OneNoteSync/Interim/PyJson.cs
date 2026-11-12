// Interim: the parts of Python's json.dumps this tool writes (strings, and objects of strings), byte for byte.
// Belongs in background-tools-lib (ai-keep-awake has a fuller PyJson for JsonNode); delete once the library has it.
using System.Globalization;
using System.Text;

namespace OneNoteSync.Interim;

public static class PyJson
{
    /// <summary>json.dumps(text): with ensureAscii (Python's default) everything past U+007E is \uXXXX (lower-case
    /// hex, surrogate pairs as two escapes); without it, only what JSON requires is escaped. Either way \" \\ \n \r
    /// \t \b \f have their short forms and other control characters are \u00XX, as Python writes them.</summary>
    public static string String(string text, bool ensureAscii = true)
    {
        var result = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                default:
                    if (c < 0x20 || (ensureAscii && c > 0x7e))
                        result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        result.Append(c);
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    /// <summary>json.dumps(list of flat objects) with the default separators (", " and ": "), keys in order and
    /// values that are strings (or null): what onenote-sync sends as a PATCH body.</summary>
    public static string ListOfObjects(IEnumerable<IReadOnlyList<KeyValuePair<string, string?>>> items)
    {
        var result = new StringBuilder("[");
        var firstItem = true;
        foreach (var item in items)
        {
            if (!firstItem)
                result.Append(", ");
            firstItem = false;
            result.Append('{');
            for (var i = 0; i < item.Count; i++)
            {
                if (i > 0)
                    result.Append(", ");
                result.Append(String(item[i].Key)).Append(": ").Append(item[i].Value is { } value ? String(value) : "null");
            }
            result.Append('}');
        }
        return result.Append(']').ToString();
    }

    /// <summary>json.dumps(data, indent=2, ensure_ascii=False) for an object of objects of strings (an empty object
    /// is "{}", as Python writes it).</summary>
    public static string NestedIndented(IEnumerable<KeyValuePair<string, OrderedDictionary<string, string>>> data)
    {
        var outer = data.ToList();
        if (outer.Count == 0)
            return "{}";
        var result = new StringBuilder("{\n");
        for (var i = 0; i < outer.Count; i++)
        {
            result.Append("  ").Append(String(outer[i].Key, false)).Append(": ");
            var inner = outer[i].Value;
            if (inner.Count == 0)
                result.Append("{}");
            else
            {
                result.Append("{\n");
                var j = 0;
                foreach (var (key, value) in inner)
                {
                    result.Append("    ").Append(String(key, false)).Append(": ").Append(String(value, false));
                    result.Append(++j < inner.Count ? ",\n" : "\n");
                }
                result.Append("  }");
            }
            result.Append(i + 1 < outer.Count ? ",\n" : "\n");
        }
        return result.Append('}').ToString();
    }
}
