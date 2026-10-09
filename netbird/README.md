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

Local (requires the .NET 10 SDK and the CDK CLI, with shared-account credentials):

```bash
cd netbird/cdk
dotnet build
npx cdk diff
npx cdk deploy NetbirdControlPlaneStack NetbirdRoutingPeerStack --require-approval never
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
