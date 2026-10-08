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
        Assert.Equal("sts:AssumeRole", statement["Action"]!.GetValue<string>());
        Assert.Contains(":iam::791686214595:root", statement["Principal"]!["AWS"].Strings().Single(x => x.Contains(":iam::")));
        Assert.Equal(
            "arn:aws:iam::791686214595:role/identity-center-pipeline",
            statement["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>());
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

    private JsonNode Statement(string roleName, string sid) => PolicyStatementsOf(roleName)
        .Single(s => s["Sid"]!.GetValue<string>() == sid);

    [Fact]
    public void Execution_role_can_never_assign_or_provision_in_the_management_account()
    {
        var deny = Statement(DeployNames.CloudFormationExecutionRoleName, "NeverGrantInManagementAccount");

        Assert.Equal("Deny", deny["Effect"]!.GetValue<string>());
        Assert.Equal("arn:aws:sso:::account/406422318285", deny["Resource"]!.GetValue<string>());
        Assert.Equivalent(new[] { "sso:CreateAccountAssignment", "sso:DeleteAccountAssignment", "sso:ProvisionPermissionSet" },
            deny["Action"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void Execution_role_can_never_create_change_or_delete_the_instance()
    {
        var deny = Statement(DeployNames.CloudFormationExecutionRoleName, "NeverChangeTheInstance");

        Assert.Equal("Deny", deny["Effect"]!.GetValue<string>());
        Assert.Equal("*", deny["Resource"]!.GetValue<string>());
        Assert.Equivalent(new[] { "sso:CreateInstance*", "sso:UpdateInstance*", "sso:DeleteInstance*" },
            deny["Action"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void Execution_role_allows_no_service_wide_wildcard_and_no_group_deletion()
    {
        var allowed = PolicyStatementsOf(DeployNames.CloudFormationExecutionRoleName)
            .Where(s => s["Effect"]!.GetValue<string>() == "Allow")
            .SelectMany(s => s["Action"]!.Strings())
            .ToArray();

        Assert.DoesNotContain(allowed, a => a.EndsWith(":*", StringComparison.Ordinal) || a == "*");
        Assert.DoesNotContain("identitystore:DeleteGroup", allowed);
    }

    [Fact]
    public void Execution_role_assigns_groups_to_the_AIOps_application_only()
    {
        var statement = Statement(DeployNames.CloudFormationExecutionRoleName, "AiopsApplicationAssignments");

        Assert.Equivalent(
            new[] { IdentityCenterInstance.InstanceArn, IdentityCenterInstance.AiopsSamlApplicationArn },
            statement["Resource"]!.Strings());
    }

    [Fact]
    public void Deploy_role_cannot_create_an_import_change_set()
    {
        var deny = Statement(DeployNames.DeployActionRoleName, "NoImportChangeSets");

        Assert.Equal("Deny", deny["Effect"]!.GetValue<string>());
        Assert.Equal("cloudformation:CreateChangeSet", deny["Action"]!.GetValue<string>());
        Assert.Equal("false", deny["Condition"]!["Null"]!["cloudformation:ImportResourceTypes"]!.GetValue<string>());
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

    // Change detection on the source action would start a run for every push to main, path filter or not.
    [Fact]
    public void Source_action_does_not_trigger_on_every_push()
    {
        var source = SourceAction();

        Assert.False(source["Configuration"]!["DetectChanges"]!.GetValue<bool>());
    }

    [Fact]
    public void Source_action_role_can_use_the_connection_under_its_renamed_prefix()
    {
        var roleLogicalId = SourceAction()["RoleArn"]!.GetAttTarget();

        var actions = fixture.Pipeline.OfType("AWS::IAM::Policy")
            .Where(p => p.Resource.Props()["Roles"]!.AsArray().Any(r => r!["Ref"]!.GetValue<string>() == roleLogicalId))
            .SelectMany(p => p.Resource.Props()["PolicyDocument"]!["Statement"]!.AsArray())
            .Where(s => s!["Resource"]!.Strings().Contains(DeployNames.GitHubConnectionArn))
            .SelectMany(s => s!["Action"]!.Strings());

        Assert.Contains("codeconnections:UseConnection", actions);
    }

    private JsonNode SourceAction()
    {
        var pipeline = Assert.Single(fixture.Pipeline.OfType("AWS::CodePipeline::Pipeline")).Resource.Props();
        var stage = pipeline["Stages"]!.AsArray().Single(s => s!["Name"]!.GetValue<string>() == "Source")!;
        return Assert.Single(stage["Actions"]!.AsArray())!;
    }
}
