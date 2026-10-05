using System.Text.Json.Nodes;
using Xunit;

namespace IdentityCenter.Cdk.Tests;

[Collection(TemplateCollection.Name)]
public sealed class DeploymentTests(TemplateFixture fixture)
{
    private JsonObject RoleNamed(string name) => fixture.DeployRoles.OfType("AWS::IAM::Role")
        .Single(r => r.Resource.Props()["RoleName"]!.GetValue<string>() == name).Resource;

    private IEnumerable<JsonNode> PolicyStatementsOf(string roleName)
    {
        var role = fixture.DeployRoles.OfType("AWS::IAM::Role")
            .Single(r => r.Resource.Props()["RoleName"]!.GetValue<string>() == roleName).LogicalId;
        return fixture.DeployRoles.OfType("AWS::IAM::Policy")
            .Where(p => p.Resource.Props()["Roles"]!.AsArray().Any(r => r!["Ref"]!.GetValue<string>() == role))
            .SelectMany(p => p.Resource.Props()["PolicyDocument"]!["Statement"]!.AsArray())!;
    }

    [Fact]
    public void Deploy_role_trusts_only_the_shared_account_pipeline_role()
    {
        var trust = RoleNamed(DeployNames.DeployActionRoleName).Props()["AssumeRolePolicyDocument"]!["Statement"]!.AsArray();

        var statement = Assert.Single(trust)!;
        Assert.Equal("arn:aws:iam::791686214595:role/identity-center-pipeline", statement["Principal"]!["AWS"]!.GetValue<string>());
    }

    [Fact]
    public void Deploy_role_can_only_touch_the_IdentityCenter_stack()
    {
        var cloudFormation = PolicyStatementsOf(DeployNames.DeployActionRoleName)
            .Single(s => s["Sid"]!.GetValue<string>() == "ChangeSetsOnIdentityCenterStack");

        Assert.Equal(
            "arn:aws:cloudformation:ap-southeast-2:406422318285:stack/IdentityCenter/*",
            cloudFormation["Resource"]!.GetValue<string>());
    }

    [Fact]
    public void Execution_role_can_never_assign_in_the_management_account()
    {
        var deny = PolicyStatementsOf(DeployNames.CloudFormationExecutionRoleName)
            .Single(s => s["Effect"]!.GetValue<string>() == "Deny");

        Assert.Equal("arn:aws:sso:::account/406422318285", deny["Resource"]!.GetValue<string>());
        Assert.Equivalent(new[] { "sso:CreateAccountAssignment", "sso:DeleteAccountAssignment" },
            deny["Action"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void Pipeline_creates_the_change_set_then_waits_for_approval_then_executes()
    {
        var pipeline = Assert.Single(fixture.Pipeline.OfType("AWS::CodePipeline::Pipeline")).Resource.Props();
        var deploy = pipeline["Stages"]!.AsArray().Single(s => s!["Name"]!.GetValue<string>() == "Deploy")!;

        var actions = deploy["Actions"]!.AsArray()
            .Select(a => (Name: a!["Name"]!.GetValue<string>(), Order: a["RunOrder"]!.GetValue<int>()))
            .OrderBy(a => a.Order)
            .ToArray();
        Assert.Equal(new[] { ("CreateChangeSet", 1), ("Approve", 2), ("ExecuteChangeSet", 3) }, actions);

        Assert.All(
            deploy["Actions"]!.AsArray().Where(a => a!["ActionTypeId"]!["Provider"]!.GetValue<string>() == "CloudFormation"),
            a => Assert.Equal("arn:aws:iam::406422318285:role/identity-center-pipeline-deploy", a!["RoleArn"]!.GetValue<string>()));
    }

    [Fact]
    public void Pipeline_runs_only_for_identity_center_changes_on_main()
    {
        var pipeline = Assert.Single(fixture.Pipeline.OfType("AWS::CodePipeline::Pipeline")).Resource.Props();
        var push = Assert.Single(pipeline["Triggers"]!.AsArray())!["GitConfiguration"]!["Push"]![0]!;

        Assert.Equal("main", push["Branches"]!["Includes"]![0]!.GetValue<string>());
        Assert.Equal("identity-center/**", push["FilePaths"]!["Includes"]![0]!.GetValue<string>());
    }
}
