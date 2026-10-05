using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.CDK;
using Amazon.CDK.Assertions;
using Xunit;

namespace IdentityCenter.Cdk.Tests;

/// <summary>
/// Synthesises the app once and exposes each stack's template as JSON. Shared through one
/// collection because the jsii runtime does not survive two apps being built concurrently.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TemplateCollection : ICollectionFixture<TemplateFixture>
{
    public const string Name = "Templates";
}

public sealed class TemplateFixture
{
    public TemplateFixture()
    {
        var (identityCenter, deployRoles, pipeline) = Program.Build(new App());
        IdentityCenter = ToJson(identityCenter);
        DeployRoles = ToJson(deployRoles);
        Pipeline = ToJson(pipeline);
    }

    public JsonObject IdentityCenter { get; }

    public JsonObject DeployRoles { get; }

    public JsonObject Pipeline { get; }

    private static JsonObject ToJson(Stack stack)
        => JsonNode.Parse(JsonSerializer.Serialize(Template.FromStack(stack).ToJSON()))!.AsObject();
}

internal static class TemplateExtensions
{
    public static IEnumerable<(string LogicalId, JsonObject Resource)> OfType(this JsonObject template, string type)
        => template["Resources"]!.AsObject()
            .Where(r => r.Value!["Type"]!.GetValue<string>() == type)
            .Select(r => (r.Key, r.Value!.AsObject()));

    public static JsonNode Props(this JsonObject resource) => resource["Properties"]!;

    /// <summary>The logical id a { "Fn::GetAtt": [id, attr] } points at.</summary>
    public static string GetAttTarget(this JsonNode node) => node["Fn::GetAtt"]![0]!.GetValue<string>();

    /// <summary>Every string anywhere under the node.</summary>
    public static IEnumerable<string> Strings(this JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => p.Value.Strings()),
        JsonArray a => a.SelectMany(i => i.Strings()),
        JsonValue v when v.TryGetValue<string>(out var s) => new[] { s },
        _ => Array.Empty<string>(),
    };
}
