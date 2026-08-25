using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.KMS;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.SecretsManager;
using Constructs;
using InstanceProps = Amazon.CDK.AWS.EC2.InstanceProps;
using InstanceType = Amazon.CDK.AWS.EC2.InstanceType;

namespace Netbird.Cdk;

/// <summary>
/// Netbird self-hosted control plane (management, signal, relay, dashboard, Coturn) on a
/// single EC2 instance with a static Elastic IP. Internet-facing VPN edge that runs in the
/// shared-services VPC public-subnet tier (alongside the Pritunl VPN it replaces and the
/// shared RDS) so it inherits the vetted peering/RDS-allowlist fabric and VPC flow logs.
/// SSM-only access (no inbound SSH).
/// DNS: netbird.autoguru.com.au is a delegated public hosted zone in this account, so the
/// stack manages the apex A record (zone apex -> the control-plane EIP) itself.
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

        // Management-server log group (2026-08-26 JWKS incident). The management
        // container is configured (docker-compose.override.yml, see control-plane-user-data.sh)
        // to ship its logs here via the awslogs driver, so the JwksKeyfuncErrors metric filter
        // below can catch a recurrence of the Entra key-rotation outage instead of it going
        // unnoticed until users report "Login Failed".
        var managementLogGroup = new LogGroup(this, "ManagementLogGroup", new LogGroupProps
        {
            LogGroupName = "/netbird/control-plane/management",
            Retention = RetentionDays.THREE_MONTHS,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
        managementLogGroup.GrantWrite(instanceRole);

        // Netbird runs in the shared-services VPC public-subnet tier, next to the Pritunl VPN it
        // replaces (the ITOC/Thoughtworks Well-Architected layout). Reusing the shared VPC means it
        // inherits the vetted peering + RDS allowlist fabric and the VPC's flow logs, and can be
        // admitted to the shared SQL Server RDS by security-group reference.
        var vpc = Vpc.FromLookup(this, "SharedVpc", new VpcLookupOptions
        {
            VpcId = Shared.VpcId,
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

        // Customer-managed key for the root EBS volume. The AWS-managed aws/ebs key cannot be
        // shared cross-account, so a managed-key volume fails the daily AWS Backup copy into the
        // ag-vault DR account (154989417267) - see COM-66. This CMK mirrors the platform's proven
        // RDS-backup key policy (autoguru KmsResourceFactory.CreateRdsKeyForBackups): the shared
        // AWS Backup role can grant/decrypt, and the ag-vault account can decrypt + re-wrap the
        // copied snapshot, scoped to EBS via kms:ViaService.
        var ebsKey = new Key(this, "ControlPlaneEbsKey", new KeyProps
        {
            Alias = "netbird-control-plane-ebs-key",
            Description = "Encrypts the Netbird control-plane EBS volume; permits AWS Backup cross-account copy to ag-vault (COM-66).",
            EnableKeyRotation = true,
            RemovalPolicy = RemovalPolicy.RETAIN,
        });
        ebsKey.AddToResourcePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "AllowAwsBackup",
            Effect = Effect.ALLOW,
            Principals = [new ArnPrincipal("arn:aws:iam::791686214595:role/AWSBackup")],
            Actions = ["kms:Decrypt", "kms:DescribeKey", "kms:CreateGrant"],
            Resources = ["*"],
        }));
        ebsKey.AddToResourcePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Sid = "AllowAgVaultAccountDecrypt",
            Effect = Effect.ALLOW,
            Principals = [new AccountPrincipal("154989417267")],
            Actions =
            [
                "kms:Decrypt", "kms:DescribeKey", "kms:CreateGrant",
                "kms:GenerateDataKey", "kms:GenerateDataKeyWithoutPlaintext",
            ],
            Resources = ["*"],
            Conditions = new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object>
                {
                    ["kms:ViaService"] = "ec2.ap-southeast-2.amazonaws.com",
                },
            },
        }));

        var instance = new Instance_(this, "ControlPlane", new InstanceProps
        {
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
            // Shared public subnets do not auto-assign public IPs, so request one explicitly:
            // the user-data needs internet egress on first boot before the EIP is associated.
            AssociatePublicIpAddress = true,
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
                        KmsKey = ebsKey,
                    }),
                },
            ],
            UserData = UserData.Custom(EmbeddedScript.Read("control-plane-user-data.sh")),
        });

        // Control plane holds the management state (peers, ACLs), so it is enrolled in the shared
        // account's AWS Backup plan by tag (matches the platform VPN/RDS backup convention).
        Amazon.CDK.Tags.Of(instance).Add("backup", "true");

        // Elastic IP - stable DNS target for netbird.autoguru.com.au.
        var eip = new CfnEIP(this, "ControlPlaneEip", new CfnEIPProps
        {
            InstanceId = instance.InstanceId,
        });

        // Apex A record -> control-plane EIP, in the delegated netbird.autoguru.com.au hosted
        // zone (created by a shared-account admin; referenced via Shared, not managed here).
        // Short TTL so an EIP change (stack rebuild) propagates quickly to peers.
        var zone = HostedZone.FromHostedZoneAttributes(this, "NetbirdZone", new HostedZoneAttributes
        {
            HostedZoneId = Shared.HostedZoneId,
            ZoneName = Shared.HostedZoneName,
        });
        _ = new ARecord(this, "ControlPlaneDnsRecord", new ARecordProps
        {
            Zone = zone,
            Target = RecordTarget.FromIpAddresses(eip.Ref),
            Ttl = Duration.Minutes(5),
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

        // CPU utilization alarm -> shared Slack topic. Satisfies the Drata "Infrastructure Instance
        // CPU Monitored" control and matches the platform's alarm-to-Slack convention.
        var cpuAlarm = new Alarm(this, "ControlPlaneCpuAlarm", new AlarmProps
        {
            Metric = new Metric(new MetricProps
            {
                Namespace = "AWS/EC2",
                MetricName = "CPUUtilization",
                DimensionsMap = new Dictionary<string, string> { { "InstanceId", instance.InstanceId } },
                Period = Duration.Minutes(5),
                Statistic = "Average",
            }),
            Threshold = 80,
            EvaluationPeriods = 2,
            DatapointsToAlarm = 2,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
            TreatMissingData = TreatMissingData.IGNORE,
        });
        var slackTopic = Shared.SlackNotifierTopic(this);
        cpuAlarm.AddAlarmAction(new SnsAction(slackTopic));
        cpuAlarm.AddOkAction(new SnsAction(slackTopic));

        // JWKS key-refresh alarm (2026-08-26 incident). When Entra rotates its token-signing key,
        // the management server logs this exact line for every failed login/API call/peer
        // handshake. IdpSignKeyRefreshEnabled=true (set in control-plane-user-data.sh) makes the
        // server self-heal by refetching the JWKS, but alarm anyway: it is cheap insurance against
        // a future Netbird version reverting the default, a misconfigured re-provision, or the
        // refetch itself failing (e.g. egress to login.microsoftonline.com blocked).
        var jwksErrorFilter = new MetricFilter(this, "JwksKeyfuncErrorsFilter", new MetricFilterProps
        {
            LogGroup = managementLogGroup,
            FilterPattern = FilterPattern.Literal("\"unable to find appropriate key\""),
            MetricNamespace = "Netbird/ControlPlane",
            MetricName = "JwksKeyfuncErrors",
            MetricValue = "1",
            DefaultValue = 0,
        });
        var jwksErrorAlarm = new Alarm(this, "JwksKeyfuncErrorsAlarm", new AlarmProps
        {
            Metric = jwksErrorFilter.Metric(new MetricOptions
            {
                Period = Duration.Minutes(5),
                Statistic = "Sum",
            }),
            Threshold = 1,
            EvaluationPeriods = 1,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
            TreatMissingData = TreatMissingData.NOT_BREACHING,
        });
        jwksErrorAlarm.AddAlarmAction(new SnsAction(slackTopic));
        jwksErrorAlarm.AddOkAction(new SnsAction(slackTopic));

        _ = new CfnOutput(this, "ControlPlaneIp", new CfnOutputProps
        {
            Value = eip.Ref,
            Description = "Control plane EIP (the netbird.autoguru.com.au A record points here)",
        });
        _ = new CfnOutput(this, "ControlPlaneInstanceId", new CfnOutputProps
        {
            Value = instance.InstanceId,
            Description = "SSM Session Manager target for post-deploy Netbird setup script",
        });
    }
}
