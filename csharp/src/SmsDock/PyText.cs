using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmsDock;

/// <summary>
/// Writes JSON values the way the Python client does, so that frames, messages of errors and
/// the output of the command-line tool are the same in both.
/// </summary>
internal static class PyText
{
    /// <summary>Reads one JSON text. Returns <c>null</c> for the JSON value <c>null</c>.</summary>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    public static JsonNode? Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return ToNode(document.RootElement);
    }

    /// <summary>JSON without spaces, as it is sent to the server.</summary>
    public static string Compact(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteJson(builder, node, ",", ":", -1, 0);
        return builder.ToString();
    }

    /// <summary>JSON on one line, like <c>json.dumps(value, ensure_ascii=False)</c>.</summary>
    public static string Line(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteJson(builder, node, ", ", ": ", -1, 0);
        return builder.ToString();
    }

    /// <summary>JSON over several lines, like <c>json.dumps(value, indent=2, ensure_ascii=False)</c>.</summary>
    public static string Pretty(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteJson(builder, node, ",", ": ", 2, 0);
        return builder.ToString();
    }

    /// <summary>The text Python's <c>str()</c> gives for a JSON value.</summary>
    public static string Str(JsonNode? node)
    {
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return value.GetValue<string>();
        }
        return Repr(node);
    }

    /// <summary>The text Python's <c>repr()</c> gives for a JSON value.</summary>
    public static string Repr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "None";
            case JsonObject map:
                return "{" + string.Join(", ", map.Select(pair => Repr(pair.Key) + ": " + Repr(pair.Value))) + "}";
            case JsonArray list:
                return "[" + string.Join(", ", list.Select(Repr)) + "]";
            default:
                return node.GetValueKind() switch
                {
                    JsonValueKind.String => Repr(node.GetValue<string>()),
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Number => Number(node, json: false),
                    _ => "None",
                };
        }
    }

    /// <summary>The text Python's <c>repr()</c> gives for a string, or <c>None</c>.</summary>
    public static string Repr(string? text)
    {
        if (text is null)
        {
            return "None";
        }
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var builder = new StringBuilder(text.Length + 2).Append(quote);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == quote || character == '\\')
            {
                builder.Append('\\').Append(character);
            }
            else if (character == '\n')
            {
                builder.Append("\\n");
            }
            else if (character == '\r')
            {
                builder.Append("\\r");
            }
            else if (character == '\t')
            {
                builder.Append("\\t");
            }
            else if (char.IsHighSurrogate(character) && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                var rune = new Rune(character, text[index + 1]);
                index++;
                if (IsPrintable(Rune.GetUnicodeCategory(rune)))
                {
                    builder.Append(rune.ToString());
                }
                else
                {
                    builder.Append("\\U").Append(rune.Value.ToString("x8", CultureInfo.InvariantCulture));
                }
            }
            else if (character == ' ' || IsPrintable(char.GetUnicodeCategory(character)))
            {
                builder.Append(character);
            }
            else if (character < 0x100)
            {
                builder.Append("\\x").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
        }
        return builder.Append(quote).ToString();
    }

    /// <summary>Whether Python treats a JSON value as true: not null, false, 0 or empty.</summary>
    public static bool Truthy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonObject map:
                return map.Count > 0;
            case JsonArray list:
                return list.Count > 0;
            default:
                return node.GetValueKind() switch
                {
                    JsonValueKind.String => node.GetValue<string>().Length > 0,
                    JsonValueKind.True => true,
                    JsonValueKind.Number => AsDouble(node) is not 0.0,
                    _ => false,
                };
        }
    }

    /// <summary>The integer of a JSON number written without fraction and exponent.</summary>
    public static long? AsInteger(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        var raw = value.ToJsonString();
        return IsIntegerText(raw)
            && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>The value of a JSON number, integer or not.</summary>
    public static double? AsDouble(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        return double.TryParse(
            value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>The string of a JSON string, else <c>null</c>.</summary>
    public static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    /// <summary>Python's <c>repr()</c> of a float.</summary>
    public static string FloatRepr(double number)
    {
        if (double.IsNaN(number))
        {
            return "nan";
        }
        if (double.IsInfinity(number))
        {
            return number > 0 ? "inf" : "-inf";
        }
        var roundTrip = number.ToString("R", CultureInfo.InvariantCulture);
        var negative = roundTrip.StartsWith('-') || (number == 0 && double.IsNegative(number));
        var text = roundTrip.TrimStart('-');
        var exponent = 0;
        var marker = text.IndexOfAny(['E', 'e']);
        if (marker >= 0)
        {
            exponent = int.Parse(text[(marker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..marker];
        }
        var point = text.IndexOf('.');
        var digits = point < 0 ? text : text.Remove(point, 1);
        var position = (point < 0 ? text.Length : point) + exponent;
        var leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0');
        position -= leading;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            return negative ? "-0.0" : "0.0";
        }
        string body;
        var power = position - 1;
        if (power is >= -4 and < 16)
        {
            if (position <= 0)
            {
                body = "0." + new string('0', -position) + digits;
            }
            else if (position >= digits.Length)
            {
                body = digits + new string('0', position - digits.Length) + ".0";
            }
            else
            {
                body = digits[..position] + "." + digits[position..];
            }
        }
        else
        {
            body = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "")
                + "e" + (power < 0 ? "-" : "+")
                + Math.Abs(power).ToString("00", CultureInfo.InvariantCulture);
        }
        return (negative ? "-" : "") + body;
    }

    /// <summary>Whether Python's <c>str.isspace()</c> holds for a character.</summary>
    public static bool IsSpace(char character) =>
        char.IsWhiteSpace(character) || character is >= '\u001c' and <= '\u001f';

    private static JsonNode? ToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    // As in Python, the last value of a repeated key wins.
                    map[property.Name] = ToNode(property.Value);
                }
                return map;
            case JsonValueKind.Array:
                var list = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(ToNode(item));
                }
                return list;
            case JsonValueKind.String:
                return JsonValue.Create(element.GetString());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (IsIntegerText(raw) && element.TryGetInt64(out var number))
                {
                    return JsonValue.Create(number);
                }
                return JsonValue.Create(element.Clone());
            default:
                return null;
        }
    }

    private static void WriteJson(
        StringBuilder builder, JsonNode? node, string itemSeparator, string keySeparator, int indent, int level)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;
            case JsonObject map:
                if (map.Count == 0)
                {
                    builder.Append("{}");
                    return;
                }
                builder.Append('{');
                WriteItems(builder, map.Count, itemSeparator, indent, level, index =>
                {
                    var pair = map.ElementAt(index);
                    WriteString(builder, pair.Key);
                    builder.Append(keySeparator);
                    WriteJson(builder, pair.Value, itemSeparator, keySeparator, indent, level + 1);
                });
                builder.Append('}');
                return;
            case JsonArray list:
                if (list.Count == 0)
                {
                    builder.Append("[]");
                    return;
                }
                builder.Append('[');
                WriteItems(builder, list.Count, itemSeparator, indent, level, index =>
                    WriteJson(builder, list[index], itemSeparator, keySeparator, indent, level + 1));
                builder.Append(']');
                return;
            default:
                switch (node.GetValueKind())
                {
                    case JsonValueKind.String:
                        WriteString(builder, node.GetValue<string>());
                        break;
                    case JsonValueKind.True:
                        builder.Append("true");
                        break;
                    case JsonValueKind.False:
                        builder.Append("false");
                        break;
                    case JsonValueKind.Number:
                        builder.Append(Number(node, json: true));
                        break;
                    default:
                        builder.Append("null");
                        break;
                }
                return;
        }
    }

    private static void WriteItems(
        StringBuilder builder, int count, string itemSeparator, int indent, int level, Action<int> write)
    {
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(itemSeparator);
            }
            if (indent >= 0)
            {
                builder.Append('\n').Append(' ', indent * (level + 1));
            }
            write(index);
        }
        if (indent >= 0)
        {
            builder.Append('\n').Append(' ', indent * level);
        }
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var character in text)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('"');
    }

    private static string Number(JsonNode node, bool json)
    {
        var raw = node.ToJsonString();
        if (IsIntegerText(raw))
        {
            // Python reads "-0" as the integer 0.
            return raw.TrimStart('-').Trim('0').Length == 0 ? "0" : raw;
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return raw;
        }
        if (json && double.IsInfinity(number))
        {
            return number > 0 ? "Infinity" : "-Infinity";
        }
        return json && double.IsNaN(number) ? "NaN" : FloatRepr(number);
    }

    private static bool IsIntegerText(string raw)
    {
        var start = raw.StartsWith('-') ? 1 : 0;
        if (raw.Length == start)
        {
            return false;
        }
        for (var index = start; index < raw.Length; index++)
        {
            if (!char.IsAsciiDigit(raw[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsPrintable(UnicodeCategory category) => category is not (
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
        or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.SpaceSeparator);
}
