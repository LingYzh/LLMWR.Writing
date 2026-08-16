using System.Text.Json;

namespace LLMW.Writing.Domain.Prompt;

public static class OutputSchemaSubset
{
    private static readonly HashSet<string> AllowedKeywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "items", "enum", "additionalProperties",
        "description", "title", "default", "nullable", "$schema", "$id", "$comment"
    };

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "object", "string", "number", "integer", "boolean", "array", "null"
    };

    public static bool TryValidateSchema(string? schemaJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            error = "schema-missing";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            return TryValidateNode(document.RootElement, "#", out error);
        }
        catch (JsonException)
        {
            error = "schema-malformed";
            return false;
        }
    }

    public static bool TryValidateInstance(string? json, string schemaJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "json-missing";
            return false;
        }

        if (!TryValidateSchema(schemaJson, out error))
        {
            return false;
        }

        try
        {
            using var instance = JsonDocument.Parse(json);
            using var schema = JsonDocument.Parse(schemaJson);
            return Match(instance.RootElement, schema.RootElement, "#", out error);
        }
        catch (JsonException)
        {
            error = "json-malformed";
            return false;
        }
    }

    private static bool TryValidateNode(JsonElement node, string path, out string? error)
    {
        error = null;
        if (node.ValueKind != JsonValueKind.Object)
        {
            error = "schema-not-object:" + path;
            return false;
        }

        foreach (var property in node.EnumerateObject())
        {
            if (!AllowedKeywords.Contains(property.Name))
            {
                error = "unsupported-keyword:" + property.Name;
                return false;
            }
        }

        if (node.TryGetProperty("type", out var typeEl))
        {
            if (!TypesAllowed(typeEl, out error))
            {
                return false;
            }
        }

        if (node.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                error = "properties-not-object:" + path;
                return false;
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (!TryValidateNode(property.Value, path + "/" + property.Name, out error))
                {
                    return false;
                }
            }
        }

        if (node.TryGetProperty("items", out var items) &&
            !TryValidateNode(items, path + "/items", out error))
        {
            return false;
        }

        if (node.TryGetProperty("required", out var required) && required.ValueKind != JsonValueKind.Array)
        {
            error = "required-not-array:" + path;
            return false;
        }

        if (node.TryGetProperty("enum", out var enums) && enums.ValueKind != JsonValueKind.Array)
        {
            error = "enum-not-array:" + path;
            return false;
        }

        if (node.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            error = "additionalProperties-not-boolean:" + path;
            return false;
        }

        return true;
    }

    private static bool TypesAllowed(JsonElement typeEl, out string? error)
    {
        error = null;
        if (typeEl.ValueKind == JsonValueKind.String)
        {
            if (!AllowedTypes.Contains(typeEl.GetString() ?? ""))
            {
                error = "unsupported-type:" + typeEl.GetString();
                return false;
            }

            return true;
        }

        if (typeEl.ValueKind != JsonValueKind.Array)
        {
            error = "type-not-string";
            return false;
        }

        foreach (var item in typeEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !AllowedTypes.Contains(item.GetString() ?? ""))
            {
                error = "unsupported-type:" + item.GetRawText();
                return false;
            }
        }

        return true;
    }

    private static bool Match(JsonElement instance, JsonElement schema, string path, out string? error)
    {
        error = null;
        var nullable = schema.TryGetProperty("nullable", out var nullableEl) && nullableEl.ValueKind == JsonValueKind.True;
        if (instance.ValueKind == JsonValueKind.Null)
        {
            if (nullable || TypeAllowsNull(schema))
            {
                return true;
            }

            error = "null-not-allowed:" + path;
            return false;
        }

        if (schema.TryGetProperty("enum", out var enums))
        {
            var raw = instance.GetRawText();
            var found = false;
            foreach (var item in enums.EnumerateArray())
            {
                if (string.Equals(item.GetRawText(), raw, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                error = "enum-mismatch:" + path;
                return false;
            }
        }

        if (!schema.TryGetProperty("type", out var typeEl))
        {
            return MatchObjectShape(instance, schema, path, out error);
        }

        if (!InstanceMatchesType(instance, typeEl, out error, path))
        {
            return false;
        }

        if (IsType(typeEl, "object") || instance.ValueKind == JsonValueKind.Object)
        {
            return MatchObjectShape(instance, schema, path, out error);
        }

        if (IsType(typeEl, "array") && instance.ValueKind == JsonValueKind.Array &&
            schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var element in instance.EnumerateArray())
            {
                if (!Match(element, items, path + "/" + index, out error))
                {
                    return false;
                }

                index++;
            }
        }

        return true;
    }

    private static bool MatchObjectShape(JsonElement instance, JsonElement schema, string path, out string? error)
    {
        error = null;
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray())
            {
                var key = name.GetString();
                if (key is not null && !instance.TryGetProperty(key, out _))
                {
                    error = "missing:" + key;
                    return false;
                }
            }
        }

        var additional = true;
        if (schema.TryGetProperty("additionalProperties", out var additionalEl) &&
            additionalEl.ValueKind == JsonValueKind.False)
        {
            additional = false;
        }

        JsonElement properties = default;
        var hasProperties = schema.TryGetProperty("properties", out properties);
        foreach (var property in instance.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var nested))
            {
                if (!Match(property.Value, nested, path + "/" + property.Name, out error))
                {
                    return false;
                }
            }
            else if (!additional)
            {
                error = "additional-property:" + property.Name;
                return false;
            }
        }

        return true;
    }

    private static bool TypeAllowsNull(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var typeEl))
        {
            return false;
        }

        if (typeEl.ValueKind == JsonValueKind.String)
        {
            return typeEl.GetString() == "null";
        }

        if (typeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeEl.EnumerateArray())
            {
                if (item.GetString() == "null")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool InstanceMatchesType(JsonElement instance, JsonElement typeEl, out string? error, string path)
    {
        error = null;
        if (TypeMatches(instance, typeEl))
        {
            return true;
        }

        error = "type-mismatch:" + path;
        return false;
    }

    private static bool TypeMatches(JsonElement instance, JsonElement typeEl)
    {
        if (typeEl.ValueKind == JsonValueKind.String)
        {
            return KindMatches(instance, typeEl.GetString());
        }

        if (typeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeEl.EnumerateArray())
            {
                if (KindMatches(instance, item.GetString()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsType(JsonElement typeEl, string expected)
    {
        if (typeEl.ValueKind == JsonValueKind.String)
        {
            return typeEl.GetString() == expected;
        }

        if (typeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeEl.EnumerateArray())
            {
                if (item.GetString() == expected)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool KindMatches(JsonElement instance, string? type) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "number" => instance.ValueKind is JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => false
    };
}
