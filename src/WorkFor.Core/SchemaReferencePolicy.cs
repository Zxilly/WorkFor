using System.Globalization;
using System.Text.Json;

namespace WorkFor.Core;

/// <summary>Product import policy, shared by host metadata and isolated schema evaluation.</summary>
public static class SchemaReferencePolicy
{
    public static void Validate(JsonElement schema)
    {
        var remaining = 4096;
        Visit(schema, schema, new HashSet<string>(StringComparer.Ordinal), 0, ref remaining);
    }

    private static void Visit(JsonElement item, JsonElement root, HashSet<string> active, int depth, ref int remaining)
    {
        if (--remaining < 0 || depth > 16) throw new ArgumentException("Schema traversal budget exceeded.");
        if (item.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("A schema must be an object or boolean.");
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name is "$id" or "$dynamicRef" or "$recursiveRef" or "$anchor" or "$dynamicAnchor")
                throw new ArgumentException("Schema URI remapping is not allowed.");
            if (property.Name == "$schema" && property.Value.GetString() != "https://json-schema.org/draft/2020-12/schema")
                throw new ArgumentException("Unsupported schema dialect.");
            if (property.Name == "$ref")
            {
                var reference = property.Value.GetString() ?? "";
                if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal)) throw new ArgumentException("Only local definition references are allowed.");
                reference = Uri.UnescapeDataString(reference);
                if (!active.Add(reference)) throw new ArgumentException("Recursive schema references are not allowed.");
                try { Visit(Resolve(root, reference), root, active, depth + 1, ref remaining); }
                finally { active.Remove(reference); }
            }
            else if (property.Name is "$defs" or "definitions" or "properties" or "patternProperties" or "dependentSchemas")
            {
                if (property.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Schema map must be an object.");
                foreach (var entry in property.Value.EnumerateObject()) Visit(entry.Value, root, active, depth + 1, ref remaining);
            }
            else if (property.Name is "allOf" or "anyOf" or "oneOf" or "prefixItems")
            {
                if (property.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("Schema list must be an array.");
                foreach (var entry in property.Value.EnumerateArray()) Visit(entry, root, active, depth + 1, ref remaining);
            }
            else if (property.Name is "additionalProperties" or "unevaluatedProperties" or "items" or "unevaluatedItems"
                or "additionalItems" or "contains" or "propertyNames" or "not" or "if" or "then" or "else" or "contentSchema")
                Visit(property.Value, root, active, depth + 1, ref remaining);
            else if (property.Name == "dependencies" && property.Value.ValueKind == JsonValueKind.Object)
                foreach (var entry in property.Value.EnumerateObject())
                    if (entry.Value.ValueKind != JsonValueKind.Array) Visit(entry.Value, root, active, depth + 1, ref remaining);
        }
    }

    private static JsonElement Resolve(JsonElement root, string reference)
    {
        var target = root;
        foreach (var encoded in reference[2..].Split('/'))
        {
            for (var i = 0; i < encoded.Length; i++)
                if (encoded[i] == '~' && (++i >= encoded.Length || encoded[i] is not ('0' or '1')))
                    throw new ArgumentException("Invalid JSON pointer escape.");
            var part = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (target.ValueKind == JsonValueKind.Object) target = target.GetProperty(part);
            else if (target.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && (part == "0" || !part.StartsWith('0')) && index < target.GetArrayLength()) target = target[index];
            else throw new ArgumentException("Invalid schema reference target.");
        }
        return target;
    }
}
