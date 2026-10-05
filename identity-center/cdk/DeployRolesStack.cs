using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace IdentityCenter.Cdk;

/// <summary>
/// The two roles in the management account that let the shared-account pipeline deploy
/// <see cref="IdentityCenterStack"/>. Deployed by hand, once, by a management-account administrator,
/// before <see cref="PipelineStack"/>: the pipeline's artifact bucket and key policies name the deploy
/// role, and S3 and KMS reject a policy whose principal does not exist yet. The pipeline never deploys
/// this stack, so a change merged to main cannot widen its own access.
///
/// What this hands the shared account: whoever can drive the pipeline role, or approve an execution,
/// can change Identity Center, and Identity Center can grant any permission set in any member
/// account. The management account itself is excluded (see the deny below), but nothing else is.
/// The gates are pull-request review on devsecops main and the pipeline's manual approval.
/// </summary>
public sealed class DeployRolesStack : Stack
{
    public DeployRolesStack(Construct scope, string id, IStackProps props)
        : base(scope, id, props)
    {
        var stackArn =
            $"arn:aws:cloudformation:{Accounts.Region}:{Accounts.Management}:stack/{DeployNames.IdentityCenterStackName}/*";

        var execution = new Role(this, "CloudFormationExecutionRole", new RoleProps
        {
            RoleName = DeployNames.CloudFormationExecutionRoleName,
            Description = "CloudFormation applies the identity-center stack (autoguru-au/devsecops) with this role.",
            AssumedBy = new ServicePrincipal("cloudformation.amazonaws.com"),
            MaxSessionDuration = Duration.Hours(1),
        });

        // Identity Center offers no useful resource scoping for its admin actions: permission sets,
        // assignments and applications are all addressed through the one instance.
        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "IdentityCenterAdmin",
            Actions = new[] { "sso:*" },
            Resources = new[] { "*" },
        }));
        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "IdentityStoreGroups",
            Actions = new[]
            {
                "identitystore:CreateGroup",
                "identitystore:UpdateGroup",
                "identitystore:DeleteGroup",
                "identitystore:DescribeGroup",
                "identitystore:ListGroups",
                "identitystore:GetGroupId",
                "identitystore:CreateGroupMembership",
                "identitystore:DeleteGroupMembership",
                "identitystore:DescribeGroupMembership",
                "identitystore:ListGroupMemberships",
                "identitystore:GetGroupMembershipId",
                "identitystore:DescribeUser",
            },
            Resources = new[]
            {
                $"arn:aws:identitystore::{Accounts.Management}:identitystore/{IdentityCenterInstance.IdentityStoreId}",
                "arn:aws:identitystore:::group/*",
                "arn:aws:identitystore:::membership/*",
                "arn:aws:identitystore:::user/*",
            },
        }));
        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "OrganizationsRead",
            Actions = new[] { "organizations:DescribeAccount", "organizations:DescribeOrganization", "organizations:ListAccounts" },
            Resources = new[] { "*" },
        }));
        // A permission set assigned in the management account is the one grant Identity Center
        // makes there, and it would put whoever controls this pipeline in charge of the org.
        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "NeverAssignInManagementAccount",
            Effect = Effect.DENY,
            Actions = new[] { "sso:CreateAccountAssignment", "sso:DeleteAccountAssignment" },
            Resources = new[] { $"arn:aws:sso:::account/{Accounts.Management}" },
        }));

        var deploy = new Role(this, "PipelineDeployRole", new RoleProps
        {
            RoleName = DeployNames.DeployActionRoleName,
            Description = "The identity-center pipeline in autoguru-shared creates and executes change sets with this role.",
            // The shared account's root, narrowed to the one pipeline role by aws:PrincipalArn. Naming
            // the role directly would need it to exist first, and the pipeline needs this role to exist
            // first. The condition is evaluated against the caller's real ARN, so it is just as narrow.
            AssumedBy = new AccountPrincipal(Accounts.Shared).WithConditions(new Dictionary<string, object>
            {
                ["ArnEquals"] = new Dictionary<string, object>
                {
                    ["aws:PrincipalArn"] = $"arn:aws:iam::{Accounts.Shared}:role/{DeployNames.PipelineRoleName}",
                },
            }),
            MaxSessionDuration = Duration.Hours(1),
        });
        deploy.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "ChangeSetsOnIdentityCenterStack",
            Actions = new[]
            {
                "cloudformation:CreateChangeSet",
                "cloudformation:DescribeChangeSet",
                "cloudformation:ExecuteChangeSet",
                "cloudformation:DeleteChangeSet",
                "cloudformation:DescribeStacks",
                "cloudformation:DescribeStackEvents",
            },
            Resources = new[] { stackArn },
        }));
        deploy.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "PassExecutionRole",
            Actions = new[] { "iam:PassRole" },
            Resources = new[] { execution.RoleArn },
            Conditions = new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object> { ["iam:PassedToService"] = "cloudformation.amazonaws.com" },
            },
        }));
        deploy.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "ReadPipelineArtifacts",
            Actions = new[] { "s3:GetObject", "s3:GetObjectVersion", "s3:GetBucketLocation", "s3:ListBucket" },
            Resources = new[]
            {
                $"arn:aws:s3:::{DeployNames.ArtifactBucketName}",
                $"arn:aws:s3:::{DeployNames.ArtifactBucketName}/*",
            },
        }));
        // The key's id is not known here; its key policy in the shared account is what limits use
        // to this role.
        deploy.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "DecryptPipelineArtifacts",
            Actions = new[] { "kms:Decrypt", "kms:DescribeKey" },
            Resources = new[] { $"arn:aws:kms:{Accounts.Region}:{Accounts.Shared}:key/*" },
        }));
    }
}
