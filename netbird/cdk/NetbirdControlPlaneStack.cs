using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.SecretsManager;
using Constructs;
using InstanceProps = Amazon.CDK.AWS.EC2.InstanceProps;
using InstanceType = Amazon.CDK.AWS.EC2.InstanceType;

namespace Netbird.Cdk;

/// <summary>
/// Netbird self-hosted control plane (management, signal, relay, dashboard, Coturn) on a
/// single EC2 instance with a static Elastic IP. Internet-facing VPN edge in its own
/// dedicated VPC, isolated from shared services. SSM-only access (no inbound SSH).
/// DNS (netbird.autoguru.com.au) lives in Cloudflare, so this stack only outputs the EIP.
/// </summary>
public class NetbirdControlPlaneStack : Stack
{
    public NetbirdControlPlaneStack(Construct scope, string id, IStackProps props)
        : base(scope, id, props)
    {
        // Entra client secret stored in Secrets Manager - written manually before first deploy.
        var entraSecret = Secret.FromSecretNameV2(
            this, "EntraClientSecret", "/netbird/control-plane/entra-client-secret");

        // IAM role: SSM access (no inbound SSH) + Secrets Manager read.
        var instanceRole = new Role(this, "ControlPlaneRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("ec2.amazonaws.com"),
            ManagedPolicies =
            [
                ManagedPolicy.FromAwsManagedPolicyName("AmazonSSMManagedInstanceCore"),
            ],
        });
        entraSecret.GrantRead(instanceRole);

        // Dedicated VPC for the VPN edge: a public-facing control plane is isolated from the
        // shared-services network. Public subnet only (the instance carries an EIP); no NAT needed.
        var vpc = new Vpc(this, "Vpc", new VpcProps
        {
            MaxAzs = 1,
            NatGateways = 0,
            SubnetConfiguration =
            [
                new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 },
            ],
        });

        var sg = new SecurityGroup(this, "ControlPlaneSg", new SecurityGroupProps
        {
            Vpc = vpc,
            Description = "Netbird self-hosted control plane",
            AllowAllOutbound = true,
        });

        // These ports are intentionally open to the internet (0.0.0.0/0). Remote peers connect to
        // the control plane from arbitrary, unpredictable networks (home ISPs, mobile, roaming), so
        // they cannot be restricted to a known corporate CIDR. Do NOT scope these to a CIDR.
        sg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(443), "HTTPS -- management API and dashboard");
        // "Lets Encrypt" intentionally has no apostrophe: AWS rejects apostrophes in
        // security-group rule descriptions (the deploy fails with "Invalid rule description").
        sg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "Lets Encrypt ACME HTTP challenge");
        sg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(33073), "Management gRPC -- peer client connections");
        sg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(10000), "Signal server -- WebRTC P2P setup");
        sg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(33080), "Relay server -- encrypted fallback tunnelling");
        sg.AddIngressRule(Peer.AnyIpv4(), Port.Udp(3478), "TURN/STUN -- Coturn NAT traversal (UDP)");
        sg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(3478), "TURN/STUN -- Coturn NAT traversal (TCP)");
        sg.AddIngressRule(Peer.AnyIpv4(), Port.UdpRange(49152, 65535), "TURN relay media ports");

        var instance = new Instance_(this, "ControlPlane", new InstanceProps
        {
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
            InstanceType = InstanceType.Of(InstanceClass.T3, InstanceSize.SMALL),
            MachineImage = MachineImage.LatestAmazonLinux2023(),
            SecurityGroup = sg,
            Role = instanceRole,
            RequireImdsv2 = true,
            BlockDevices =
            [
                new BlockDevice
                {
                    DeviceName = "/dev/xvda",
                    Volume = BlockDeviceVolume.Ebs(30, new EbsDeviceOptions
                    {
                        VolumeType = EbsDeviceVolumeType.GP3,
                        Encrypted = true,
                    }),
                },
            ],
            UserData = UserData.Custom(EmbeddedScript.Read("control-plane-user-data.sh")),
        });

        // Elastic IP - stable DNS target for netbird.autoguru.com.au.
        var eip = new CfnEIP(this, "ControlPlaneEip", new CfnEIPProps
        {
            InstanceId = instance.InstanceId,
        });

        // Auto Recovery: recover action preserves the EIP association and private IP.
        var statusAlarm = new Alarm(this, "ControlPlaneStatusAlarm", new AlarmProps
        {
            Metric = new Metric(new MetricProps
            {
                Namespace = "AWS/EC2",
                MetricName = "StatusCheckFailed_System",
                DimensionsMap = new Dictionary<string, string> { { "InstanceId", instance.InstanceId } },
                Period = Duration.Minutes(1),
                Statistic = "Maximum",
            }),
            Threshold = 1,
            EvaluationPeriods = 2,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        });
        statusAlarm.AddAlarmAction(new Ec2Action(Ec2InstanceAction.RECOVER));

        _ = new CfnOutput(this, "ControlPlaneIp", new CfnOutputProps
        {
            Value = eip.Ref,
            Description = "Point netbird.autoguru.com.au DNS A record to this IP before running setup",
        });
        _ = new CfnOutput(this, "ControlPlaneInstanceId", new CfnOutputProps
        {
            Value = instance.InstanceId,
            Description = "SSM Session Manager target for post-deploy Netbird setup script",
        });
    }
}
