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

# Write environment file for the Netbird setup script.
# Entra App Registration "Netbird" (SPA, COM-141): tenant + client IDs are not secrets. The dashboard
# login is a public PKCE client, so NO client secret is used - an empty NETBIRD_AUTH_CLIENT_SECRET is
# correct for Entra (a secret would only be needed to enable NETBIRD_MGMT_IDP user/group sync, which
# is out of scope for this POC).
mkdir -p /opt/netbird
cat > /opt/netbird/setup.env << EOF
NETBIRD_DOMAIN=netbird.autoguru.com.au
NETBIRD_AUTH_OIDC_CONFIGURATION_ENDPOINT=https://login.microsoftonline.com/4542d3b9-a2ab-47a6-bc7a-1c25894c1adf/v2.0/.well-known/openid-configuration
NETBIRD_USE_AUTH0=false
NETBIRD_AUTH_CLIENT_ID=5853144b-3c6f-4e39-a5b0-df1c3efcdcb1
NETBIRD_AUTH_CLIENT_SECRET=
NETBIRD_AUTH_AUDIENCE=5853144b-3c6f-4e39-a5b0-df1c3efcdcb1
NETBIRD_AUTH_SUPPORTED_SCOPES="openid profile email offline_access"
NETBIRD_AUTH_USER_ID_CLAIM=oid
# Entra issues an access token scoped to MS Graph for OIDC-only scopes, so its aud never matches
# our app; validate the ID token instead (its aud is always the client id). This is Netbird's own
# documented Entra fix (setup.env.example ships it commented). Also disable the Auth0-style audience
# param, which Entra does not support.
NETBIRD_TOKEN_SOURCE=idToken
NETBIRD_AUTH_PKCE_USE_ID_TOKEN=true
NETBIRD_DASH_AUTH_USE_AUDIENCE=false
# The dashboard defaults its OIDC callbacks to fragment URIs (/#callback, /#silent-callback), which
# Entra rejects (RFC 6749 forbids a fragment in redirect_uri). Use Netbird's documented Entra callback
# routes /auth and /silent-auth: dedicated non-page routes. Do NOT use real pages like /peers here -
# react-oidc re-mounts on a real page and hangs on the loading spinner. Register both as SPA redirect
# URIs on the Entra app: https://<domain>/auth and https://<domain>/silent-auth.
NETBIRD_AUTH_REDIRECT_URI="/auth"
NETBIRD_AUTH_SILENT_REDIRECT_URI="/silent-auth"
# Pin the whole control-plane stack to a MATCHED netbird release line (COM-147). Management and the
# dashboard are versioned separately and MUST be paired: netbird 0.74.x pairs with dashboard v2.90.x.
# Mixing release lines breaks login (the older dashboard v2.39.0 does not understand the 0.74.x
# /api/instance schema and hangs; very early v2.90.x builds also had a broken env substitution for
# NETBIRD_MGMT_API_ENDPOINT, fixed by v2.90.4). Keep management/signal/relay and the cloned
# NETBIRD_VERSION below in lockstep on the same line.
NETBIRD_MANAGEMENT_TAG=0.74.7
NETBIRD_SIGNAL_TAG=0.74.7
NETBIRD_RELAY_TAG=0.74.7
NETBIRD_DASHBOARD_TAG=v2.90.5
COTURN_TAG=4.14.0
# Let's Encrypt ACME account email (cert auto-renews; this only receives expiry notices).
# TODO (pre-cutover, COM-147): move to a monitored team distribution list instead of an individual.
NETBIRD_LETSENCRYPT_EMAIL=guillermo@autoguru.com.au
EOF
chmod 0600 /opt/netbird/setup.env

# Netbird setup is run MANUALLY via SSM once the DNS A record (netbird.autoguru.com.au -> this EIP)
# has propagated. We use the EXTERNAL Entra OIDC flow, NOT the bundled ZITADEL script.
# Our Entra-populated env is at /opt/netbird/setup.env. Run via SSM Session Manager:
#   NETBIRD_VERSION="v0.74.7"   # MUST match the *_TAG image pins above (dashboard v2.90.x pairs with 0.74.x)
#   git clone --depth 1 --branch "$NETBIRD_VERSION" https://github.com/netbirdio/netbird/ /opt/netbird/src
#   cp /opt/netbird/setup.env /opt/netbird/src/infrastructure_files/setup.env
#   cd /opt/netbird/src/infrastructure_files && ./configure.sh   # -> artifacts/{docker-compose.yml,management.json,turnserver.conf}
#   cd artifacts && docker compose up -d
# NOTE (COM-147, datastore encryption key): configure.sh writes a DataStoreEncryptionKey into
# management.json that encrypts user PII (email/name) at rest. It MUST be a STABLE value across
# re-provisioning. A random key that only lives in the running container is lost on the next
# container recreate and then breaks peer sync ("decrypt user: decrypt email: message authentication
# failed"). configure.sh preserves the key of an existing management.json; for production, source a
# fixed key from Secrets Manager at boot and write it into management.json (recording it in setup.env
# as NETBIRD_DATASTORE_ENCRYPTION_KEY) so every rebuild reuses the same key.
# Peers and the routing peer enroll with a pre-shared setup key (created in the dashboard, stored in
# Secrets Manager); the dashboard admin login uses Entra SSO via the SPA app registration.
echo "Control plane ready. Run Netbird setup via SSM after DNS propagation (Entra flow: configure.sh, not zitadel)." >> /var/log/netbird-setup.log
