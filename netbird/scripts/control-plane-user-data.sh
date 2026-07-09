#!/bin/bash
set -euo pipefail

# Install Docker (and git, needed at setup time to fetch the Netbird infrastructure files)
dnf install -y docker git
systemctl enable --now docker

# Install Docker Compose plugin (pinned + checksum-verified; COM-147)
COMPOSE_VERSION="v5.3.1"
COMPOSE_SHA256="f9ebc6ebdb19d769b793c245a736caaeb198c62587f13b25c660c13b4987f959"
mkdir -p /usr/local/lib/docker/cli-plugins
curl -fsSL "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-x86_64" \
  -o /usr/local/lib/docker/cli-plugins/docker-compose
echo "${COMPOSE_SHA256}  /usr/local/lib/docker/cli-plugins/docker-compose" | sha256sum -c
chmod +x /usr/local/lib/docker/cli-plugins/docker-compose

# Fetch Entra client secret from Secrets Manager
ENTRA_SECRET=$(aws secretsmanager get-secret-value \
  --secret-id /netbird/control-plane/entra-client-secret \
  --region ap-southeast-2 \
  --query SecretString \
  --output text)

# Write environment file for the Netbird setup script
# Entra App Registration "Netbird" values filled in below (tenant + client IDs are not secrets;
# the client secret is pulled from Secrets Manager above). App registered 2026-06-15 under COM-141.
mkdir -p /opt/netbird
cat > /opt/netbird/setup.env << EOF
NETBIRD_DOMAIN=netbird.autoguru.com.au
NETBIRD_AUTH_OIDC_CONFIGURATION_ENDPOINT=https://login.microsoftonline.com/4542d3b9-a2ab-47a6-bc7a-1c25894c1adf/v2.0/.well-known/openid-configuration
NETBIRD_AUTH_CLIENT_ID=5853144b-3c6f-4e39-a5b0-df1c3efcdcb1
NETBIRD_AUTH_CLIENT_SECRET=${ENTRA_SECRET}
NETBIRD_AUTH_AUDIENCE=5853144b-3c6f-4e39-a5b0-df1c3efcdcb1
NETBIRD_AUTH_SUPPORTED_CLAIMS=email
NETBIRD_AUTH_SUPPORTED_SCOPES="openid profile email"
NETBIRD_AUTH_USER_ID_CLAIM=sub
# Let's Encrypt ACME account email (cert auto-renews; this only receives expiry notices).
# TODO (pre-cutover, COM-147): move to a monitored team distribution list instead of an individual.
NETBIRD_LETSENCRYPT_EMAIL=guillermo@autoguru.com.au
EOF
chmod 0600 /opt/netbird/setup.env

# Netbird setup is run MANUALLY via SSM once the DNS A record (netbird.autoguru.com.au -> this EIP)
# has propagated. We use the EXTERNAL Entra OIDC flow, NOT the bundled ZITADEL script.
# Our Entra-populated env is at /opt/netbird/setup.env. Run via SSM Session Manager:
#   NETBIRD_VERSION="v0.74.2"   # keep in lockstep with the routing-peer image (COM-147)
#   git clone --depth 1 --branch "$NETBIRD_VERSION" https://github.com/netbirdio/netbird/ /opt/netbird/src
#   cp /opt/netbird/setup.env /opt/netbird/src/infrastructure_files/setup.env
#   cd /opt/netbird/src/infrastructure_files && ./configure.sh   # -> artifacts/{docker-compose.yml,management.json,turnserver.conf}
#   cd artifacts && docker compose up -d
# Peers and the routing peer enroll with a pre-shared setup key (created in the dashboard, stored in
# Secrets Manager); the dashboard admin login uses Entra SSO via the SPA app registration.
echo "Control plane ready. Run Netbird setup via SSM after DNS propagation (Entra flow: configure.sh, not zitadel)." >> /var/log/netbird-setup.log
