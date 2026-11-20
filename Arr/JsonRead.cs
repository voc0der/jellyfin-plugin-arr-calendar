using System.Text.Json;

namespace Jellyfin.Plugin.ArrCalendar.Arr;

/// <summary>
/// Tolerant readers for Sonarr's and Radarr's JSON.
/// </summary>
/// <remarks>
/// A missing field, a null, or a value of the wrong type all read as absent. Upstream schemas
/// grow and occasionally rename a field's case (<c>sizeleft</c> versus <c>sizeLeft</c>), so names
/// match exactly first and case-insensitively second.
/// </remarks>
internal static class JsonRead
{
    public static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    public static string? String(JsonElement element, string name)
    {
        return TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static int? Int(JsonElement element, string name)
    {
        return TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    public static int? PositiveInt(JsonElement element, string name)
    {
        var number = Int(element, name);
        return number > 0 ? number : null;
    }

    public static double? Number(JsonElement element, string name)
    {
        return TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    public static bool Bool(JsonElement element, string name)
    {
        return TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    public static bool? NullableBool(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public static JsonElement? Object(JsonElement element, string name)
    {
        return TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
    }

    public static IEnumerable<JsonElement> Items(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        return array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object);
    }

    public static IEnumerable<string> Strings(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(text => !string.IsNullOrWhiteSpace(text));
    }
}
