namespace IdentityCenter.Cdk;

/// <summary>AWS accounts this app deploys into or grants access to.</summary>
internal static class Accounts
{
    /// <summary>
    /// The Organizations management account. It owns the Identity Center instance, and there is no
    /// delegated administrator, so it is the only account that can write groups, permission sets
    /// and application assignments.
    /// </summary>
    public const string Management = "406422318285";

    /// <summary>autoguru-shared: hosts the deployment pipeline and the AIOps console.</summary>
    public const string Shared = "791686214595";

    public const string Region = "ap-southeast-2";
}

/// <summary>The one org-level Identity Center instance.</summary>
internal static class IdentityCenterInstance
{
    public const string InstanceArn = "arn:aws:sso:::instance/ssoins-8259316042a4e826";

    public const string IdentityStoreId = "d-97675b6074";

    /// <summary>
    /// The AIOps customer-managed SAML 2.0 application, federated to the AIOps Cognito user pool.
    /// Its SAML metadata URL ends in base64("406422318285_ins-825998949b600ce5"), see
    /// aiops infrastructure/lib/config/saml.ts. The apl- id follows from that ins- id; confirm it
    /// with `aws sso-admin list-applications` in the management account before the first deploy.
    /// </summary>
    public const string AiopsSamlApplicationArn =
        "arn:aws:sso::406422318285:application/ssoins-8259316042a4e826/apl-825998949b600ce5";
}

/// <summary>
/// Identity Center users referenced by group memberships. Users themselves are not managed here.
/// </summary>
internal static class Users
{
    /// <summary>amir@autoguru.com.au</summary>
    public const string AmirZahedi = "790e7458-5051-7042-bdd5-d9a8dddd61bd";

    /// <summary>adam@autoguru.com.au</summary>
    public const string AdamWebb = "597e6438-4051-704e-b728-eaa1994dc61c";
}

/// <summary>
/// Names shared between the pipeline (shared account) and the deploy roles (management account).
/// Each side refers to the other by these fixed names, so neither stack has to read the other.
/// </summary>
internal static class DeployNames
{
    public const string IdentityCenterStackName = "IdentityCenter";

    /// <summary>The CodePipeline service role in the shared account.</summary>
    public const string PipelineRoleName = "identity-center-pipeline";

    /// <summary>Assumed by the pipeline role to create and execute change sets in the management account.</summary>
    public const string DeployActionRoleName = "identity-center-pipeline-deploy";

    /// <summary>Passed to CloudFormation in the management account to make the Identity Center changes.</summary>
    public const string CloudFormationExecutionRoleName = "identity-center-cloudformation-execution";

    public const string ArtifactBucketName = "identity-center-pipeline-artifacts-791686214595";

    /// <summary>The shared account's GitHub connection, already used by ignite-pipeline.</summary>
    public const string GitHubConnectionArn =
        "arn:aws:codeconnections:ap-southeast-2:791686214595:connection/8d0f7620-c0f7-420c-a70c-893a46c414e1";
}
