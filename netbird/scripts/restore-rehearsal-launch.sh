#!/bin/bash
# COM-234 restore rehearsal, step 1 of 2: launch a THROWAWAY copy of the Netbird control plane from
# its latest AWS Backup recovery point. Needs a shared-account (791686214595) ADMIN session.
# Step 2 (restore-rehearsal-validate-and-cleanup.sh) runs with the shared Developer session: it
# validates the copy through SSM Run Command, records the timings and removes everything.
#
# Why an admin session: any instance profile on the copy needs iam:PassRole, which the shared
# Developer permission set does not have. Without a profile the copy cannot register with SSM:
# Default Host Management Configuration is off in this account.
#
# Why RunInstances instead of an AWS Backup restore job: the EC2 recovery point is an AMI, and this
# launches the same AMI with the same disk. A restore job cannot do this rehearsal safely:
#   - AWS Backup only restores an EC2 instance with the backed-up instance profile (the production
#     control-plane role, which can read the Entra secret). It refuses a different profile.
#   - The account's AWSBackup role has no restore permissions (no ec2:RunInstances, no iam:PassRole).
#
# The copy:
#   - private subnet autoguru-shared-private-sn-a (egress through the NAT), no public IP, no EIP;
#   - a new security group with NO inbound rules and outbound TCP 443 only (to any address);
#   - instance profile AmazonSSMRoleForInstancesQuickSetup (SSM only), never the production role;
#   - IMDSv2 required, EBS encrypted with the recovery point's key, NO user data;
#   - tags Purpose=COM-234-restore-rehearsal and DeleteAfter=2026-10-17, and NOT backup=true (the
#     org backup plan selects backup=true, so the copy is never backed up);
#   - a CPU alarm to the Slack topic, like the production instance has (Drata CPU monitoring).
#     Step 2 deletes it.
#
# Idempotent: a second run reuses the security group and prints the existing copy instead of
# launching another one. If a step fails after this run created something, the script removes it.
#
# Usage:
#   AWS_PROFILE=<shared admin profile> bash netbird/scripts/restore-rehearsal-launch.sh
#   AWS_PROFILE=shared bash netbird/scripts/restore-rehearsal-launch.sh --dry-run [--skip-instance-profile]
#     --dry-run checks every create call with the EC2 DryRun flag and creates nothing.
#     --skip-instance-profile (dry-run only) leaves out the profile, so a Developer session can prove
#     that the rest of the request is valid.
set -Eeuo pipefail

REGION=ap-southeast-2
ACCOUNT=791686214595
VPC_ID="vpc-064a7525a3bcc4667"
# autoguru-shared-private-sn-a: default route to a NAT gateway, no internet gateway.
SUBNET_ID="subnet-0e02fd563212fd98c"
SOURCE_INSTANCE="i-06766fc0cc0e7815e"
VAULT=AWSBackup
INSTANCE_PROFILE=AmazonSSMRoleForInstancesQuickSetup
PRODUCTION_ROLE_PREFIX=NetbirdControlPlaneStack-ControlPlaneRole
INSTANCE_TYPE=t3.small
PURPOSE=COM-234-restore-rehearsal
DELETE_AFTER=2026-10-17
NAME=netbird-restore-rehearsal-COM-234
SG_NAME=netbird-restore-rehearsal-COM-234
ALARM_NAME=netbird-restore-rehearsal-COM-234-cpu
SLACK_TOPIC_EXPORT=anz-shared-SlackNotifierTopicArn

# Timestamps in UTC whatever the laptop clock says; no pager; no MSYS path mangling on Git Bash.
export AWS_REGION=$REGION AWS_DEFAULT_REGION=$REGION TZ=UTC AWS_PAGER="" MSYS_NO_PATHCONV=1

DRY_RUN=false
SKIP_PROFILE=false
for arg in "$@"; do
  case $arg in
    --dry-run) DRY_RUN=true ;;
    --skip-instance-profile) SKIP_PROFILE=true ;;
    -h|--help) sed -n '2,35p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $arg" >&2; exit 64 ;;
  esac
done
if $SKIP_PROFILE && ! $DRY_RUN; then
  echo "--skip-instance-profile is only allowed with --dry-run: the copy needs the SSM-only profile." >&2
  exit 64
fi

log() { echo "[$(date -u +%H:%M:%SZ)] $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }
trap 'echo "ERROR: line $LINENO: \"$BASH_COMMAND\" exited with status $?" >&2' ERR

# Resources created by THIS run, removed again if a later step fails.
CREATED_SG=""
LAUNCHED_ID=""
on_exit() {
  local rc=$?
  [ "$rc" -eq 0 ] && return 0
  if [ -n "$LAUNCHED_ID" ]; then
    echo "Failure after launch: terminating $LAUNCHED_ID (fail closed)." >&2
    aws ec2 terminate-instances --instance-ids "$LAUNCHED_ID" --output text >/dev/null \
      || echo "Could not terminate $LAUNCHED_ID. Terminate it by hand." >&2
    aws cloudwatch delete-alarms --alarm-names "$ALARM_NAME" \
      || echo "Could not delete alarm $ALARM_NAME. Delete it by hand." >&2
    echo "Then run restore-rehearsal-validate-and-cleanup.sh --cleanup-only to remove the security group." >&2
  elif [ -n "$CREATED_SG" ]; then
    echo "Failure before launch: deleting security group $CREATED_SG created by this run." >&2
    aws ec2 delete-security-group --group-id "$CREATED_SG" \
      || echo "Could not delete $CREATED_SG. Delete it by hand." >&2
  fi
  exit "$rc"
}
trap on_exit EXIT

# Runs an EC2 call with --dry-run. Passes only when AWS answers DryRunOperation.
dry_run_check() {
  local out
  if out=$(aws "$@" --dry-run 2>&1); then
    die "dry run of '$1 $2' returned success instead of DryRunOperation"
  fi
  case $out in
    *DryRunOperation*) log "  dry run OK: $1 $2 (request is valid and permitted)" ;;
    *) echo "$out" >&2; die "dry run of '$1 $2' was refused (see the message above)" ;;
  esac
}

log "COM-234 restore rehearsal, launch step (dry run: $DRY_RUN)"

# 0. Right account.
caller=$(aws sts get-caller-identity --query Arn --output text)
account=$(aws sts get-caller-identity --query Account --output text)
[ "$account" = "$ACCOUNT" ] || die "this session is in account $account, expected $ACCOUNT (shared)"
log "Caller: $caller"

# 1. A copy is already running: print it and stop (idempotent re-run).
existing=$(aws ec2 describe-instances \
  --filters "Name=tag:Purpose,Values=$PURPOSE" "Name=instance-state-name,Values=pending,running,stopping,stopped" \
  --query 'Reservations[].Instances[].InstanceId' --output text)
if [ -n "$existing" ] && [ "$existing" != "None" ]; then
  log "A rehearsal copy already exists: $existing. Not launching another one."
  echo "INSTANCE_ID=$existing"
  exit 0
fi

# 2. Latest COMPLETED recovery point of the control plane in the local vault.
rp=$(aws backup list-recovery-points-by-resource \
  --resource-arn "arn:aws:ec2:$REGION:$ACCOUNT:instance/$SOURCE_INSTANCE" \
  --query "reverse(sort_by(RecoveryPoints[?Status=='COMPLETED' && BackupVaultName=='$VAULT'], &CreationDate))[0].[RecoveryPointArn,CreationDate,EncryptionKeyArn]" \
  --output text)
read -r RP_ARN RP_CREATED RP_KEY <<<"$rp"
case $RP_ARN in arn:aws:ec2:*:image/ami-*) ;; *) die "no completed recovery point found (got: $rp)" ;; esac
AMI_ID=${RP_ARN##*/}
RP_COMPLETED=$(aws backup describe-recovery-point --backup-vault-name "$VAULT" --recovery-point-arn "$RP_ARN" \
  --query CompletionDate --output text)
image=$(aws ec2 describe-images --image-ids "$AMI_ID" \
  --query 'Images[0].[State,RootDeviceName,BlockDeviceMappings[0].Ebs.Encrypted]' --output text)
read -r AMI_STATE ROOT_DEVICE ROOT_ENCRYPTED <<<"$image"
[ "$AMI_STATE" = available ] || die "recovery-point AMI $AMI_ID is $AMI_STATE, not available"
[ "$ROOT_ENCRYPTED" = True ] || die "recovery-point AMI $AMI_ID root snapshot is not encrypted"
log "Recovery point: $AMI_ID, created $RP_CREATED, completed $RP_COMPLETED, key ${RP_KEY##*/}"

# 3. The subnet is private: no public IPs by default, default route through a NAT, no IGW.
subnet=$(aws ec2 describe-subnets --subnet-ids "$SUBNET_ID" \
  --query 'Subnets[0].[VpcId,MapPublicIpOnLaunch]' --output text)
read -r SUBNET_VPC MAP_PUBLIC <<<"$subnet"
[ "$SUBNET_VPC" = "$VPC_ID" ] || die "subnet $SUBNET_ID is in $SUBNET_VPC, not $VPC_ID"
[ "$MAP_PUBLIC" = False ] || die "subnet $SUBNET_ID assigns public IPs on launch"
default_route=$(aws ec2 describe-route-tables --filters "Name=association.subnet-id,Values=$SUBNET_ID" \
  --query "RouteTables[0].Routes[?DestinationCidrBlock=='0.0.0.0/0'].[NatGatewayId,GatewayId]" --output text)
case $default_route in
  nat-*) log "Subnet $SUBNET_ID is private (default route: ${default_route%%[[:space:]]*})" ;;
  *) die "subnet $SUBNET_ID default route is '$default_route', expected a NAT gateway" ;;
esac

# 4. The SSM-only instance profile exists and is not the production control-plane role.
if ! $SKIP_PROFILE; then
  profile_role=$(aws iam get-instance-profile --instance-profile-name "$INSTANCE_PROFILE" \
    --query 'InstanceProfile.Roles[0].RoleName' --output text)
  case $profile_role in
    "$PRODUCTION_ROLE_PREFIX"*|None|"") die "instance profile $INSTANCE_PROFILE has role '$profile_role'" ;;
  esac
  log "Instance profile: $INSTANCE_PROFILE (role $profile_role)"
fi

# 5. Slack topic for the CPU alarm (same topic as the production alarms).
SLACK_TOPIC=$(aws cloudformation list-exports --query "Exports[?Name=='$SLACK_TOPIC_EXPORT'].Value" --output text)
case $SLACK_TOPIC in arn:aws:sns:*) ;; *) die "export $SLACK_TOPIC_EXPORT not found" ;; esac

# 6. Security group: no inbound rules, outbound TCP 443 to 0.0.0.0/0 and nothing else.
verify_sg() {
  local sg=$1 ingress egress
  ingress=$(aws ec2 describe-security-group-rules --filters "Name=group-id,Values=$sg" \
    --query 'length(SecurityGroupRules[?!IsEgress])' --output text)
  egress=$(aws ec2 describe-security-group-rules --filters "Name=group-id,Values=$sg" \
    --query 'SecurityGroupRules[?IsEgress].[IpProtocol,FromPort,ToPort,CidrIpv4,CidrIpv6,ReferencedGroupInfo.GroupId,PrefixListId]' \
    --output text)
  [ "$ingress" = 0 ] || die "security group $sg has $ingress inbound rule(s); it must have none"
  [ "$(printf '%s\n' "$egress" | tr -s '[:space:]' ' ' | sed 's/ $//')" = "tcp 443 443 0.0.0.0/0 None None None" ] \
    || die "security group $sg egress is not exactly TCP 443 to 0.0.0.0/0: $egress"
  log "Security group $sg: 0 inbound rules, egress TCP 443 only"
}
SG_ID=$(aws ec2 describe-security-groups --filters "Name=vpc-id,Values=$VPC_ID" "Name=group-name,Values=$SG_NAME" \
  --query 'SecurityGroups[0].GroupId' --output text)
SG_TAGS="[{Key=Name,Value=$NAME},{Key=Purpose,Value=$PURPOSE},{Key=DeleteAfter,Value=$DELETE_AFTER}]"
if [ "$SG_ID" = None ] || [ -z "$SG_ID" ]; then
  if $DRY_RUN; then
    dry_run_check ec2 create-security-group --group-name "$SG_NAME" --vpc-id "$VPC_ID" \
      --description "COM-234 restore rehearsal: no inbound, egress TCP 443 only" \
      --tag-specifications "ResourceType=security-group,Tags=$SG_TAGS"
    # The dry run of RunInstances needs an existing group id. The VPC default group is only named in
    # that request; it is never attached to anything.
    SG_ID=$(aws ec2 describe-security-groups --filters "Name=vpc-id,Values=$VPC_ID" "Name=group-name,Values=default" \
      --query 'SecurityGroups[0].GroupId' --output text)
    log "Dry run: RunInstances request checked with the VPC default group $SG_ID as a stand-in"
  else
    SG_ID=$(aws ec2 create-security-group --group-name "$SG_NAME" --vpc-id "$VPC_ID" \
      --description "COM-234 restore rehearsal: no inbound, egress TCP 443 only" \
      --tag-specifications "ResourceType=security-group,Tags=$SG_TAGS" --query GroupId --output text)
    CREATED_SG=$SG_ID
    log "Created security group $SG_ID"
    # A new group allows all egress. Remove every egress rule, then allow TCP 443 only.
    default_egress=$(aws ec2 describe-security-group-rules --filters "Name=group-id,Values=$SG_ID" \
      --query 'SecurityGroupRules[?IsEgress].SecurityGroupRuleId' --output text)
    for rule in $default_egress; do
      aws ec2 revoke-security-group-egress --group-id "$SG_ID" --security-group-rule-ids "$rule" --output text >/dev/null
    done
    aws ec2 authorize-security-group-egress --group-id "$SG_ID" \
      --ip-permissions 'IpProtocol=tcp,FromPort=443,ToPort=443,IpRanges=[{CidrIp=0.0.0.0/0,Description=HTTPS for SSM and the Entra OIDC discovery management needs at startup}]' \
      --output text >/dev/null
    verify_sg "$SG_ID"
  fi
else
  log "Reusing security group $SG_ID"
  verify_sg "$SG_ID"
fi

# 7. Launch the copy. No user data: none is passed, and step 2 checks the attribute is empty.
START_UTC=$(date -u +%Y-%m-%dT%H:%M:%SZ)
TAGS="[{\"Key\":\"Name\",\"Value\":\"$NAME\"},{\"Key\":\"Purpose\",\"Value\":\"$PURPOSE\"},{\"Key\":\"DeleteAfter\",\"Value\":\"$DELETE_AFTER\"},{\"Key\":\"SourceInstance\",\"Value\":\"$SOURCE_INSTANCE\"},{\"Key\":\"SourceRecoveryPoint\",\"Value\":\"$AMI_ID\"},{\"Key\":\"RecoveryPointCreated\",\"Value\":\"$RP_CREATED\"},{\"Key\":\"RehearsalStartUtc\",\"Value\":\"$START_UTC\"}]"
run_args=(ec2 run-instances
  --image-id "$AMI_ID"
  --instance-type "$INSTANCE_TYPE"
  --count 1
  --network-interfaces "[{\"DeviceIndex\":0,\"SubnetId\":\"$SUBNET_ID\",\"Groups\":[\"$SG_ID\"],\"AssociatePublicIpAddress\":false,\"DeleteOnTermination\":true}]"
  --metadata-options "HttpTokens=required,HttpEndpoint=enabled,HttpPutResponseHopLimit=2"
  --block-device-mappings "[{\"DeviceName\":\"$ROOT_DEVICE\",\"Ebs\":{\"Encrypted\":true,\"DeleteOnTermination\":true,\"VolumeType\":\"gp3\"}}]"
  --instance-initiated-shutdown-behavior terminate
  --tag-specifications "[{\"ResourceType\":\"instance\",\"Tags\":$TAGS},{\"ResourceType\":\"volume\",\"Tags\":$TAGS},{\"ResourceType\":\"network-interface\",\"Tags\":$TAGS}]"
  --client-token "COM-234-$AMI_ID-$SG_ID")
$SKIP_PROFILE || run_args+=(--iam-instance-profile "Name=$INSTANCE_PROFILE")

if $DRY_RUN; then
  dry_run_check "${run_args[@]}"
  log "Dry run complete. Nothing was created."
  exit 0
fi

log "Launching the copy from $AMI_ID (restore start $START_UTC)"
LAUNCHED_ID=$(aws "${run_args[@]}" --query 'Instances[0].InstanceId' --output text)
case $LAUNCHED_ID in i-*) ;; *) die "run-instances returned no instance id: $LAUNCHED_ID" ;; esac
[ "$LAUNCHED_ID" != "$SOURCE_INSTANCE" ] || die "refusing: the launched id equals the production instance"
log "Launched $LAUNCHED_ID, waiting for running"
aws ec2 wait instance-running --instance-ids "$LAUNCHED_ID"

# 8. Prove the posture of what was launched. Any mismatch terminates the copy (EXIT trap).
posture=$(aws ec2 describe-instances --instance-ids "$LAUNCHED_ID" \
  --query 'Reservations[0].Instances[0].[PublicIpAddress,MetadataOptions.HttpTokens,IamInstanceProfile.Arn,State.Name,SubnetId,LaunchTime]' \
  --output text)
read -r PUBLIC_IP TOKENS PROFILE_ARN STATE SUBNET LAUNCH_TIME <<<"$posture"
[ "$PUBLIC_IP" = None ] || die "the copy has a public IP ($PUBLIC_IP)"
[ "$TOKENS" = required ] || die "IMDSv2 is not required on the copy"
case $PROFILE_ARN in *"/$INSTANCE_PROFILE") ;; *) die "unexpected instance profile $PROFILE_ARN" ;; esac
[ "$SUBNET" = "$SUBNET_ID" ] || die "the copy is in $SUBNET, expected $SUBNET_ID"
groups=$(aws ec2 describe-instances --instance-ids "$LAUNCHED_ID" \
  --query 'Reservations[0].Instances[0].SecurityGroups[].GroupId' --output text)
[ "$groups" = "$SG_ID" ] || die "the copy has security groups '$groups', expected only $SG_ID"
eips=$(aws ec2 describe-addresses --filters "Name=instance-id,Values=$LAUNCHED_ID" --query 'length(Addresses)' --output text)
[ "$eips" = 0 ] || die "an Elastic IP is associated with the copy"
user_data=$(aws ec2 describe-instance-attribute --instance-id "$LAUNCHED_ID" --attribute userData \
  --query 'UserData.Value' --output text)
[ "$user_data" = None ] || [ -z "$user_data" ] || die "the copy has user data"
volumes=$(aws ec2 describe-volumes --filters "Name=attachment.instance-id,Values=$LAUNCHED_ID" \
  --query 'Volumes[].[VolumeId,Encrypted,KmsKeyId]' --output text)
while read -r vol enc key; do
  [ "$enc" = True ] || die "volume $vol is not encrypted"
  [ "$key" = "$RP_KEY" ] || die "volume $vol uses key $key, expected $RP_KEY"
done <<<"$volumes"
log "Posture OK: no public IP, no EIP, only $SG_ID, profile $INSTANCE_PROFILE, IMDSv2 required, no user data, EBS encrypted"

# 9. CPU alarm, as on the production instance (alarm action only: no OK message to Slack).
aws cloudwatch put-metric-alarm --alarm-name "$ALARM_NAME" \
  --alarm-description "COM-234 restore rehearsal copy $LAUNCHED_ID. Deleted by restore-rehearsal-validate-and-cleanup.sh." \
  --namespace AWS/EC2 --metric-name CPUUtilization --dimensions "Name=InstanceId,Value=$LAUNCHED_ID" \
  --statistic Average --period 300 --evaluation-periods 2 --datapoints-to-alarm 2 --threshold 80 \
  --comparison-operator GreaterThanThreshold --treat-missing-data ignore --alarm-actions "$SLACK_TOPIC" \
  --tags "Key=Purpose,Value=$PURPOSE" "Key=DeleteAfter,Value=$DELETE_AFTER"
log "CPU alarm $ALARM_NAME created"

INSTANCE_ID=$LAUNCHED_ID
LAUNCHED_ID=""   # launch complete: from here a failure must not terminate the copy
CREATED_SG=""
log "Done. Restore start $START_UTC, instance running since $LAUNCH_TIME (state $STATE)."
echo "INSTANCE_ID=$INSTANCE_ID"
echo "Next (shared Developer session): AWS_PROFILE=shared bash netbird/scripts/restore-rehearsal-validate-and-cleanup.sh"
