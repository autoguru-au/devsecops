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
/// Netbird routing peer: runs the Netbird agent + WireGuard data plane on a single EC2
/// instance and carries the static Elastic IP that is added once to the Cloudflare origin
/// allowlist (it survives EC2 Auto Recovery). Stateless: re-enrols from the setup key on
/// rebuild. Own dedicated VPC, SSM-only access (no inbound SSH).
/// </summary>
public class NetbirdRoutingPeerStack : Stack
{
    public NetbirdRoutingPeerStack(Construct scope, string id, IStackProps props)
        : base(scope, id, props)
    {
        // Netbird setup key in Secrets Manager - created empty here, populated out of band after
        // the control plane is set up, so the key can be rotated without an instance rebuild.
        var setupKeySecret = new Secret(this, "NetbirdSetupKey", new SecretProps
        {
            SecretName = "/netbird/routing-peer/setup-key",
            Description = "Netbird setup key for routing peer enrollment",
        });

        // IAM role: SSM access (no inbound SSH) + Secrets Manager read.
        var instanceRole = new Role(this, "RoutingPeerRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("ec2.amazonaws.com"),
            ManagedPolicies =
            [
                ManagedPolicy.FromAwsManagedPolicyName("AmazonSSMManagedInstanceCore"),
            ],
        });
        setupKeySecret.GrantRead(instanceRole);

        // Dedicated VPC for the routing peer (public subnet + EIP, no NAT). Isolated from shared
        // services; the peer only needs internet egress to reach Cloudflare via its EIP.
        var vpc = new Vpc(this, "Vpc", new VpcProps
        {
            MaxAzs = 1,
            NatGateways = 0,
            SubnetConfiguration =
            [
                new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 },
            ],
        });

        var sg = new SecurityGroup(this, "RoutingPeerSg", new SecurityGroupProps
        {
            Vpc = vpc,
            Description = "Netbird routing peer",
            AllowAllOutbound = true,
        });
        sg.AddIngressRule(Peer.AnyIpv4(), Port.Udp(51820), "WireGuard / Netbird data plane");

        var instance = new Instance_(this, "RoutingPeer", new InstanceProps
        {
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
            InstanceType = InstanceType.Of(InstanceClass.T3, InstanceSize.MICRO),
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
            UserData = UserData.Custom(EmbeddedScript.Read("routing-peer-user-data.sh")),
        });

        // Elastic IP - stable egress address added once to the Cloudflare origin allowlist.
        var eip = new CfnEIP(this, "RoutingPeerEip", new CfnEIPProps
        {
            InstanceId = instance.InstanceId,
            Tags = [new CfnTag { Key = "Purpose", Value = "cloudflare-allowlist-source" }],
        });

        // Auto Recovery: recover action preserves the instance ID, EIP association and private IP.
        var statusAlarm = new Alarm(this, "InstanceStatusAlarm", new AlarmProps
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

        _ = new CfnOutput(this, "ElasticIp", new CfnOutputProps
        {
            Value = eip.Ref,
            Description = "Add this IP to Cloudflare origin allowlist",
        });
    }
}
