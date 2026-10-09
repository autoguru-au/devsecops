# Netbird control-plane restore rehearsal (COM-234), October 2026

**Status: prepared, not run.** The scripts are ready and tested offline. The rehearsal needs one
step from a shared-account admin (Anthony): run one script, about 5 minutes, before Thu 15 Oct.
Guillermo then runs the second script with the shared Developer session to validate the copy and
remove it. Due date: 16 Oct, before the control-plane change window on Tue 20 Oct 18:00 UTC
([COM-236](https://autoguru.atlassian.net/browse/COM-236)).

Jira: [COM-234](https://autoguru.atlassian.net/browse/COM-234) (parent
[COM-140](https://autoguru.atlassian.net/browse/COM-140), findings in
[COM-219](https://autoguru.atlassian.net/browse/COM-219)).

## 1. Goal and acceptance criteria

Prove that the control plane can be rebuilt from backup. Restore the latest AWS Backup recovery
point of the control-plane instance `i-06766fc0cc0e7815e` into a throwaway instance, then show:

- `store.db` and `management.json` are present;
- the datastore key matches the datastore;
- management starts without decrypt errors, with the expected peers and settings;
- the timings (RTO) and the age of the restored data (RPO) are recorded;
- the throwaway instance and everything created for it are removed.

## 2. Why the rehearsal has two steps

The shared Developer permission set cannot do the launch. These facts were checked on 2026-10-09
with read-only calls, the IAM policy simulator and EC2 `--dry-run`:

| Call | Result with shared Developer |
| --- | --- |
| `ec2 run-instances` with any instance profile | `UnauthorizedOperation`: not authorized to perform `iam:PassRole` |
| `backup start-restore-job` | needs `iam:PassRole` on its `IamRoleArn`; Developer has no `iam:PassRole` in this account (simulator: implicitDeny for `AWSBackup`, the SSM-only role and the control-plane role) |
| `ec2 run-instances` without a profile | allowed (dry run passes), but the copy cannot use SSM |
| `iam get-instance-profile` | `AccessDenied` (PowerUserAccess excludes IAM) |

Without an instance profile the copy cannot register with SSM: the Default Host Management
Configuration setting is `$None` in this account. So the launch needs an admin, and the validation
and cleanup do not (PowerUserAccess can describe, run SSM commands, terminate and delete).

### AWS Backup restore jobs cannot be used for this, or for a real restore today

1. **The `AWSBackup` role has no restore permissions.** It is created by the StackSet
   `foundation-operational-aws-backup` (autoguru repo,
   `eng/Infrastructure/src/01-cloud-foundation/03-stack-sets/05-operational-accounts-stack-instances/operational-08-aws-backup-new.yml`)
   with `AWSBackupServiceRolePolicyForBackup` and `AWSBackupServiceRolePolicyForS3Backup` only.
   The simulator returns implicitDeny for `ec2:RunInstances` and `iam:PassRole`, so an EC2 restore
   job that passes this role fails, even when an admin starts it. This is deliberate:
   [autoguru PR #6285](https://github.com/autoguru-au/autoguru/pull/6285) (AG-21069) records that
   widening this role "would grant restore permissions to every consumer of that role", and uses
   per-service restore roles instead. There is no EC2 restore role in the shared account.
2. **AWS Backup restores an EC2 instance only with the backed-up instance profile.** The ag-vault
   restore-framework evidence (restore job `a40b3c32-964a-4b26-9d62-d0a6ef7408fd`, 2026-08-13)
   failed with "AWS Backup does not permit attaching a new instance profile to an EC2 instance".
   For the control plane that profile is the production role, which can read the Entra secret.

The recovery point is an AMI. Launching that AMI with `RunInstances` restores the same disk, with
the profile and network we choose. The rehearsal does that.

## 3. The throwaway copy

| Property | Value | Why |
| --- | --- | --- |
| Source | latest COMPLETED recovery point of `i-06766fc0cc0e7815e` in vault `AWSBackup` | the restore path a real recovery uses |
| Subnet | `subnet-0e02fd563212fd98c` (`autoguru-shared-private-sn-a`), default route to a NAT gateway | not reachable from the internet |
| Public IP / EIP | none / none | never takes over the production address |
| Security group | new `netbird-restore-rehearsal-COM-234`, **no inbound rules**, outbound TCP 443 to `0.0.0.0/0` only | SSM needs outbound HTTPS (see the residual below) |
| Instance profile | `AmazonSSMRoleForInstancesQuickSetup` (SSM core and patch policies only) | never the production role, which can read `/netbird/control-plane/entra-client-secret` |
| IMDS | IMDSv2 required, hop limit 2 | same as production |
| EBS | root volume encrypted with the recovery point's CMK (`alias/netbird-control-plane-ebs-key`), deleted on termination | same as production |
| User data | none | nothing re-runs setup; step 2 checks the attribute and IMDS |
| Shutdown behaviour | terminate | a shutdown inside the copy removes it |
| Tags | `Purpose=COM-234-restore-rehearsal`, `DeleteAfter=2026-10-17`, `Name`, `SourceRecoveryPoint`, `RecoveryPointCreated`, `RehearsalStartUtc` | cleanup finds everything by `Purpose`; **no `backup=true`** |
| CPU alarm | `netbird-restore-rehearsal-COM-234-cpu` to the Slack topic, alarm action only | the production instance has one; deleted in step 2 |

**Nothing on the copy acts on production:**

- The org backup plan selects `backup=true` only, so the copy is never backed up. The
  `ec2-resource-tagger` Lambda only copies instance tags to volumes and network interfaces.
- None of the 16 SSM associations in the account targets the copy: they target other instance ids
  or a no-op automation tag.
- The containers start on boot from the restored compose project. Management, signal, relay and
  coturn only listen. No inbound rule exists, so no peer can reach the copy. The public name
  resolves to the production EIP, so the checks talk to `127.0.0.1` only (`curl --resolve`).
- The dashboard's certbot renews 30 days before expiry. The certificate expires on 2026-12-06, so
  no renewal is due before 2026-11-06. Even if one ran, the HTTP challenge would reach production,
  not the copy, and fail.

**Residual (accepted):** the copy can reach any address on TCP 443. A security group cannot filter
by domain, and the VPC has no SSM interface endpoints (only an S3 gateway endpoint), so SSM needs
HTTPS through the NAT. NetBird management also needs HTTPS at startup: v0.74.7 exits if it cannot
fetch the Entra OIDC discovery document (`management/cmd/management.go`, `ApplyOIDCConfig`) or
check for a GeoLite update (`management/internals/server/modules.go`, `log.Fatalf`). On 443 the
copy can fetch the Entra discovery document and keys, check for GeoLite updates, and send NetBird's
anonymous usage metrics. None of these writes to any AutoGuru system. This is acceptable for a copy
that lives about one hour, has no inbound rules, no public IP, an SSM-only role, and no backup tag.
Every other port is closed. Step 2 proves that TCP 80 is blocked.

## 4. Runbook

Run both steps from a checkout of this repository on `main`.

### Step 1, shared admin (Anthony), about 5 minutes

```bash
AWS_PROFILE=<shared admin profile> bash netbird/scripts/restore-rehearsal-launch.sh
```

The script:

1. checks the account (`791686214595`) and stops if a rehearsal copy is already running (it prints
   that copy instead);
2. finds the latest COMPLETED recovery point and checks that its AMI is available and encrypted;
3. checks that the subnet has no automatic public IPs and a NAT default route;
4. checks that the SSM-only profile exists and is not the production role;
5. creates the security group, removes its default egress rule, allows TCP 443 out only, and
   checks there are no inbound rules;
6. launches the copy (no user data), waits for `running`, and checks: no public IP, no EIP, only
   the rehearsal group, the SSM-only profile, IMDSv2 required, empty user-data attribute, volumes
   encrypted with the recovery point's key. Any mismatch terminates the copy;
7. creates the CPU alarm and prints `INSTANCE_ID=i-...`.

If a step fails after it created something, the script removes what this run created. A second run
reuses the security group and does not launch a second copy.

Dry run (creates nothing): `bash netbird/scripts/restore-rehearsal-launch.sh --dry-run`.

### Step 2, shared Developer (Guillermo), 15 to 25 minutes

Run it the same day, soon after step 1:

```bash
AWS_PROFILE=shared bash netbird/scripts/restore-rehearsal-validate-and-cleanup.sh
```

The script refuses `i-06766fc0cc0e7815e` before any AWS call and acts only on an instance tagged
`Purpose=COM-234-restore-rehearsal`. Then it:

1. checks the posture of the copy through the API again. If anything fails, it skips the checks on
   the copy and goes to cleanup;
2. waits for SSM `Online` and runs the read-only checks below through Run Command
   (`AWS-RunShellScript`). On the copy the script stops if its own instance id is the production one;
3. terminates the copy, deletes the alarm, any leftover volume and the security group, and prints
   the cleanup proof;
4. prints the timings, the RTO and the RPO.

Cleanup also runs after an unexpected exit, for example an expired SSO session. If the session
ends in the middle, run `aws sso login --profile shared`, then run the script again with
`--cleanup-only`. To see exactly what runs on the copy:
`bash netbird/scripts/restore-rehearsal-validate-and-cleanup.sh --print-remote-script`.

Exit status: 0 = all checks passed and cleanup is complete, 1 = a check failed (cleanup complete),
2 = cleanup incomplete, 3 = refused.

## 5. What step 2 checks

Every check prints `CHECK <name> PASS|WARN|FAIL <detail>`. The output has names, counts, dates,
fingerprints, hash prefixes and booleans only. It never prints a secret, a user email or a name.

| Check | PASS when |
| --- | --- |
| `no_public_ip`, `no_eip`, `private_subnet`, `no_inbound`, `ssm_only_profile`, `imdsv2`, `no_user_data_attribute`, `ebs_encrypted` | the API shows the posture in section 3 |
| `ssm_online` | the SSM agent reports `Online` within 15 minutes |
| `no_user_data` | IMDS `user-data` returns 404 on the copy |
| `no_netbird_client` | no NetBird client service runs on the copy |
| `egress_only_443` | TCP 80 to example.com is blocked (TCP 443 is the accepted residual) |
| `containers_up` | management, signal, relay, dashboard and coturn are running |
| `management_api` | unauthenticated `GET /api/users` on `:33073` returns 401 with a valid certificate (waits up to 10 minutes) |
| `management_no_decrypt_errors` | management logs since this boot have no decrypt error |
| `management_json` | `management.json` (the file mounted into the management container) has `DataStoreEncryptionKey`, `Datadir`, `HttpConfig`, `Signal`, `StoreConfig` |
| `idp_signkey_refresh` | `HttpConfig.IdpSignKeyRefreshEnabled` is true (the 2026-08-26 JWKS fix, in `management.json` only) |
| `pkce_redirect_ports` (WARN) | the PKCE redirect ports are 53000, 8976, 35000, 43000 |
| `datastore_key_present` | the key decodes to 32 bytes; a 12-character SHA-256 prefix is printed |
| `store_db` | `store.db` opens read-only with 1 account, at least 1 user and 1 peer |
| `datastore_key_matches` | every encrypted user email and name in `store.db` opens (AES-256-GCM, tag verified) with the key from `management.json`. Plaintext stays in memory; only counts are printed |
| `peer_count`, `setup_key_peers`, `routers_bound_to_routing_peer` (WARN) | the restored counts equal the reference: 69 peers, 1 setup-key peer, 6 network routers bound to peer `d9gbdrj6leos73em57u0` (production, 2026-10-09). A WARN is expected if peers joined or left after the recovery point |
| `tls_cert_files` | `fullchain.pem` and `privkey.pem` exist in the Let's Encrypt volume with 14 days or more left |
| `tls_served_443`, `tls_served_33073`, `tls_served_10000` | each port serves the certificate on disk |

It also prints the account settings (peer login expiration, inactivity expiration, JWT group sync),
the `setup.env` files and whether they record the datastore key, the systemd timers, and the
management restart count. Reference from COM-219: login expiration 24 h, inactivity expiration 8 h,
JWT group sync and propagation on.

## 6. Timings, RTO and RPO

Step 2 prints these times (UTC):

- restore start: when step 1 sent `RunInstances` (tag `RehearsalStartUtc`);
- instance launched (EC2 `LaunchTime`), copy booted (from the uptime), management container
  started (`docker inspect`), SSM online (first seen), API 401 (first seen).

**RTO on the copy** = restore start to management running on the restored data. A real restore
adds the steps in section 8.

**RPO** = age of the restored data at the restore start = restore start minus the recovery point
creation time. The control plane is backed up once a day at 15:00 UTC (plus a monthly job at 21:00
UTC). Measured from the backup jobs between 2026-09-09 and 2026-10-08: 31 of 31 completed, the
longest gap between two backup starts is 24.0 h, and a job takes 49 to 137 minutes (median 53).
On 2026-10-09 at 12:00 UTC the newest recovery point (`ami-092525d190e6c0962`, started
2026-10-08 15:00 UTC, completed 15:51 UTC) was 21 h old. **Worst-case RPO: about 24 h of
changes**, plus up to about 2 h while a job runs.

## 7. Offline tests (2026-10-09)

| Test | Result |
| --- | --- |
| `shellcheck -S style` (v0.11.0) on both scripts and on the script that runs on the copy | clean |
| `bash -n` on all three | clean |
| `restore-rehearsal-launch.sh --dry-run --skip-instance-profile` as shared Developer | PASS: `create-security-group` and `run-instances` return `DryRunOperation` (request valid and permitted, with the real AMI `ami-092525d190e6c0962`, the private subnet and the tag set) |
| `restore-rehearsal-launch.sh --dry-run` as shared Developer | fails closed at `iam:GetInstanceProfile`: Developer cannot use the profile, as expected |
| Launch script against an AWS CLI mock | happy path, rollback (a failure after launch terminates the copy), re-run (no second launch), flag guard |
| Validate script `--instance-id i-06766fc0cc0e7815e` | refused before any AWS call (exit 3) |
| Validate script `--cleanup-only` as shared Developer, nothing deployed | proof: 0 instances, 0 volumes, 0 network interfaces, 0 security groups, 0 alarms, nothing tagged |
| Validate script against an AWS CLI mock | full run; posture failure skips SSM and still cleans up; an expired session mid-run still cleans up; the SSM payload is valid JSON (8.5 KB) and decodes to the exact check script |
| Datastore checks on synthetic `store.db` and `management.json` (AES-256-GCM vectors from Node.js `crypto`) | right key: PASS; one email under another key: FAIL; output contains no plaintext and no key |

Not tested offline: the docker, curl and openssl parts on the copy. They run for the first time in
step 2, read-only.

## 8. What a real restore adds

This is the rollback path for [COM-236](https://autoguru.atlassian.net/browse/COM-236) when the
on-box tar is not enough. **Do not rely on an AWS Backup restore job** (section 2). An admin
launches the recovery-point AMI with `RunInstances`:

1. Stop the broken production instance first, so only one control plane holds the identity.
2. Launch the AMI into `autoguru-shared-public-sn-a` (`subnet-0549ad9cb3abdea15`) with the
   production security group, the production instance profile, `t3.small`, IMDSv2 required and no
   user data.
3. Move the EIP: find it with
   `aws ec2 describe-addresses --filters Name=instance-id,Values=i-06766fc0cc0e7815e`, then
   `aws ec2 associate-address --allocation-id <id> --instance-id <new> --allow-reassociation`.
   DNS does not change: the `netbird.autoguru.com.au` A record points to the EIP.
4. Certificates: the disk has the certificate that existed at the recovery point. If certbot
   renewed after that, management and signal serve the older one until the TLS reload job
   (`netbird-control-plane-tls-reload`, after PR #26 is deployed) or a manual restart picks up a
   renewal. Check `:443`, `:33073` and `:10000` with `openssl s_client`.
5. Verify: dashboard 200, `:33073/api/users` 401, peers reconnect in the dashboard, the routing
   peer connects (its network routers are bound to its peer id, which the restore keeps), SSO login
   on Windows and Mac.
6. Data loss: everything after the recovery point is gone. Peers enrolled after it must enrol again.
7. Afterwards: tag the new instance `backup=true`, and plan the CloudFormation reconciliation. The
   `NetbirdControlPlaneStack` still references the old instance id, so do not deploy the stack
   until that is planned.

## 9. Results

To be filled in from the step 2 output after the run.

| Item | Value |
| --- | --- |
| Recovery point | |
| Restore start (UTC) | |
| Management running (UTC) | |
| RTO on the copy | |
| RPO | |
| Checks | |
| Cleanup proof | |
