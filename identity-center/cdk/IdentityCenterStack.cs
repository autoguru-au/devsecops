using Amazon.CDK;
using Amazon.CDK.AWS.IdentityStore;
using Amazon.CDK.AWS.SSO;
using Constructs;

namespace IdentityCenter.Cdk;

/// <summary>
/// Identity Center groups, their memberships and application assignments, and the permission sets
/// that belong to them. Deployed to the management account by the identity-center pipeline.
///
/// This is the single definition of these groups. No other repository declares an
/// AWS::IdentityStore::Group; consumers (aiops, guru-share, ignite) only read groups by id or name.
///
/// App-AllStaff is deliberately absent: autoguru's AllStaffGroupSyncFunction owns its membership
/// and looks it up by name.
///
/// Groups are RETAIN. A deleted group cannot be brought back with the same GroupId, and AIOps maps
/// group ids rather than names, so removing a group from this file must never delete it as a side
/// effect. Delete it on purpose, after its consumers have dropped it. Permission sets and
/// assignments keep CloudFormation's default (delete), so removing one from this file revokes the
/// grant rather than orphaning it live.
///
/// Memberships declared here are the only memberships of those groups: one GroupMembership per
/// person, added and removed by pull request, never in the console. App-AIOps and
/// App-TechLeadership predate this stack and their members are still managed in the console.
/// </summary>
public sealed class IdentityCenterStack : Stack
{
    public IdentityCenterStack(Construct scope, string id, IStackProps props)
        : base(scope, id, props)
    {
        // Created by hand before this stack existed, and imported (see README). Their DisplayName
        // and Description match the live groups exactly, because an import records the template as
        // is and would otherwise leave drift that no later deploy corrects.
        _ = Group("AppAIOpsGroup", "App-AIOps",
            "AIOps portal access. Application access only, no AWS account permissions.");
        _ = Group("AppTechLeadershipGroup", "App-TechLeadership",
            "Tech leadership cohort - application access only, no AWS account permissions");

        // Moved from autoguru master-06-permissionsets, which keeps the group's permission set and
        // Production assignment and refers to the group by its fixed GroupId. It has no members.
        _ = Group("AIOpsMlflowProductionGroup", "AIOps-Mlflow-Production",
            "Open the AIOps production MLflow instance (FR-MLF-11). Tier 1 grant. Members are declared in autoguru-au/devsecops identity-center only, never added in the console.");

        // AIOps evaluation (AI-777, epic AI-764). AIOps maps each group's id to its own groups, so
        // all three are assigned to the AIOps SAML application: without that, members cannot sign in.
        var evaluate = Group("AppAIOpsEvaluateGroup", "App-AIOps-Evaluate",
            "AIOps evaluation workbench: golden set curation and experiments. Application access only, no AWS account permissions.");
        Member("AppAIOpsEvaluateAmirZahedi", evaluate, Users.AmirZahedi);
        Member("AppAIOpsEvaluateAdamWebb", evaluate, Users.AdamWebb);
        AssignToAiops("AppAIOpsEvaluateAiopsAssignment", evaluate);

        // No members until the second rater is named. Never give this group a permission set:
        // raters only rank and label in the console.
        var rater = Group("AppAIOpsRaterGroup", "App-AIOps-Rater",
            "AIOps blind ranking and labelling rounds only. Application access only, no AWS account permissions.");
        AssignToAiops("AppAIOpsRaterAiopsAssignment", rater);

        var unblind = Group("AppAIOpsUnblindGroup", "App-AIOps-Unblind",
            "AIOps: reveal a closed blind round. Application access only, no AWS account permissions.");
        Member("AppAIOpsUnblindAmirZahedi", unblind, Users.AmirZahedi);
        AssignToAiops("AppAIOpsUnblindAiopsAssignment", unblind);

        // Read-only access to what an evaluation run touches in the shared account. Runs start only
        // through the AIOps console backend, so there is no states:StartExecution here, and no S3
        // (nothing on the golden set bucket) and no bedrock:InvokeModel. AIOpsEvaluationSet and
        // AIOpsGoldenRegistrar wait on the CISO's approval (AIOps PRD OQ-45) and are not declared.
        //
        // aiops-golden-eval does not exist until AI-773 deploys it; until then its statement grants
        // nothing.
        var evaluation = new CfnPermissionSet(this, "AIOpsEvaluationPermissionSet", new CfnPermissionSetProps
        {
            InstanceArn = IdentityCenterInstance.InstanceArn,
            Name = "AIOpsEvaluation",
            Description = "AIOps evaluation: shared MLflow and golden-eval runner read access (AI-777).",
            SessionDuration = "PT1H",
            InlinePolicy = AiopsEvaluationPolicy,
            Tags = new[]
            {
                new CfnTag { Key = "App", Value = "AIOps" },
                new CfnTag { Key = "ManagedBy", Value = "autoguru-au/devsecops" },
            },
        });

        _ = new CfnAssignment(this, "AIOpsEvaluationSharedAssignment", new CfnAssignmentProps
        {
            InstanceArn = IdentityCenterInstance.InstanceArn,
            PermissionSetArn = evaluation.AttrPermissionSetArn,
            PrincipalId = evaluate.AttrGroupId,
            PrincipalType = "GROUP",
            TargetId = Accounts.Shared,
            TargetType = "AWS_ACCOUNT",
        });
    }

    /// <summary>
    /// The AIOpsEvaluation inline policy, written out rather than built with PolicyDocument so the
    /// template carries exactly these statements and nothing is merged, reordered or minimised.
    /// </summary>
    internal static readonly Dictionary<string, object> AiopsEvaluationPolicy = new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new object[]
        {
            new Dictionary<string, object>
            {
                ["Sid"] = "SharedMlflowInstance",
                ["Effect"] = "Allow",
                ["Action"] = new[] { "sagemaker:CallMlflowAppApi", "sagemaker:DescribeMlflowApp" },
                ["Resource"] = "arn:aws:sagemaker:ap-southeast-2:791686214595:mlflow-app/app-GBU5I6LYU4G7",
            },
            new Dictionary<string, object>
            {
                ["Sid"] = "DescribeRunnerExecutions",
                ["Effect"] = "Allow",
                ["Action"] = new[] { "states:DescribeExecution", "states:ListExecutions", "states:DescribeStateMachine" },
                ["Resource"] = new[]
                {
                    "arn:aws:states:ap-southeast-2:791686214595:stateMachine:aiops-golden-eval",
                    "arn:aws:states:ap-southeast-2:791686214595:execution:aiops-golden-eval:*",
                },
            },
            new Dictionary<string, object>
            {
                ["Sid"] = "ReadInvocationLoggingConfig",
                ["Effect"] = "Allow",
                ["Action"] = "bedrock:GetModelInvocationLoggingConfiguration",
                ["Resource"] = "*",
            },
        },
    };

    private CfnGroup Group(string logicalId, string displayName, string description)
    {
        var group = new CfnGroup(this, logicalId, new CfnGroupProps
        {
            DisplayName = displayName,
            Description = description,
            IdentityStoreId = IdentityCenterInstance.IdentityStoreId,
        });
        group.ApplyRemovalPolicy(RemovalPolicy.RETAIN);
        return group;
    }

    private void Member(string logicalId, CfnGroup group, string userId)
        => _ = new CfnGroupMembership(this, logicalId, new CfnGroupMembershipProps
        {
            GroupId = group.AttrGroupId,
            IdentityStoreId = IdentityCenterInstance.IdentityStoreId,
            MemberId = new CfnGroupMembership.MemberIdProperty { UserId = userId },
        });

    private void AssignToAiops(string logicalId, CfnGroup group)
        => _ = new CfnApplicationAssignment(this, logicalId, new CfnApplicationAssignmentProps
        {
            ApplicationArn = IdentityCenterInstance.AiopsSamlApplicationArn,
            PrincipalId = group.AttrGroupId,
            PrincipalType = "GROUP",
        });
}
