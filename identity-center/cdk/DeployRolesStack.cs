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
/// account. The execution role cannot assign or provision a permission set in the management account,
/// change or delete the Identity Center instance, delete a group, or assign groups to any application
/// but AIOps, and the deploy role cannot import. It can still add anyone to any existing group,
/// including one that already holds access to the management account, because group ids are not
/// known in advance. The gates are pull-request review on devsecops main and the pipeline's manual
/// approval.
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

        var instanceArn = IdentityCenterInstance.InstanceArn;
        var instanceId = instanceArn[(instanceArn.LastIndexOf('/') + 1)..];

        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "IdentityCenterRead",
            Actions = new[] { "sso:Describe*", "sso:List*", "sso:Get*" },
            Resources = new[] { "*" },
        }));
        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "PermissionSetsAndAccountAssignments",
            Actions = new[]
            {
                "sso:CreatePermissionSet",
                "sso:UpdatePermissionSet",
                "sso:DeletePermissionSet",
                "sso:PutInlinePolicyToPermissionSet",
                "sso:DeleteInlinePolicyFromPermissionSet",
                "sso:AttachManagedPolicyToPermissionSet",
                "sso:DetachManagedPolicyFromPermissionSet",
                "sso:AttachCustomerManagedPolicyReferenceToPermissionSet",
                "sso:DetachCustomerManagedPolicyReferenceFromPermissionSet",
                "sso:PutPermissionsBoundaryToPermissionSet",
                "sso:DeletePermissionsBoundaryFromPermissionSet",
                "sso:ProvisionPermissionSet",
                "sso:CreateAccountAssignment",
                "sso:DeleteAccountAssignment",
                "sso:TagResource",
                "sso:UntagResource",
            },
            Resources = new[]
            {
                instanceArn,
                $"arn:aws:sso:::permissionSet/{instanceId}/*",
                "arn:aws:sso:::account/*",
            },
        }));
        // The one application this stack assigns groups to.
        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "AiopsApplicationAssignments",
            Actions = new[] { "sso:CreateApplicationAssignment", "sso:DeleteApplicationAssignment" },
            Resources = new[] { instanceArn, IdentityCenterInstance.AiopsSamlApplicationArn },
        }));
        // Group and membership ids are not known until the groups exist, so these stay on group/*:
        // a template can add anyone to any existing group. Review and the manual approval are the
        // control for that. There is no DeleteGroup: every group here is Retain, so CloudFormation
        // never needs it, and without it no template can delete a group.
        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "IdentityStoreGroups",
            Actions = new[]
            {
                "identitystore:CreateGroup",
                "identitystore:UpdateGroup",
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
        // A permission set assigned or provisioned in the management account is the one grant
        // Identity Center makes there, and it would put whoever controls this pipeline in charge of
        // the org.
        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "NeverGrantInManagementAccount",
            Effect = Effect.DENY,
            Actions = new[] { "sso:CreateAccountAssignment", "sso:DeleteAccountAssignment", "sso:ProvisionPermissionSet" },
            Resources = new[] { $"arn:aws:sso:::account/{Accounts.Management}" },
        }));
        execution.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "NeverChangeTheInstance",
            Effect = Effect.DENY,
            Actions = new[] { "sso:CreateInstance*", "sso:UpdateInstance*", "sso:DeleteInstance*" },
            Resources = new[] { "*" },
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
        // An import change set would let a template take over a resource it never created, such as a
        // permission set provisioned in the management account. The one import (README) is run by
        // an administrator by hand.
        deploy.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "NoImportChangeSets",
            Effect = Effect.DENY,
            Actions = new[] { "cloudformation:CreateChangeSet" },
            Resources = new[] { "*" },
            Conditions = new Dictionary<string, object>
            {
                ["Null"] = new Dictionary<string, object> { ["cloudformation:ImportResourceTypes"] = "false" },
            },
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
