#!/bin/bash
# Netbird control plane: detect (and where safe, correct) drift in the settings that were applied by
# hand and have broken logins before (COM-219). Runs hourly on the control-plane instance as an SSM
# State Manager association (NetbirdControlPlaneStack.cs, SettingsGuardAssociation).
#
# A. Generated config (detect only, never edited here). configure.sh regenerates management.json
#    from setup.env on every run, so both must carry the fixes:
#    - management.json, the file the running management container mounts:
#        HttpConfig.IdpSignKeyRefreshEnabled = true   (2026-08-26 JWKS outage)
#        PKCEAuthorizationFlow.ProviderConfig.RedirectURLs = the four loopback ports (COM-188). The
#          Entra app registration must list the same four URIs; this job cannot see Entra.
#        DataStoreEncryptionKey not empty   (losing it makes user emails undecryptable)
#    - /opt/netbird/setup.env and the setup.env next to the generated artifacts (the copy
#      configure.sh actually reads): NETBIRD_MGMT_IDP_SIGNKEY_REFRESH=true,
#      NETBIRD_AUTH_PKCE_REDIRECT_URL_PORTS with the four ports, and NETBIRD_MANAGEMENT_TAG equal to
#      the running management image (otherwise a configure.sh run changes the version).
# B. Account settings in the management store. Netbird 0.74.7 has no setup.env, configure.sh or
#    management.json knob for these: every new account is created with user_approval_required=true
#    (management/server/account.go), so a fresh store or re-provision turns it back on and new users
#    get "user pending approval cannot add peers". peer_approval_enabled is a Cloud-only feature (the
#    open-source peer validator ignores it), but it is held at false too so an upgrade that starts
#    honouring it cannot lock peers out silently.
#    In fix mode (default) a drifted value is set back through the management REST API with the same
#    request the dashboard's Settings > Authentication page sends: GET the account, change only these
#    two fields in the returned settings, PUT the settings back.
#
# Publishes to CloudWatch (namespace Netbird/ControlPlane):
#   SettingsDrift          number of drifted items found this run (corrected or not)
#   SettingsGuardFailures  1 if a check could not be completed or a correction did not stick
# The stack alarms on either, and on no datapoint at all (the job stopped running).
# Any check that cannot be completed is a failure, never a silent pass.
set -uo pipefail

DOMAIN="${NB_GUARD_DOMAIN:-netbird.autoguru.com.au}"
MGMT_PORT="${NB_GUARD_MGMT_PORT:-33073}"
COMPOSE_PROJECT="${NB_GUARD_COMPOSE_PROJECT:-artifacts}"
PAT_SECRET="${NB_GUARD_PAT_SECRET:-/netbird/control-plane/settings-guard-pat}"
MODE="${NB_GUARD_MODE:-fix}" # fix | check
PUBLISH_METRIC="${NB_GUARD_PUBLISH_METRIC:-true}"
PKCE_PORTS="53000,8976,35000,43000"
OPERATOR_SETUP_ENV="${NB_GUARD_OPERATOR_SETUP_ENV:-/opt/netbird/setup.env}"
REGION=ap-southeast-2
NAMESPACE=Netbird/ControlPlane

drift=0
corrected=0
failed=0
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
chmod 0700 "$work"

log() { echo "$*"; logger -t netbird-settings-guard -- "$*" 2>/dev/null || true; }
fail() { log "ERROR: $*"; failed=1; }
drifted() { log "DRIFT: $*"; drift=$((drift + 1)); }

# ---- A. generated config ---------------------------------------------------------------------

cid=$(docker ps -q --filter "label=com.docker.compose.project=$COMPOSE_PROJECT" \
  --filter "label=com.docker.compose.service=management")
mgmt_json=""
running_tag=""
if [ "$(wc -w <<<"$cid")" -eq 1 ]; then
  mgmt_json=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/etc/netbird/management.json"}}{{.Source}}{{end}}{{end}}' "$cid")
  image=$(docker inspect -f '{{.Config.Image}}' "$cid")
  image="${image%%@*}"
  [[ "$image" == *:* ]] && running_tag="${image##*:}"
fi

if [ -z "$mgmt_json" ] || [ ! -r "$mgmt_json" ]; then
  fail "cannot find the management.json the management container mounts (container '${cid//$'\n'/ }')"
else
  if ! findings=$(NB_PORTS="$PKCE_PORTS" python3 - "$mgmt_json" <<'PY'
import json, os, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
want = sorted(f"http://localhost:{p.strip()}" for p in os.environ["NB_PORTS"].split(","))
if (cfg.get("HttpConfig") or {}).get("IdpSignKeyRefreshEnabled") is not True:
    print("management.json: HttpConfig.IdpSignKeyRefreshEnabled is not true")
urls = (((cfg.get("PKCEAuthorizationFlow") or {}).get("ProviderConfig") or {}).get("RedirectURLs")) or []
if sorted(urls) != want:
    print(f"management.json: PKCE RedirectURLs are {urls}, expected {want}")
if not cfg.get("DataStoreEncryptionKey"):
    print("management.json: DataStoreEncryptionKey is empty")
PY
  ); then
    fail "cannot parse $mgmt_json"
  fi
  while IFS= read -r f; do [ -n "$f" ] && drifted "$f"; done <<<"$findings"
fi

# Last assignment of a variable in an env file, without quotes and spaces (the file is not sourced).
env_value() {
  grep -E "^[[:space:]]*(export[[:space:]]+)?$1=" "$2" | tail -n 1 | cut -d= -f2- | tr -d "\"' \r"
}

setup_envs="$OPERATOR_SETUP_ENV"
[ -n "$mgmt_json" ] && setup_envs="$setup_envs $(dirname "$(dirname "$mgmt_json")")/setup.env"
for env_file in $setup_envs; do
  if [ ! -r "$env_file" ]; then
    fail "$env_file is missing or unreadable"
    continue
  fi
  [ "$(env_value NETBIRD_MGMT_IDP_SIGNKEY_REFRESH "$env_file")" = true ] \
    || drifted "$env_file: NETBIRD_MGMT_IDP_SIGNKEY_REFRESH is not true (a configure.sh run would turn the JWKS refresh off)"
  [ "$(env_value NETBIRD_AUTH_PKCE_REDIRECT_URL_PORTS "$env_file")" = "$PKCE_PORTS" ] \
    || drifted "$env_file: NETBIRD_AUTH_PKCE_REDIRECT_URL_PORTS is not $PKCE_PORTS"
  tag=$(env_value NETBIRD_MANAGEMENT_TAG "$env_file")
  if [ -z "$running_tag" ]; then
    fail "cannot read the running management image tag"
  elif [ "$tag" != "$running_tag" ]; then
    drifted "$env_file: NETBIRD_MANAGEMENT_TAG is '$tag' but management runs $running_tag (a configure.sh run would change the version)"
  fi
done

# ---- B. account settings through the API -----------------------------------------------------

if ! pat=$(aws secretsmanager get-secret-value --region "$REGION" --secret-id "$PAT_SECRET" \
    --query SecretString --output text 2>"$work/aws.err"); then
  fail "cannot read $PAT_SECRET: $(head -c 300 "$work/aws.err")"
  pat=""
fi
case "$pat" in
  nbp_*) ;;
  "") ;;
  *) fail "$PAT_SECRET does not hold a Netbird personal access token (nbp_...); see netbird/README.md"; pat="" ;;
esac

if [ -n "$pat" ]; then
  # The token goes in a 0600 header file, never on a command line or in the log.
  printf 'Authorization: Token %s\n' "$pat" >"$work/auth.hdr"
  unset pat
  api() {
    curl -sS --fail-with-body --max-time 20 --resolve "$DOMAIN:$MGMT_PORT:127.0.0.1" \
      -H @"$work/auth.hdr" -H 'Accept: application/json' "$@"
  }

  # inspect <accounts.json> <body-out>: print "account=<id>" and one "drift: ..." line per wrong
  # field; write the PUT body (settings with the two fields corrected) when something is wrong.
  inspect() {
    python3 - "$1" "$2" <<'PY'
import json, sys
accounts = json.load(open(sys.argv[1], encoding="utf-8"))
if not isinstance(accounts, list) or len(accounts) != 1:
    sys.exit(f"expected exactly one account, got {len(accounts) if isinstance(accounts, list) else type(accounts).__name__}")
account = accounts[0]
settings = account["settings"]
extra = dict(settings.get("extra") or {})
want = {"user_approval_required": False, "peer_approval_enabled": False}
print(f"account={account['id']}")
wrong = {k: v for k, v in want.items() if extra.get(k) is not v}
for k in wrong:
    print(f"drift: settings.extra.{k} is {extra.get(k)!r}, expected {want[k]!r}")
if wrong:
    # Same shape as the dashboard's Authentication tab save: every returned setting, these two changed.
    extra.update(want)
    body = {"settings": dict(settings, extra=extra)}
    json.dump(body, open(sys.argv[2], "w", encoding="utf-8"))
PY
  }

  if ! api "https://$DOMAIN:$MGMT_PORT/api/accounts" >"$work/accounts.json" 2>"$work/curl.err"; then
    fail "GET /api/accounts failed: $(head -c 300 "$work/curl.err") $(head -c 300 "$work/accounts.json")"
  elif ! report=$(inspect "$work/accounts.json" "$work/put.json" 2>"$work/py.err"); then
    fail "cannot read the account settings: $(head -c 300 "$work/py.err")"
  else
    account_id=$(sed -n 's/^account=//p' <<<"$report")
    api_lines=$(grep '^drift: ' <<<"$report")
    api_drift=0
    if [ -n "$api_lines" ]; then
      while IFS= read -r line; do drifted "${line#drift: }"; api_drift=$((api_drift + 1)); done <<<"$api_lines"
    fi
    if [ "$api_drift" -eq 0 ]; then
      log "account $account_id: user and peer approval are off"
    elif [ "$MODE" != fix ]; then
      log "check mode: not correcting account $account_id"
    elif ! api -X PUT -H 'Content-Type: application/json' --data @"$work/put.json" \
        "https://$DOMAIN:$MGMT_PORT/api/accounts/$account_id" >"$work/put.out" 2>"$work/curl.err"; then
      fail "PUT /api/accounts/$account_id failed: $(head -c 300 "$work/curl.err") $(head -c 300 "$work/put.out")"
    elif ! api "https://$DOMAIN:$MGMT_PORT/api/accounts" >"$work/accounts.json" 2>"$work/curl.err" \
        || ! report=$(inspect "$work/accounts.json" "$work/put2.json" 2>"$work/py.err"); then
      fail "corrected account $account_id but could not read it back"
    elif grep -q '^drift: ' <<<"$report"; then
      fail "account $account_id is still drifted after the correction: $(grep '^drift: ' <<<"$report" | tr '\n' ' ')"
    else
      corrected=$api_drift
      log "account $account_id: corrected user/peer approval back to off (find out what reset it)"
    fi
  fi
fi

# ---- publish ---------------------------------------------------------------------------------

log "result: SettingsDrift=$drift SettingsGuardFailures=$failed (mode $MODE)"
if [ "$PUBLISH_METRIC" = true ]; then
  # If this call fails nothing is published, and the stack's no-datapoint alarm fires instead.
  if ! aws cloudwatch put-metric-data --region "$REGION" --namespace "$NAMESPACE" --metric-data \
      "MetricName=SettingsDrift,Value=$drift,Unit=Count" \
      "MetricName=SettingsGuardFailures,Value=$failed,Unit=Count"; then
    fail "could not publish metrics to $NAMESPACE"
  fi
fi

# Non-zero (association run shown as failed) while anything is still wrong after this run.
[ "$failed" -eq 0 ] && [ "$drift" -eq "$corrected" ] || exit 1
exit 0
