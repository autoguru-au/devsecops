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

    private const string DotnetInstallScriptCommit = "e5cf1dd2d1540ed05ac84f8eb8c5cdec2807621e";
    private const string DotnetInstallScriptSha256 = "082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e";
    private const string DotnetSdkVersion = "10.0.401";

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

        // CDK grants this role only codestar-connections:UseConnection. The connection's ARN carries the
        // renamed codeconnections prefix, so the new action name is granted as well.
        var sourceRole = new Role(this, "SourceActionRole", new RoleProps
        {
            AssumedBy = new ArnPrincipal(pipelineRole.RoleArn),
        });
        sourceRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "UseGitHubConnection",
            Actions = new[] { "codeconnections:UseConnection" },
            Resources = new[] { DeployNames.GitHubConnectionArn },
        }));

        var sourceAction = new CodeStarConnectionsSourceAction(new CodeStarConnectionsSourceActionProps
        {
            ActionName = "GitHub",
            ConnectionArn = DeployNames.GitHubConnectionArn,
            Owner = "autoguru-au",
            Repo = "devsecops",
            Branch = "main",
            Output = source,
            Role = sourceRole,
            // The path-filtered trigger below is the only trigger. Left on, this would start a run for
            // every push to main.
            TriggerOnPush = false,
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
                            // The install script at a fixed commit, checked against its hash, and an exact SDK, so
                            // neither an upstream script change nor a new 10.0 release can change what deploys.
                            $"curl -fsSL https://raw.githubusercontent.com/dotnet/install-scripts/{DotnetInstallScriptCommit}/src/dotnet-install.sh -o /tmp/dotnet-install.sh",
                            $"echo \"{DotnetInstallScriptSha256}  /tmp/dotnet-install.sh\" | sha256sum -c -",
                            $"bash /tmp/dotnet-install.sh --version {DotnetSdkVersion} --install-dir \"$HOME/.dotnet\"",
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
