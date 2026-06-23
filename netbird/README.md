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
Two independent stacks (no "God" stack), each: dedicated VPC (public subnet + EIP, no NAT),
Amazon Linux 2023, Docker, IMDSv2 required, encrypted EBS, CloudWatch auto-recovery, an SSM-only
IAM role (no inbound SSH), and secrets read at boot from Secrets Manager. The EC2 user-data lives
in `netbird/scripts/*.sh` and is embedded into the assembly at build time.

| Stack | Instance | Purpose |
| --- | --- | --- |
| `NetbirdControlPlaneStack` | t3.small / 30 GB gp3 | Management, signal, relay, dashboard, Coturn |
| `NetbirdRoutingPeerStack` | t3.micro / 30 GB gp3 | Routing agent + WireGuard data plane + Cloudflare egress EIP |

## Prerequisites

1. **Entra ID app registration** `Netbird` (single tenant, SPA) already exists (COM-141):
   client `5853144b-3c6f-4e39-a5b0-df1c3efcdcb1`, tenant `4542d3b9-a2ab-47a6-bc7a-1c25894c1adf`.
   Its client secret must be in Secrets Manager (`ap-southeast-2`) at
   `/netbird/control-plane/entra-client-secret`.
2. The routing-peer setup key secret `/netbird/routing-peer/setup-key` is created (empty) by
   the routing-peer stack and populated after the control plane is set up.

## Deploy

Deploys run via the [`netbird-deploy`](../.github/workflows/netbird-deploy.yml) workflow:
pull requests touching `netbird/**` get a `cdk diff`; deploys are a manual `workflow_dispatch`
(`action: deploy`). The workflow assumes `AWS_DEPLOY_ROLE_ARN` in the shared account via OIDC.

Prerequisite: the shared account is already CDK-bootstrapped (the existing `SharedPlatformStack`
is deployed there via CDK), so no `cdk bootstrap` is needed.

Local (requires the .NET 9 SDK and the CDK CLI, with shared-account credentials):

```bash
cd netbird/cdk
dotnet build
npx cdk diff
npx cdk deploy NetbirdControlPlaneStack NetbirdRoutingPeerStack --require-approval never
```

## Post-deploy setup (manual, once)

1. From the `NetbirdControlPlaneStack` outputs, take the control-plane EIP and ask an admin to
   create the Cloudflare DNS A record `netbird.autoguru.com.au` (DNS-only, not proxied) pointing
   at it, then wait for propagation (Let's Encrypt needs the FQDN resolvable).
2. SSM into the control plane and run the Netbird setup (clone, `configure.sh`, `docker compose up`)
   following the commented steps in [`scripts/control-plane-user-data.sh`](scripts/control-plane-user-data.sh).
   This uses the external Entra OIDC flow, not the bundled ZITADEL script.
3. In the dashboard, create a setup key and store it in `/netbird/routing-peer/setup-key`, then
   rebuild / reboot the routing peer so it enrols.
4. Define the Domain Resource (`*.autoguru.com.au`) and ask an admin to add the routing-peer EIP
   (from `NetbirdRoutingPeerStack` outputs) to the Cloudflare origin allowlist.
5. Gate (COM-145): confirm one Cloudflare-protected FQDN egresses via the routing-peer EIP in the
   Cloudflare access logs. Pritunl stays live until the gate passes.
