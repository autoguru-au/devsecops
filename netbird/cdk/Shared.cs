using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
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

    /// <summary>
    /// Delegated public hosted zone for netbird.autoguru.com.au, created by a shared-account
    /// admin and delegated from the parent autoguru.com.au zone. This app manages the records
    /// inside it (the control-plane A record) but not the zone itself.
    /// </summary>
    public const string HostedZoneId = "Z0616831377EB4AQXS3S1";

    /// <summary>Zone name of <see cref="HostedZoneId"/>.</summary>
    public const string HostedZoneName = "netbird.autoguru.com.au";

    /// <summary>
    /// Amazon Linux 2023 AMI both Netbird instances run (al2023-ami-2023.12.20260706.1-kernel-6.1-x86_64).
    /// Pinned on purpose. MachineImage.LatestAmazonLinux2023() is an SSM-parameter lookup that
    /// CloudFormation re-resolves on EVERY deploy, and a changed ImageId replaces the instance. Once
    /// AWS published a newer AMI (2026-10-01), any deploy, even one that changes nothing on the
    /// instance, would have replaced both boxes: on the control plane that wipes /opt/netbird and the
    /// management datastore (peers, users, networks). Bump this only as a planned rebuild. Patch the
    /// running instances in place with `dnf upgrade --releasever=latest`: AL2023 locks its package
    /// repository to the AMI's release, so a plain `dnf upgrade` stays on this AMI's packages.
    /// </summary>
    public const string Al2023AmiId = "ami-03eb9488a97c79445";

    /// <summary>The pinned <see cref="Al2023AmiId"/> as a machine image for both instances.</summary>
    public static IMachineImage PinnedAmazonLinux2023()
        => MachineImage.GenericLinux(new Dictionary<string, string> { ["ap-southeast-2"] = Al2023AmiId });

    /// <summary>Imports the shared Slack-notifier SNS topic so alarms notify the same channel as the rest of the platform.</summary>
    public static ITopic SlackNotifierTopic(Stack stack)
        => Topic.FromTopicArn(stack, "SlackNotifierTopic", Fn.ImportValue(SlackNotifierTopicArnExport));
}
