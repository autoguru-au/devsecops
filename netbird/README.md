# Netbird (self-hosted VPN)

Infrastructure as Code for AutoGuru's self-hosted [Netbird](https://netbird.io) deployment,
per [ADR-002: Netbird VPN Replacement](https://autoguru.atlassian.net/wiki/spaces/CS/pages/3515678725/ADR-002+Netbird+VPN+Replacement)
and the [Hybrid-ZTNA-Netbird Business Case](https://autoguru.atlassian.net/wiki/spaces/CS/pages/3515514895).

Netbird is the planned replacement for the Pritunl VPN. It is a true Layer-3 WireGuard VPN
and solves **native FQDN routing**: its Domain Resources (Networks) feature routes wildcard
FQDN traffic through a dedicated routing peer that carries a **static Elastic IP**. That IP
is added once to the Cloudflare origin allowlist and never changes (it survives EC2 Auto
Recovery), so Cloudflare-protected apps see a stable, managed egress instead of unmanageable
home ISP IPs.

Everything runs in the **autoguru-shared** account (`791686214595`), `ap-southeast-2`.

## Stacks

AWS CDK in **C#** (matching the rest of AutoGuru's CDK infrastructure), under `netbird/cdk`.
Two independent stacks (no "God" stack). Both run in the existing shared-services VPC
(`vpc-064a7525a3bcc4667`) public-subnet tier — alongside the Pritunl VPN they replace and the
shared RDS — each with a static EIP (no NAT). Reusing the shared VPC (rather than a dedicated one)
is deliberate: the peers inherit the vetted peering + RDS-allowlist fabric and the VPC's flow logs,
and the shared SQL Server RDS can admit them by security-group reference (how developers reach RDS
over the VPN). Each is otherwise self-contained: Amazon Linux 2023, Docker, IMDSv2 required,
encrypted EBS, CloudWatch auto-recovery, an SSM-only IAM role (no inbound SSH), and secrets read at
boot from Secrets Manager. The EC2 user-data lives in `netbird/scripts/*.sh` and is embedded into
the assembly at build time.

| Stack | Instance | Purpose |
| --- | --- | --- |
| `NetbirdControlPlaneStack` | t3.small / 30 GB gp3 | Management, signal, relay, dashboard, Coturn |
| `NetbirdRoutingPeerStack` | t3.micro / 30 GB gp3 | Routing agent + WireGuard data plane + Cloudflare egress EIP |

## Prerequisites

1. **Entra ID app registration** `Netbird` (single tenant, SPA) already exists (COM-141):
   client `5853144b-3c6f-4e39-a5b0-df1c3efcdcb1`, tenant `4542d3b9-a2ab-47a6-bc7a-1c25894c1adf`.
2. The routing-peer setup key secret `/netbird/routing-peer/setup-key` is created with a placeholder
   value by the routing-peer stack and overwritten with a real setup key (from the dashboard) after
   the control plane is set up.
3. The Entra app is a **public PKCE SPA client** (no client secret). It must have:
   - Under the **Single-page application** platform, redirect URIs
     `https://netbird.autoguru.com.au/auth` and `https://netbird.autoguru.com.au/silent-auth`
     (dashboard browser login).
   - Under the **Mobile and desktop applications** platform, loopback redirect URIs
     `http://localhost:53000`, `http://localhost:8976`, `http://localhost:35000` and
     `http://localhost:43000` (desktop-client PKCE callback). These must match
     `NETBIRD_AUTH_PKCE_REDIRECT_URL_PORTS` in `setup.env`; without them the desktop login fails
     with "no SSO provider returned from management". COM-188.

## Deploy

Deploys run via the [`netbird-deploy`](../.github/workflows/netbird-deploy.yml) workflow:
pull requests touching `netbird/**` get a `cdk diff`; deploys are a manual `workflow_dispatch`
(`action: deploy`, `stack: NetbirdControlPlaneStack | NetbirdRoutingPeerStack | both`). The workflow
assumes `AWS_DEPLOY_ROLE_ARN` in the shared account via OIDC.

### Deploy guard (COM-219)

A deploy has two jobs:

1. **Plan** creates a CloudFormation change set per stack and does not execute it
   (`cdk deploy --method=prepare-change-set`). [`ci/change_set_guard.py`](ci/change_set_guard.py)
   writes every resource change to the run summary and fails the run if the change set removes,
   replaces or may replace an `AWS::EC2::Instance`, `AWS::EC2::EIP`, `AWS::EC2::Volume`,
   `AWS::KMS::Key` or `AWS::SecretsManager::Secret`. The `allow_instance_replacement` input lets a
   planned, backed-up rebuild through; the reviewer must agree to it.
2. **Deploy** runs in the `netbird-production` GitHub environment: it waits for a required reviewer
   who did not start the run, and only runs from `main`. It checks that the environment still has
   those rules ([`ci/check-environment.sh`](ci/check-environment.sh)), re-checks the change sets and
   executes exactly those, by name.

Both stacks have termination protection, so `cdk destroy` and DeleteStack fail until someone turns
it off on purpose. One manual run executes at a time (`concurrency`).

Reviewer checklist before approving: the run started from `main`; the summary table shows the
expected stack only; no `BLOCKED` or `ALLOWED BY INPUT` rows unless this is the planned rebuild;
an on-demand backup of the control plane finished after the last change to it.

The environment is repository configuration, not code. A repo admin creates it before this
workflow reaches `main`: GitHub creates a missing environment with no rules the first time a job
names it (the check above then stops the deploy, but the approval gate is gone).

Before any deploy, read the diff for an `AWS::EC2::Instance` replacement:

- Both instances use a **pinned AMI** (`Shared.Al2023AmiId`). Do not go back to
  `MachineImage.LatestAmazonLinux2023()`: CloudFormation re-resolves it on every deploy, so a new
  AWS AMI turns any deploy into an instance replacement, which on the control plane wipes
  `/opt/netbird` and the management datastore. Patch the running instances in place with
  `dnf upgrade --releasever=latest` (AL2023 locks its package repository to the AMI's release).
- A control-plane user-data change is an in-place update: CloudFormation stops and starts the
  instance (a short outage; state on the EBS volume survives) and the new user-data does not run.
- The routing peer has `UserDataCausesReplacement`: any user-data difference replaces it and it
  re-enrols with a new peer identity. Any network router bound to the specific old peer (rather
  than to a peer group) stops routing until it is re-pointed (COM-175). Deploy it only when a
  change needs that.

Prerequisite: the shared account is already CDK-bootstrapped (the existing `SharedPlatformStack`
is deployed there via CDK), so no `cdk bootstrap` is needed.

Local (requires the .NET 10 SDK and the CDK CLI, with shared-account credentials). Diff only: do
not deploy from a workstation, it skips the change-set check and the approval.

```bash
cd netbird/cdk
dotnet build
npx cdk diff NetbirdControlPlaneStack
python3 -m unittest discover -s ../ci -v   # deploy guard tests, no AWS access needed
```

## Post-deploy setup (manual, once)

DNS is automatic: `netbird.autoguru.com.au` is a delegated public hosted zone in the shared account
(created by autoguru PR #5948, COM-144) and `NetbirdControlPlaneStack` manages the apex A record ->
control-plane EIP. After deploy, wait for it to resolve (Let's Encrypt needs the FQDN reachable).

1. **Install Netbird on the control plane** (SSM into the instance). The user-data has already written
   `/opt/netbird/setup.env` with the Entra OIDC settings and the image version pins. Run:
   ```bash
   NETBIRD_VERSION=v0.74.7   # MUST match the *_TAG pins in setup.env (dashboard v2.90.x pairs with 0.74.x)
   git clone --depth 1 --branch "$NETBIRD_VERSION" https://github.com/netbirdio/netbird/ /opt/netbird/src
   cp /opt/netbird/setup.env /opt/netbird/src/infrastructure_files/setup.env
   cd /opt/netbird/src/infrastructure_files && bash ./configure.sh
   cd artifacts && docker compose up -d
   ```
   All five containers (dashboard, management, signal, relay, coturn) come up and the dashboard gets a
   Let's Encrypt cert automatically. Verify with `curl https://netbird.autoguru.com.au` (expect 200).
   **Version coherence is mandatory**: management/signal/relay and the dashboard are a matched set
   (0.74.x pairs with dashboard v2.90.x; use v2.90.4+). Mixing release lines breaks login.
2. **Dashboard login**: browse to https://netbird.autoguru.com.au and sign in with Entra SSO. The first
   login bootstraps the org and makes you admin. (Uses the external Entra OIDC flow, not the bundled
   ZITADEL script; the dashboard is a public PKCE client with no secret, callbacks on `/auth`+`/silent-auth`.)
3. **Enrol the routing peer**: in the dashboard create a reusable Setup Key, store it in Secrets Manager
   at `/netbird/routing-peer/setup-key`, then re-run the routing-peer agent so it enrols (it reads the key
   at boot; `NB_MANAGEMENT_URL` must include the `:33073` management port).
4. **Route the internal apps**: in the dashboard create a Network, add a Domain Resource (a specific FQDN
   to start, or `*.autoguru.com.au`), assign the routing peer (Masquerade ON) plus an Access Policy from
   the client group to the resource group. Ask an admin to add the routing-peer EIP `54.253.102.22` to the
   Cloudflare origin allowlist.

## TLS certificate reload

The dashboard container's certbot renews the Let's Encrypt certificate (30 days before expiry) in
the shared `netbird-letsencrypt` volume. Management (`:33073`) and signal (`:10000`) load that
certificate once at startup and never reload it. Without a restart they keep serving the old
certificate until it expires, and clients then hang on Connect with no SSO prompt. This caused the
outage on 2026-10-07.

[`control-plane-tls-reload.sh`](scripts/control-plane-tls-reload.sh) handles it. It runs as the SSM
association `netbird-control-plane-tls-reload`, created by `NetbirdControlPlaneStack`: daily at
17:00 UTC, and also once whenever the association is created or updated (a deploy). It restarts
management or signal (`docker restart`, no recreate) only when the served certificate differs from
a trusted, valid, newer certificate on disk for the right hostname with a matching key, then checks
that the new certificate is being served. It publishes `TlsReloadFailures` and
`TlsCertDaysToExpiry` to the `Netbird/ControlPlane` namespace. Two alarms notify the Slack topic:

- `TlsReloadFailedAlarm`: the job reported a failure, including a served certificate with less
  than 14 days left (renewal has stopped).
- `TlsReloadNotRunAlarm`: no report for 25 hours (the job stopped running or cannot publish).

Operations:

- Run it now: `aws ssm start-associations-once --association-ids <id>` (or run the script by hand
  as root on the instance).
- Logs: the association execution history in SSM, and `journalctl -t netbird-tls-reload`.
- Quick external check: `openssl s_client -connect netbird.autoguru.com.au:33073` (and `:10000`)
  should show the same `notAfter` as `:443`. For up to a day after a renewal they can differ,
  until the next daily run reloads them.

## Settings guard

Some settings that keep logins working were applied by hand and have reset before
(COM-219). [`control-plane-settings-guard.sh`](scripts/control-plane-settings-guard.sh) runs as the
SSM association `netbird-control-plane-settings-guard`, hourly at :15 and once when it is created
or updated.

- **Account settings, corrected.** `user_approval_required` and `peer_approval_enabled` must be
  `false`. They are account settings in the management store, with no `setup.env`,
  `configure.sh` or `management.json` knob in 0.74.7, and every new account starts with user
  approval on. When the job finds either one on, it sets it back through the management API with
  the request the dashboard's Settings > Authentication page sends (GET the account, change only
  these two fields, PUT the settings back), then reads it back.
- **Generated config, reported only.** In the `management.json` the management container mounts:
  `IdpSignKeyRefreshEnabled: true`, the four PKCE `RedirectURLs`, a non-empty
  `DataStoreEncryptionKey`. In `/opt/netbird/setup.env` and the `setup.env` that `configure.sh`
  reads: `NETBIRD_MGMT_IDP_SIGNKEY_REFRESH=true`, the four PKCE ports, and
  `NETBIRD_MANAGEMENT_TAG` equal to the running image. The job does not edit these files.
- The PKCE ports are two-sided: the Entra app registration must list the same four loopback
  redirect URIs. The job cannot see Entra.

It publishes `SettingsDrift` (items found wrong this run) and `SettingsGuardFailures` (a check or
correction did not complete). Alarms to Slack: `SettingsDriftAlarm`, `SettingsGuardFailedAlarm`,
and `SettingsGuardNotRunAlarm` (no report for 3 hours).

One-time setup, after the deploy that creates the association (and after any store rebuild). Use an
admin PAT of your own for the first three calls, then revoke it:

```bash
API=https://netbird.autoguru.com.au:33073/api
# 1. Service user with the admin role (admin is the lowest role that can change account settings).
curl -sS -X POST "$API/users" -H "Authorization: Token $MY_PAT" -H 'Content-Type: application/json' \
  -d '{"name":"settings-guard","role":"admin","auto_groups":[],"is_service_user":true}'
# 2. Its token, 365 days (the maximum). Note the expiry date; the guard fails once it expires.
curl -sS -X POST "$API/users/<service-user-id>/tokens" -H "Authorization: Token $MY_PAT" \
  -H 'Content-Type: application/json' -d '{"name":"settings-guard","expires_in":365}'
# 3. Store the returned plain_token (nbp_...). Only the instance role can read it back.
aws secretsmanager put-secret-value --region ap-southeast-2 \
  --secret-id /netbird/control-plane/settings-guard-pat --secret-string 'nbp_...'
# 4. Run the guard once and read the result.
aws ssm start-associations-once --region ap-southeast-2 --association-ids <settings-guard association id>
```

Offline tests (stub docker, aws and curl; no AWS access): `python3 -m unittest discover -s ci -v`.

## Testing the POC (for others)

1. Install the Netbird desktop client. Client SSO/device-auth is disabled, so enrol with a Setup Key
   (ask the POC owner, or create one in the dashboard):
   ```
   netbird up --management-url https://netbird.autoguru.com.au:33073 --setup-key <SETUP_KEY>
   ```
2. `netbird status --detail` should show Management/Signal **Connected** and the routed domain under
   **Networks**.
3. Browse to a routed Cloudflare-fronted app. To prove traffic egresses through the peer's fixed IP,
   query Cloudflare's trace endpoint on that app - it returns the source IP Cloudflare sees:
   ```
   curl https://<app>.autoguru.com.au/cdn-cgi/trace    # -> ip=54.253.102.22 (the routing-peer EIP)
   ```
   Gate (COM-145): that IP must be the routing-peer EIP (`54.253.102.22`), not the user's local IP.
   Verified 2026-07-10. Pritunl stays live in parallel until the cutover.
