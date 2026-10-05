using Amazon.CDK;
using Amazon.CDK.AWS.CodeBuild;
using Amazon.CDK.AWS.CodePipeline;
using Amazon.CDK.AWS.CodePipeline.Actions;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.KMS;
using Amazon.CDK.AWS.S3;
using Constructs;
using StageProps = Amazon.CDK.AWS.CodePipeline.StageProps;
using IStageProps = Amazon.CDK.AWS.CodePipeline.IStageProps;

namespace IdentityCenter.Cdk;

/// <summary>
/// The pipeline in the shared account that deploys <see cref="IdentityCenterStack"/> to the
/// management account: a merge to main that touches identity-center/ runs the tests, synthesises
/// the template, creates a change set in the management account, waits for a manual approval, and
/// then executes it.
///
/// Deployed by hand (see README). It does not update itself: a merge to main changes what the
/// pipeline deploys, never the pipeline or the roles it deploys with.
/// </summary>
public sealed class PipelineStack : Stack
{
    private const string ChangeSetName = "identity-center-pipeline";

    public PipelineStack(Construct scope, string id, IStackProps props)
        : base(scope, id, props)
    {
        var key = new Key(this, "ArtifactKey", new KeyProps
        {
            Alias = "alias/identity-center-pipeline-artifacts",
            Description = "Encrypts identity-center pipeline artifacts.",
            EnableKeyRotation = true,
        });
        var bucket = new Bucket(this, "ArtifactBucket", new BucketProps
        {
            BucketName = DeployNames.ArtifactBucketName,
            EncryptionKey = key,
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            EnforceSSL = true,
            LifecycleRules = new ILifecycleRule[] { new LifecycleRule { Expiration = Duration.Days(30) } },
        });

        var pipelineRole = new Role(this, "PipelineRole", new RoleProps
        {
            RoleName = DeployNames.PipelineRoleName,
            AssumedBy = new ServicePrincipal("codepipeline.amazonaws.com"),
        });

        // Created by DeployRolesStack in the management account.
        var deployRole = Role.FromRoleArn(this, "DeployActionRole",
            $"arn:aws:iam::{Accounts.Management}:role/{DeployNames.DeployActionRoleName}",
            new FromRoleArnOptions { Mutable = false });
        var executionRole = Role.FromRoleArn(this, "CloudFormationExecutionRole",
            $"arn:aws:iam::{Accounts.Management}:role/{DeployNames.CloudFormationExecutionRoleName}",
            new FromRoleArnOptions { Mutable = false });

        var source = new Artifact_("Source");
        var synthesised = new Artifact_("Synthesised");

        var sourceAction = new CodeStarConnectionsSourceAction(new CodeStarConnectionsSourceActionProps
        {
            ActionName = "GitHub",
            ConnectionArn = DeployNames.GitHubConnectionArn,
            Owner = "autoguru-au",
            Repo = "devsecops",
            Branch = "main",
            Output = source,
        });

        var synth = new PipelineProject(this, "Synth", new PipelineProjectProps
        {
            Description = "Tests and synthesises the identity-center stack.",
            Environment = new BuildEnvironment { BuildImage = LinuxBuildImage.STANDARD_7_0 },
            BuildSpec = BuildSpec.FromObject(new Dictionary<string, object>
            {
                ["version"] = "0.2",
                ["phases"] = new Dictionary<string, object>
                {
                    ["install"] = new Dictionary<string, object>
                    {
                        ["runtime-versions"] = new Dictionary<string, object> { ["nodejs"] = "22" },
                        ["commands"] = new[]
                        {
                            "curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh",
                            "bash /tmp/dotnet-install.sh --channel 10.0 --install-dir \"$HOME/.dotnet\"",
                            "export DOTNET_ROOT=\"$HOME/.dotnet\" PATH=\"$HOME/.dotnet:$PATH\"",
                            // Same version as the identity-center workflow, so a CLI release cannot change a deploy.
                            "npm install -g aws-cdk@2.1144.0",
                        },
                    },
                    ["build"] = new Dictionary<string, object>
                    {
                        ["commands"] = new[]
                        {
                            // Buildspec 0.2 runs every command in one shell, so each cd is absolute.
                            "cd \"$CODEBUILD_SRC_DIR/identity-center\" && dotnet test tests -c Release",
                            "cd \"$CODEBUILD_SRC_DIR/identity-center/cdk\" && cdk synth " + DeployNames.IdentityCenterStackName,
                        },
                    },
                },
                ["artifacts"] = new Dictionary<string, object>
                {
                    ["base-directory"] = "identity-center/cdk/cdk.out",
                    ["files"] = new[] { DeployNames.IdentityCenterStackName + ".template.json" },
                },
            }),
        });

        _ = new Pipeline(this, "Pipeline", new PipelineProps
        {
            PipelineName = "identity-center",
            PipelineType = PipelineType.V2,
            Role = pipelineRole,
            ArtifactBucket = bucket,
            Stages = new IStageProps[]
            {
                new StageProps { StageName = "Source", Actions = new IAction[] { sourceAction } },
                new StageProps
                {
                    StageName = "Synth",
                    Actions = new IAction[]
                    {
                        new CodeBuildAction(new CodeBuildActionProps
                        {
                            ActionName = "TestAndSynth",
                            Project = synth,
                            Input = source,
                            Outputs = new[] { synthesised },
                        }),
                    },
                },
                new StageProps
                {
                    StageName = "Deploy",
                    Actions = new IAction[]
                    {
                        new CloudFormationCreateReplaceChangeSetAction(new CloudFormationCreateReplaceChangeSetActionProps
                        {
                            ActionName = "CreateChangeSet",
                            RunOrder = 1,
                            Account = Accounts.Management,
                            Region = Accounts.Region,
                            Role = deployRole,
                            DeploymentRole = executionRole,
                            StackName = DeployNames.IdentityCenterStackName,
                            ChangeSetName = ChangeSetName,
                            TemplatePath = synthesised.AtPath(DeployNames.IdentityCenterStackName + ".template.json"),
                            AdminPermissions = false,
                        }),
                        new ManualApprovalAction(new ManualApprovalActionProps
                        {
                            ActionName = "Approve",
                            RunOrder = 2,
                            AdditionalInformation =
                                $"Review change set {ChangeSetName} on stack {DeployNames.IdentityCenterStackName} in {Accounts.Management} before approving. It changes who can sign in where.",
                        }),
                        new CloudFormationExecuteChangeSetAction(new CloudFormationExecuteChangeSetActionProps
                        {
                            ActionName = "ExecuteChangeSet",
                            RunOrder = 3,
                            Account = Accounts.Management,
                            Region = Accounts.Region,
                            Role = deployRole,
                            StackName = DeployNames.IdentityCenterStackName,
                            ChangeSetName = ChangeSetName,
                        }),
                    },
                },
            },
            Triggers = new ITriggerProps[]
            {
                new TriggerProps
                {
                    ProviderType = ProviderType.CODE_STAR_SOURCE_CONNECTION,
                    GitConfiguration = new GitConfiguration
                    {
                        SourceAction = sourceAction,
                        PushFilter = new IGitPushFilter[]
                        {
                            new GitPushFilter
                            {
                                BranchesIncludes = new[] { "main" },
                                FilePathsIncludes = new[] { "identity-center/**" },
                            },
                        },
                    },
                },
            },
        });
    }
}
