using System.Text.Json;
using Xunit;

namespace WorkFor.ScriptHost.Tests;

public class ManifestTests
{
    [Fact]
    public void BuiltinSchemaRejectsMissingAndAdditionalParameters()
    {
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"rules","builtin.hourly","rule.json")));
        Assert.Null(RulePackageValidator.Validate(manifest.RootElement,"export default()=>0;",JsonSerializer.SerializeToElement(new {hourlyRate="16.00"})));
        Assert.Equal("PARAMETER_INVALID",RulePackageValidator.Validate(manifest.RootElement,"export default()=>0;",JsonSerializer.SerializeToElement(new {hourlyRate="1.005"}))?.Code);
        Assert.Equal("PARAMETER_INVALID",RulePackageValidator.Validate(manifest.RootElement,"export default()=>0;",JsonSerializer.SerializeToElement(new {hourlyRate="16.00",extra=true}))?.Code);
        Assert.Equal("PARAMETER_INVALID",RulePackageValidator.Validate(manifest.RootElement,"export default()=>0;",JsonSerializer.SerializeToElement(new {}))?.Code);
    }

    [Theory]
    [InlineData("{\"$ref\":\"https://example.com/schema.json\"}")]
    [InlineData("{\"$defs\":{\"loop\":{\"$ref\":\"#/$defs/loop\"}},\"$ref\":\"#/$defs/loop\"}")]
    [InlineData("{\"$id\":\"file:///tmp/private\",\"type\":\"object\"}")]
    [InlineData("{\"$schema\":\"https://example.com/dialect\"}")]
    public void RejectsExternalAndCyclicSchemaReferences(string schema)
    {
        using var manifest=JsonDocument.Parse("{\"manifestVersion\":1,\"apiVersion\":1,\"id\":\"custom.test\",\"name\":\"Test\",\"version\":\"1.0.0\",\"entry\":\"index.mjs\",\"parametersSchema\":"+schema+"}");
        Assert.Equal("MANIFEST_INVALID",RulePackageValidator.Validate(manifest.RootElement,"export default()=>0;",JsonSerializer.SerializeToElement(new {}))?.Code);
    }

    [Fact]
    public void LocalDefinitionReferencesAreSupported()
    {
        using var manifest=JsonDocument.Parse("""{"manifestVersion":1,"apiVersion":1,"id":"custom.test","name":"Test","version":"1.0.0","entry":"index.mjs","parametersSchema":{"$defs":{"value":{"type":"string"}},"type":"object","properties":{"value":{"$ref":"#/$defs/value"}}}}""");
        Assert.Null(RulePackageValidator.Validate(manifest.RootElement,"export default()=>0;",JsonSerializer.SerializeToElement(new {value="ok"})));
        Assert.Equal("PARAMETER_INVALID",RulePackageValidator.Validate(manifest.RootElement,"export default()=>0;",JsonSerializer.SerializeToElement(new {value=1}))?.Code);
    }

    [Theory]
    [InlineData("{\"type\":\"not-a-json-type\"}")]
    [InlineData("{\"required\":\"name\"}")]
    [InlineData("{\"minimum\":\"1\"}")]
    [InlineData("{\"prefixItems\":[3]}")]
    public void InvalidSchemaKeywordsAreRejectedByTheStandardMetaschema(string schema)
        => Assert.Equal("MANIFEST_INVALID", Validate(schema, "{}")?.Code);

    [Fact]
    public void Draft202012TracksUnevaluatedPropertiesAcrossComposition()
    {
        const string schema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","allOf":[{"properties":{"name":{"type":"string"}}}],"unevaluatedProperties":false}""";
        Assert.Null(Validate(schema, """{"name":"valid"}"""));
        Assert.Equal("PARAMETER_INVALID", Validate(schema, """{"name":"valid","extra":true}""")?.Code);
    }

    [Fact]
    public void Draft202012PrefixItemsAndContainsApplyTheirActualSemantics()
    {
        const string schema = """{"type":"object","properties":{"tuple":{"type":"array","prefixItems":[{"type":"string"},{"type":"integer"}],"items":false},"values":{"type":"array","contains":{"type":"integer"},"minContains":2,"maxContains":2}}}""";
        Assert.Null(Validate(schema, """{"tuple":["x",2],"values":[1,"x",2]}"""));
        Assert.Equal("PARAMETER_INVALID", Validate(schema, """{"tuple":["x",2,true],"values":[1,2]}""")?.Code);
        Assert.Equal("PARAMETER_INVALID", Validate(schema, """{"tuple":["x",2],"values":[1,"x"]}""")?.Code);
        Assert.Equal("PARAMETER_INVALID", Validate(schema, """{"tuple":["x",2],"values":[1,2,3]}""")?.Code);
    }

    [Fact]
    public void ConditionalAndDependentSchemasAreEvaluated()
    {
        const string schema = """{"type":"object","properties":{"kind":{"enum":["employee","contractor"]},"company":{"type":"string"}},"if":{"properties":{"kind":{"const":"employee"}},"required":["kind"]},"then":{"required":["company"]},"else":{"not":{"required":["company"]}},"dependentSchemas":{"company":{"properties":{"company":{"minLength":2}}}}}""";
        Assert.Null(Validate(schema, """{"kind":"employee","company":"AB"}"""));
        Assert.Null(Validate(schema, """{"kind":"contractor"}"""));
        Assert.Equal("PARAMETER_INVALID", Validate(schema, """{"kind":"employee","company":"A"}""")?.Code);
        Assert.Equal("PARAMETER_INVALID", Validate(schema, """{"kind":"employee"}""")?.Code);
        Assert.Equal("PARAMETER_INVALID", Validate(schema, """{"kind":"contractor","company":"AB"}""")?.Code);
    }

    [Fact]
    public void SchemaLikeKeysInsideDataAreNotTreatedAsResolvers()
    {
        const string schema = """{"type":"object","properties":{"$id":{"type":"string"},"literal":{"const":{"$ref":"https://example.invalid/data-not-a-schema","$id":"literal"}}},"required":["$id","literal"],"default":{"$ref":"file:///not-a-schema"}}""";
        Assert.Null(Validate(schema, """{"$id":"user data","literal":{"$ref":"https://example.invalid/data-not-a-schema","$id":"literal"}}"""));
        Assert.Equal("PARAMETER_INVALID", Validate(schema, """{"$id":"user data","literal":{}}""")?.Code);
    }

    [Fact]
    public void FormatRemainsAnAnnotationInDraft202012()
        => Assert.Null(Validate("""{"type":"object","properties":{"email":{"type":"string","format":"email"}}}""", """{"email":"not an email"}"""));

    [Theory]
    [InlineData("{\"$defs\":{\"value\":{\"$ref\":\"file:///private.json\"}},\"type\":\"object\"}")]
    [InlineData("{\"dependentSchemas\":{\"x\":{\"$ref\":\"https://example.invalid/private\"}}}")]
    [InlineData("{\"properties\":{\"x\":{\"$dynamicRef\":\"#thing\"}}}")]
    [InlineData("{\"allOf\":[{\"$ref\":\"../relative.json\"}]}")]
    public void ExternalResolutionIsDeniedInAllSchemaLocations(string schema)
        => Assert.Equal("MANIFEST_INVALID", Validate(schema, "{}")?.Code);

    [Fact]
    public void LocalReferencesUseJsonPointerEscaping()
    {
        const string schema = """{"$defs":{"a/b~c":{"type":"integer"}},"properties":{"value":{"$ref":"#/$defs/a~1b~0c"}}}""";
        Assert.Null(Validate(schema, """{"value":2}"""));
        Assert.Equal("PARAMETER_INVALID", Validate(schema, """{"value":"two"}""")?.Code);
    }

    [Fact]
    public void RegexFailureIsBoundedAndDoesNotPoisonTheNextValidation()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var invalid = Validate("""{"properties":{"value":{"type":"string","pattern":"^(a|aa)+$"}}}""",
            JsonSerializer.Serialize(new { value = new string('a', 10000) + "!" }));
        Assert.NotNull(invalid);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Null(Validate("""{"properties":{"value":{"type":"integer"}}}""", """{"value":2}"""));
    }

    [Fact]
    public void BooleanSchemasAreSupported()
    {
        Assert.Null(Validate("true", "{}"));
        Assert.Equal("PARAMETER_INVALID", Validate("false", "{}")?.Code);
    }

    private static WorkFor.Core.EvaluationError? Validate(string schema, string parameters)
    {
        using var manifest = JsonDocument.Parse("{\"manifestVersion\":1,\"apiVersion\":1,\"id\":\"custom.test\",\"name\":\"Test\",\"version\":\"1.0.0\",\"entry\":\"index.mjs\",\"parametersSchema\":" + schema + "}");
        using var values = JsonDocument.Parse(parameters);
        return RulePackageValidator.Validate(manifest.RootElement, "export default()=>0;", values.RootElement);
    }
}
