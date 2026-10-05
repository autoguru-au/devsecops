using System.Text.Json.Nodes;
using Xunit;

namespace IdentityCenter.Cdk.Tests;

[Collection(TemplateCollection.Name)]
public sealed class IdentityCenterStackTests(TemplateFixture fixture)
{
    private const string SharedAccount = "791686214595";

    private JsonObject Template => fixture.IdentityCenter;

    private Dictionary<string, string> GroupNamesByLogicalId => Template.OfType("AWS::IdentityStore::Group")
        .ToDictionary(g => g.LogicalId, g => g.Resource.Props()["DisplayName"]!.GetValue<string>());

    private string GroupLogicalId(string displayName) => GroupNamesByLogicalId.Single(g => g.Value == displayName).Key;

    [Fact]
    public void Declares_exactly_these_groups_and_not_AllStaff()
    {
        Assert.Equal(
            new[]
            {
                "AIOps-Mlflow-Production", "App-AIOps", "App-AIOps-Evaluate", "App-AIOps-Rater",
                "App-AIOps-Unblind", "App-TechLeadership",
            },
            GroupNamesByLogicalId.Values.Order());
    }

    // An import records the template as it is, so these must match the live groups character for
    // character or the difference stays as drift that no later deploy corrects.
    [Theory]
    [InlineData("App-AIOps", "AIOps portal access. Application access only, no AWS account permissions.")]
    [InlineData("App-TechLeadership", "Tech leadership cohort - application access only, no AWS account permissions")]
    [InlineData("AIOps-Mlflow-Production",
        "Open the AIOps production MLflow instance (FR-MLF-11). Tier 1 grant. Members are declared in autoguru-au/devsecops identity-center only, never added in the console.")]
    public void Imported_groups_match_the_live_groups(string displayName, string description)
    {
        var group = Template.OfType("AWS::IdentityStore::Group")
            .Single(g => g.Resource.Props()["DisplayName"]!.GetValue<string>() == displayName).Resource;

        Assert.Equal(description, group.Props()["Description"]!.GetValue<string>());
    }

    [Fact]
    public void Import_mapping_names_exactly_the_three_groups_that_predate_this_stack()
    {
        var mapping = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "import-mapping.json")))!.AsObject();

        Assert.Equal(
            new[] { "AIOps-Mlflow-Production", "App-AIOps", "App-TechLeadership" },
            mapping.Select(m => GroupNamesByLogicalId[m.Key]).Order());
        Assert.All(mapping, m => Assert.Equal(IdentityCenterInstance.IdentityStoreId, m.Value!["IdentityStoreId"]!.GetValue<string>()));
    }

    [Fact]
    public void Every_group_is_retained()
    {
        Assert.All(Template.OfType("AWS::IdentityStore::Group"),
            g => Assert.Equal("Retain", g.Resource["DeletionPolicy"]!.GetValue<string>()));
    }

    [Fact]
    public void Memberships_are_exactly_the_approved_people()
    {
        var members = Template.OfType("AWS::IdentityStore::GroupMembership")
            .Select(m => (
                Group: GroupNamesByLogicalId[m.Resource.Props()["GroupId"]!.GetAttTarget()],
                User: m.Resource.Props()["MemberId"]!["UserId"]!.GetValue<string>()))
            .OrderBy(m => m.Group).ThenBy(m => m.User)
            .ToArray();

        Assert.Equal(
            new[]
            {
                ("App-AIOps-Evaluate", Users.AdamWebb),
                ("App-AIOps-Evaluate", Users.AmirZahedi),
                ("App-AIOps-Unblind", Users.AmirZahedi),
            }.OrderBy(m => m.Item1).ThenBy(m => m.Item2),
            members);
    }

    [Fact]
    public void Only_the_three_new_groups_are_assigned_to_the_AIOps_application()
    {
        var assignments = Template.OfType("AWS::SSO::ApplicationAssignment").Select(a => a.Resource.Props()).ToArray();

        Assert.All(assignments, a =>
        {
            Assert.Equal(IdentityCenterInstance.AiopsSamlApplicationArn, a["ApplicationArn"]!.GetValue<string>());
            Assert.Equal("GROUP", a["PrincipalType"]!.GetValue<string>());
        });
        Assert.Equal(
            new[] { "App-AIOps-Evaluate", "App-AIOps-Rater", "App-AIOps-Unblind" },
            assignments.Select(a => GroupNamesByLogicalId[a["PrincipalId"]!.GetAttTarget()]).Order());
    }

    [Fact]
    public void The_only_permission_set_is_AIOpsEvaluation_for_one_hour()
    {
        var permissionSet = Assert.Single(Template.OfType("AWS::SSO::PermissionSet")).Resource.Props();

        Assert.Equal("AIOpsEvaluation", permissionSet["Name"]!.GetValue<string>());
        Assert.Equal("PT1H", permissionSet["SessionDuration"]!.GetValue<string>());
        Assert.Null(permissionSet["ManagedPolicies"]);
        Assert.Null(permissionSet["CustomerManagedPolicyReferences"]);
        Assert.Null(permissionSet["PermissionsBoundary"]);
    }

    [Fact]
    public void AIOpsEvaluation_inline_policy_is_exactly_the_approved_policy()
    {
        var expected = JsonNode.Parse("""
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Sid": "SharedMlflowInstance",
                  "Effect": "Allow",
                  "Action": ["sagemaker:CallMlflowAppApi", "sagemaker:DescribeMlflowApp"],
                  "Resource": "arn:aws:sagemaker:ap-southeast-2:791686214595:mlflow-app/app-GBU5I6LYU4G7"
                },
                {
                  "Sid": "DescribeRunnerExecutions",
                  "Effect": "Allow",
                  "Action": ["states:DescribeExecution", "states:ListExecutions", "states:DescribeStateMachine"],
                  "Resource": [
                    "arn:aws:states:ap-southeast-2:791686214595:stateMachine:aiops-golden-eval",
                    "arn:aws:states:ap-southeast-2:791686214595:execution:aiops-golden-eval:*"
                  ]
                },
                {
                  "Sid": "ReadInvocationLoggingConfig",
                  "Effect": "Allow",
                  "Action": "bedrock:GetModelInvocationLoggingConfiguration",
                  "Resource": "*"
                }
              ]
            }
            """);
        var actual = Assert.Single(Template.OfType("AWS::SSO::PermissionSet")).Resource.Props()["InlinePolicy"];

        Assert.True(JsonNode.DeepEquals(expected, actual), actual!.ToJsonString());
    }

    // Belt and braces over the exact-policy test: the hard limits of AI-777, stated on their own so
    // a future edit to the policy cannot quietly cross one.
    [Theory]
    [InlineData("states:StartExecution")]
    [InlineData("states:*")]
    [InlineData("s3:")]
    [InlineData("bedrock:InvokeModel")]
    [InlineData("bedrock:*")]
    [InlineData("autoguru-anz-shared-dvc-storage")]
    public void No_permission_set_grants_a_forbidden_action_or_resource(string forbidden)
    {
        Assert.All(Template.OfType("AWS::SSO::PermissionSet"),
            p => Assert.DoesNotContain(p.Resource.Props().Strings(), s => s.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("AIOpsEvaluationSet")]
    [InlineData("AIOpsGoldenRegistrar")]
    public void Permission_sets_awaiting_CISO_approval_are_not_declared(string name)
    {
        Assert.DoesNotContain(Template.OfType("AWS::SSO::PermissionSet"),
            p => p.Resource.Props()["Name"]!.GetValue<string>() == name);
    }

    [Fact]
    public void AIOpsEvaluation_is_assigned_to_the_Evaluate_group_in_the_shared_account_only()
    {
        var assignment = Assert.Single(Template.OfType("AWS::SSO::Assignment")).Resource.Props();

        Assert.Equal(GroupLogicalId("App-AIOps-Evaluate"), assignment["PrincipalId"]!.GetAttTarget());
        Assert.Equal("GROUP", assignment["PrincipalType"]!.GetValue<string>());
        Assert.Equal(SharedAccount, assignment["TargetId"]!.GetValue<string>());
        Assert.Equal("AWS_ACCOUNT", assignment["TargetType"]!.GetValue<string>());
    }

    [Fact]
    public void The_Rater_group_never_holds_a_permission_set()
    {
        var rater = GroupLogicalId("App-AIOps-Rater");

        Assert.DoesNotContain(Template.OfType("AWS::SSO::Assignment"),
            a => a.Resource.Props()["PrincipalId"]!.GetAttTarget() == rater);
    }

    [Fact]
    public void Synthesises_without_a_bootstrap_dependency()
    {
        // The management account is not CDK-bootstrapped; the version check would fail the deploy.
        Assert.Null(Template["Parameters"]?["BootstrapVersion"]);
        Assert.Null(Template["Rules"]?["CheckBootstrapVersion"]);
    }
}
