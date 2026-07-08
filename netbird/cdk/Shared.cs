using Amazon.CDK;
using Amazon.CDK.AWS.SNS;

namespace Netbird.Cdk;

/// <summary>
/// Well-known identifiers in the autoguru-shared account that this app references rather than
/// creates. Netbird lives alongside the existing platform infrastructure (Pritunl VPN, shared
/// RDS) in the shared-services VPC, so it reuses these instead of standing up parallel resources.
/// </summary>
internal static class Shared
{
    /// <summary>
    /// The shared-services VPC (10.70.0.0/16): public subnets for the edge tier (Pritunl, the
    /// public ALB, and now Netbird) and private-isolated subnets for the data tier (shared RDS).
    /// </summary>
    public const string VpcId = "vpc-064a7525a3bcc4667";

    /// <summary>
    /// CloudFormation export of the shared Slack-notifier SNS topic ARN, published by the
    /// SharedPlatformStack as "{region}-{accountEnvironment}-SlackNotifierTopicArn".
    /// </summary>
    public const string SlackNotifierTopicArnExport = "anz-shared-SlackNotifierTopicArn";

    /// <summary>Imports the shared Slack-notifier SNS topic so alarms notify the same channel as the rest of the platform.</summary>
    public static ITopic SlackNotifierTopic(Stack stack)
        => Topic.FromTopicArn(stack, "SlackNotifierTopic", Fn.ImportValue(SlackNotifierTopicArnExport));
}
