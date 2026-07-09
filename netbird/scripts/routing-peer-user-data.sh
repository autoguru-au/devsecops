#!/bin/bash
set -euo pipefail

# Install Docker
dnf install -y docker
systemctl enable --now docker

# Install Docker Compose plugin (pinned + checksum-verified; COM-147)
COMPOSE_VERSION="v5.3.1"
COMPOSE_SHA256="f9ebc6ebdb19d769b793c245a736caaeb198c62587f13b25c660c13b4987f959"
mkdir -p /usr/local/lib/docker/cli-plugins
curl -fsSL "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-x86_64" \
  -o /usr/local/lib/docker/cli-plugins/docker-compose
echo "${COMPOSE_SHA256}  /usr/local/lib/docker/cli-plugins/docker-compose" | sha256sum -c
chmod +x /usr/local/lib/docker/cli-plugins/docker-compose

# Fetch Netbird setup key from Secrets Manager and write to .env (not stored in source control)
SETUP_KEY=$(aws secretsmanager get-secret-value \
  --secret-id /netbird/routing-peer/setup-key \
  --region ap-southeast-2 \
  --query SecretString \
  --output text)

mkdir -p /opt/netbird
cat > /opt/netbird/.env << EOF
NB_SETUP_KEY=${SETUP_KEY}
NB_MANAGEMENT_URL=https://netbird.autoguru.com.au
EOF
chmod 0600 /opt/netbird/.env

# Routing peer runs the Netbird agent in host networking with NET_ADMIN to manage routes.
# Image pinned by tag + digest (COM-147). MUST match the control-plane management line (0.72.x) so the
# agent and management stay compatible; keep this in lockstep with NETBIRD_MANAGEMENT_TAG.
cat > /opt/netbird/docker-compose.yml << 'COMPOSE'
services:
  netbird:
    image: netbirdio/netbird:0.72.4@sha256:6c6c20baffae4a3ec50f29ec9361608a420625185505e8cd6f0c44d71c5d4798
    container_name: netbird-routing-peer
    restart: unless-stopped
    network_mode: host
    cap_add:
      - NET_ADMIN
    volumes:
      - /var/lib/netbird:/etc/netbird
    env_file:
      - /opt/netbird/.env
COMPOSE

# Enable IP forwarding (required for routing peer masquerade NAT)
echo 'net.ipv4.ip_forward=1' >> /etc/sysctl.d/99-netbird.conf
sysctl -p /etc/sysctl.d/99-netbird.conf

# The peer is useless until the control plane is set up and netbird.autoguru.com.au resolves;
# until then the agent retries (restart: unless-stopped).
cd /opt/netbird && docker compose up -d
