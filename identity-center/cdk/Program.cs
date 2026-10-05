using Amazon.CDK;

namespace IdentityCenter.Cdk;

public static class Program
{
    public static void Main()
    {
        var app = new App();
        _ = Build(app);
        app.Synth();
    }

    internal static (IdentityCenterStack IdentityCenter, DeployRolesStack DeployRoles, PipelineStack Pipeline) Build(App app)
    {
        var management = new Amazon.CDK.Environment { Account = Accounts.Management, Region = Accounts.Region };
        var shared = new Amazon.CDK.Environment { Account = Accounts.Shared, Region = Accounts.Region };

        // The management account is not CDK-bootstrapped and these two stacks carry no assets, so
        // they synthesise without the bootstrap version check. The pipeline deploys IdentityCenter
        // through CloudFormation directly; DeployRoles is deployed by hand with CLI credentials.
        var identityCenter = new IdentityCenterStack(app, DeployNames.IdentityCenterStackName, new StackProps
        {
            Env = management,
            Synthesizer = new BootstraplessSynthesizer(),
            TerminationProtection = true,
            Description = "Identity Center groups, memberships, application assignments and permission sets (autoguru-au/devsecops identity-center).",
        });
        var deployRoles = new DeployRolesStack(app, "IdentityCenterDeployRoles", new StackProps
        {
            Env = management,
            Synthesizer = new BootstraplessSynthesizer(),
            TerminationProtection = true,
            Description = "Roles the identity-center pipeline in autoguru-shared deploys with (autoguru-au/devsecops identity-center).",
        });
        var pipeline = new PipelineStack(app, "IdentityCenterPipeline", new StackProps
        {
            Env = shared,
            Description = "Deploys the IdentityCenter stack to the management account (autoguru-au/devsecops identity-center).",
        });

        return (identityCenter, deployRoles, pipeline);
    }
}
