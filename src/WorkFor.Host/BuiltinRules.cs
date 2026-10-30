using System.Text.Json.Nodes;

namespace WorkFor.Host;

internal static class BuiltinRules
{
    public static JsonObject Catalog()
    {
        var rules = new JsonArray();
        foreach (var key in new[] { "hourly", "monthly" })
        {
            var manifest = JsonNode.Parse(Read($"WorkFor.Builtin.{key}.Manifest"))!.AsObject();
            var parameters = new JsonObject();
            if (manifest["parametersSchema"]?["properties"] is JsonObject properties)
                foreach (var property in properties)
                    if (property.Value?["default"] is { } value) parameters[property.Key] = value.DeepClone();
            rules.Add(new JsonObject
            {
                ["id"] = manifest["id"]!.DeepClone(), ["name"] = manifest["name"]!.DeepClone(),
                ["source"] = Read($"WorkFor.Builtin.{key}.Source"),
                ["manifest"] = manifest, ["defaultParameters"] = parameters
            });
        }
        return new() { ["rules"] = rules };
    }

    private static string Read(string resource)
    {
        using var stream = typeof(BuiltinRules).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("内置规则资源缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
