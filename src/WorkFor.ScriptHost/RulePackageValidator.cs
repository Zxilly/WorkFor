using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Corvus.Text.Json.RuntimeEvaluator;
using Corvus.Text.Json.Validator;
using WorkFor.Core;

namespace WorkFor.ScriptHost;

public sealed record RuleManifest(int ManifestVersion, int ApiVersion, string Id, string Name, string Version, string Entry, JsonElement ParametersSchema, string? Description = null);

/// <summary>Run only inside a disposable ScriptHost protected by the Host's external deadline.</summary>
public static partial class RulePackageValidator
{
    private static readonly JsonSchema.Options SchemaOptions = new(
        allowFileSystemAndHttpResolution: false,
        defaultDialect: JsonSchemaDialect.Draft202012,
        alwaysAssertFormat: false,
        codeGeneration: JsonSchemaCodeGeneration.Disabled,
        regexMatchTimeout: TimeSpan.FromMilliseconds(20));

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
            SchemaReferencePolicy.Validate(schema);
            var text = schema.GetRawText();
            // The standard metaschema is embedded in Corvus. Its retrieval does
            // not enable filesystem or HTTP resolution for user schemas.
            var metaschema = JsonSchema.FromUri("https://json-schema.org/draft/2020-12/schema", SchemaOptions);
            if (!metaschema.Validate(text)) return new("MANIFEST_INVALID", "Parameter schema is not a valid Draft 2020-12 schema.");
            var canonicalUri = "urn:workfor:parameter-schema:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var compiled = JsonSchema.FromText(text, canonicalUri, SchemaOptions);
            if (!compiled.Validate(parameters.GetRawText())) return new("PARAMETER_INVALID", "Parameters do not satisfy the rule schema.");
            return null;
        }
        catch (RegexMatchTimeoutException)
        { return new("MANIFEST_INVALID", "Schema regular expression exceeded its execution budget."); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return new("MANIFEST_INVALID", "Invalid or disallowed rule schema."); }
        catch (Exception) { return new("MANIFEST_INVALID", "Rule validation failed."); }
    }
}
