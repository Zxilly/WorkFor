using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using WorkFor.Core;

namespace WorkFor.ScriptHost;

public sealed record RuleManifest(int ManifestVersion, int ApiVersion, string Id, string Name, string Version, string Entry, JsonElement ParametersSchema, string? Description = null);

/// <summary>Run only inside a disposable ScriptHost protected by the Host's external deadline.</summary>
public static partial class RulePackageValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static EvaluationError? Validate(JsonElement manifestJson, string source, JsonElement parameters)
    {
        try
        {
            ContextValidator.ValidateJson(manifestJson);
            ContextValidator.ValidateJson(parameters);
            if (Encoding.UTF8.GetByteCount(manifestJson.GetRawText()) + Encoding.UTF8.GetByteCount(source) > 96 * 1024 || Encoding.UTF8.GetByteCount(source) > 64 * 1024) return new("MANIFEST_INVALID", "Rule package exceeds size limits.");
            var manifest = manifestJson.Deserialize<RuleManifest>(WorkForJson.Options) ?? throw new JsonException();
            if (manifest.ManifestVersion != 1 || manifest.ApiVersion != 1) return new("API_VERSION_UNSUPPORTED", "Only manifest and script API version 1 are supported.");
            if (manifest.Id is null || !IdPattern().IsMatch(manifest.Id) || manifest.Name is null || manifest.Name.Length is < 1 or > 40 || manifest.Version is null || manifest.Version.Length > 80 || !VersionPattern().IsMatch(manifest.Version) || manifest.Entry != "index.mjs" || manifest.Description?.Length > 200) return new("MANIFEST_INVALID", "Invalid rule metadata.");
            var schema = manifest.ParametersSchema;
            if (schema.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False) || Encoding.UTF8.GetByteCount(schema.GetRawText()) > 32 * 1024) return new("MANIFEST_INVALID", "Invalid parameter schema.");
            ValidateSchemaPolicy(schema, schema, new HashSet<string>(StringComparer.Ordinal), 0);
            var registry = new SchemaRegistry { Fetch = (_, _) => throw new InvalidOperationException("External schema loading is disabled.") };
            var compiled = JsonSchema.Build(schema, new BuildOptions { SchemaRegistry = registry });
            if (!compiled.Evaluate(parameters).IsValid) return new("PARAMETER_INVALID", "Parameters do not satisfy the rule schema.");
            return null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return new("MANIFEST_INVALID", "Invalid or disallowed rule schema."); }
        catch (Exception) { return new("MANIFEST_INVALID", "Rule validation failed."); }
    }

    private static void ValidateSchemaPolicy(JsonElement item, JsonElement root, HashSet<string> activeRefs, int depth)
    {
        if (depth > 16) throw new ArgumentException("Schema reference depth exceeded.");
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name is "$id" or "$dynamicRef" or "$recursiveRef" or "$anchor" or "$dynamicAnchor") throw new ArgumentException("Schema URI remapping is not allowed.");
                if (property.Name == "$schema" && property.Value.GetString() != "https://json-schema.org/draft/2020-12/schema") throw new ArgumentException("Unsupported dialect.");
                if (property.Name == "$ref")
                {
                    var reference = property.Value.GetString() ?? "";
                    if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal) || !activeRefs.Add(reference)) throw new ArgumentException("Only acyclic local definition references are allowed.");
                    var target = root;
                    foreach (var part in reference[2..].Split('/')) target = target.GetProperty(part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
                    ValidateSchemaPolicy(target, root, activeRefs, depth + 1);
                    activeRefs.Remove(reference);
                }
                else ValidateSchemaPolicy(property.Value, root, activeRefs, depth + 1);
            }
        }
        else if (item.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in item.EnumerateArray()) ValidateSchemaPolicy(value, root, activeRefs, depth + 1);
        }
    }
}
