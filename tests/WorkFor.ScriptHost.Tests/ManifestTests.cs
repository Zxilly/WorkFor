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
}
