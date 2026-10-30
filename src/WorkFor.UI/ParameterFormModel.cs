using System.Text.Json.Nodes;

namespace WorkFor.UI;

internal enum ParameterFieldKind { String, Number, Integer, Boolean, Enumeration }

internal sealed record ParameterField(string Name, string Title, string Description, bool Required,
    ParameterFieldKind Kind, IReadOnlyList<JsonNode?> Choices, bool HasDefault, JsonNode? DefaultValue);

/// <summary>
/// Projects only schemas whose native form is unambiguous. The original parameter object is cloned,
/// including unknown keys. Rendering a field never creates a value or applies a JSON Schema default.
/// </summary>
internal sealed class ParameterFormModel
{
    private JsonObject _parameters;
    public IReadOnlyList<ParameterField> Fields { get; }

    private ParameterFormModel(IReadOnlyList<ParameterField> fields, JsonObject parameters)
    {
        Fields = fields;
        _parameters = parameters.DeepClone().AsObject();
    }

    public static bool TryCreate(JsonNode? schema, JsonObject parameters,
        out ParameterFormModel? model, out string reason)
    {
        model = null;
        reason = "此规则使用复杂参数定义，请使用参数 JSON 编辑。";
        if (schema is not JsonObject root || root["type"]?.ToString() != "object"
            || root["properties"] is not JsonObject properties || properties.Count > 24
            || HasUnsupportedStructure(root, true)) return false;
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (root["required"] is JsonArray requiredNames)
        {
            foreach (var name in requiredNames)
            {
                if (name is not JsonValue value || !value.TryGetValue<string>(out var text)) return false;
                required.Add(text);
            }
        }
        else if (root.ContainsKey("required")) return false;

        var fields = new List<ParameterField>();
        foreach (var (name, property) in properties)
        {
            if (property is not JsonObject definition || HasUnsupportedStructure(definition, false)) return false;
            var choices = new List<JsonNode?>();
            ParameterFieldKind kind;
            if (definition["enum"] is JsonArray options)
            {
                if (options.Count is < 1 or > 100 || options.Any(option => option is JsonObject or JsonArray)) return false;
                kind = ParameterFieldKind.Enumeration;
                choices.AddRange(options.Select(option => option?.DeepClone()));
            }
            else
            {
                kind = definition["type"]?.ToString() switch
                {
                    "string" => ParameterFieldKind.String,
                    "number" => ParameterFieldKind.Number,
                    "integer" => ParameterFieldKind.Integer,
                    "boolean" => ParameterFieldKind.Boolean,
                    _ => (ParameterFieldKind)(-1)
                };
                if ((int)kind == -1) return false;
            }
            var field = new ParameterField(name, Text(definition, "title", name),
                Text(definition, "description", ""), required.Contains(name), kind, choices,
                definition.ContainsKey("default"), definition["default"]?.DeepClone());
            if (parameters.TryGetPropertyValue(name, out var existing) && !CanRepresent(field, existing))
            {
                reason = $"参数“{field.Title}”的当前值需要保留原始 JSON，请在高级参数中修改。";
                return false;
            }
            if (field.HasDefault && !CanRepresent(field, field.DefaultValue)) return false;
            fields.Add(field);
        }
        // Required names without a declared field need the full JSON editor.
        if (required.Any(name => !properties.ContainsKey(name))) return false;
        model = new ParameterFormModel(fields, parameters);
        reason = "";
        return true;
    }

    public bool Contains(string name) => _parameters.ContainsKey(name);
    public JsonNode? Value(string name) => _parameters[name]?.DeepClone();
    public JsonObject Snapshot() => _parameters.DeepClone().AsObject();

    public void ReplaceParameters(JsonObject parameters)
    {
        foreach (var field in Fields)
            if (parameters.TryGetPropertyValue(field.Name, out var value) && !CanRepresent(field, value))
                throw new InvalidDataException($"参数“{field.Title}”需要使用高级 JSON 编辑。");
        _parameters = parameters.DeepClone().AsObject();
    }

    public void SetValue(string name, JsonNode? value)
    {
        var field = Field(name);
        if (!CanRepresent(field, value)) throw new InvalidDataException($"参数“{field.Title}”的值类型不正确。");
        _parameters[name] = value?.DeepClone();
    }

    public void Remove(string name)
    {
        _ = Field(name);
        _parameters.Remove(name);
    }

    public void UseDefault(string name)
    {
        var field = Field(name);
        if (!field.HasDefault) throw new InvalidOperationException("该参数未定义默认值。");
        SetValue(name, field.DefaultValue);
    }

    public JsonNode? InitialValue(string name)
    {
        var field = Field(name);
        if (field.HasDefault) return field.DefaultValue?.DeepClone();
        return field.Kind switch
        {
            ParameterFieldKind.Boolean => JsonValue.Create(false),
            ParameterFieldKind.Number or ParameterFieldKind.Integer => JsonValue.Create(0),
            ParameterFieldKind.Enumeration => field.Choices[0]?.DeepClone(),
            _ => JsonValue.Create("")
        };
    }

    private ParameterField Field(string name) => Fields.FirstOrDefault(field => field.Name == name)
        ?? throw new ArgumentException("Unknown parameter field.", nameof(name));

    public static string ChoiceLabel(JsonNode? value) => value is JsonValue text && text.TryGetValue<string>(out var label)
        ? (label.Length == 0 ? "（空字符串）" : label) : value?.ToJsonString() ?? "null";

    public static bool SameValue(JsonNode? left, JsonNode? right) => JsonNode.DeepEquals(left, right);

    private static bool CanRepresent(ParameterField field, JsonNode? value)
    {
        if (field.Kind == ParameterFieldKind.Enumeration) return field.Choices.Any(choice => SameValue(choice, value));
        if (value is not JsonValue scalar) return false;
        return field.Kind switch
        {
            ParameterFieldKind.String => scalar.TryGetValue<string>(out _),
            ParameterFieldKind.Boolean => scalar.TryGetValue<bool>(out _),
            // NumberBox uses IEEE doubles: keep values outside safe integer precision in the JSON editor.
            ParameterFieldKind.Number or ParameterFieldKind.Integer => TryNumber(scalar, out var number)
                && double.IsFinite(number) && Math.Abs(number) <= 9_007_199_254_740_991d,
            _ => false
        };
    }

    public static bool TryNumber(JsonNode? value, out double number) =>
        double.TryParse(value?.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out number);

    private static string Text(JsonObject value, string name, string fallback) =>
        value[name] is JsonValue text && text.TryGetValue<string>(out var content) ? content : fallback;

    private static bool HasUnsupportedStructure(JsonObject schema, bool root)
    {
        var unsupported = new[] { "$ref", "$dynamicRef", "allOf", "anyOf", "oneOf", "not", "if", "then", "else",
            "dependentRequired", "dependentSchemas", "patternProperties", "propertyNames", "unevaluatedProperties",
            "prefixItems", "items", "contains", "const" };
        if (unsupported.Any(schema.ContainsKey) || schema["type"] is JsonArray) return true;
        if (!root && (schema.ContainsKey("properties") || schema["type"]?.ToString() is "object" or "array")) return true;
        // A schema for additional keys cannot be represented by flat known fields.
        return root && schema["additionalProperties"] is JsonObject;
    }
}
