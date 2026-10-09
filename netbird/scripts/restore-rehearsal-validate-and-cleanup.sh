#!/bin/bash
# COM-234 restore rehearsal, step 2 of 2: validate the THROWAWAY copy launched by
# restore-rehearsal-launch.sh, record the timings, then remove the copy and prove nothing is left.
# The shared Developer session (AWS_PROFILE=shared) is enough: describe, SSM Run Command, terminate,
# delete the security group and the alarm.
#
# Safety:
#   - It refuses the production control plane (i-06766fc0cc0e7815e) before any AWS call, and acts
#     only on an instance tagged Purpose=COM-234-restore-rehearsal.
#   - It checks the posture of the copy through the API first (no public IP, no EIP, only the
#     rehearsal security group with no inbound rules, the SSM-only profile, IMDSv2, no user data,
#     encrypted EBS). If any of these fails it skips the checks on the copy and goes to cleanup.
#   - The checks on the copy are read-only (SSM Run Command, run as root on the copy): docker ps,
#     local HTTPS requests to 127.0.0.1 only (never the public name, which resolves to production),
#     sqlite opened read-only, file metadata. They print names, counts, dates, hash prefixes and
#     booleans, never a secret or a personal value. The copy refuses to run them if its own instance
#     id is the production one.
#   - Cleanup runs even when a check fails.
#
# Usage:
#   AWS_PROFILE=shared bash netbird/scripts/restore-rehearsal-validate-and-cleanup.sh
#   AWS_PROFILE=shared bash netbird/scripts/restore-rehearsal-validate-and-cleanup.sh --cleanup-only
#   bash netbird/scripts/restore-rehearsal-validate-and-cleanup.sh --print-remote-script
# Options:
#   --instance-id <id>       validate this copy instead of finding it by tag (it must carry the tag).
#   --cleanup-only           skip validation; remove every rehearsal resource and print the proof.
#   --print-remote-script    print the read-only check script that runs on the copy, and exit.
# Exit status: 0 = all checks passed and cleanup is complete; 1 = a check failed (cleanup complete);
# 2 = cleanup incomplete (see the proof); 3 = refused (production target or wrong account).
#
# Reference values (production, 2026-10-09, COM-219 and COM-234): 69 peers, 1 setup-key peer,
# 6 network routers bound to peer d9gbdrj6leos73em57u0. Override with EXPECT_PEERS,
# EXPECT_SETUP_KEY_PEERS, EXPECT_ROUTERS. A difference is a WARN: the recovery point is older than
# the reference, and peers join every day.
set -Eeuo pipefail

REGION=ap-southeast-2
ACCOUNT=791686214595
VPC_ID="vpc-064a7525a3bcc4667"
SUBNET_ID="subnet-0e02fd563212fd98c"
PROD_INSTANCE="i-06766fc0cc0e7815e"
INSTANCE_PROFILE=AmazonSSMRoleForInstancesQuickSetup
PURPOSE=COM-234-restore-rehearsal
SG_NAME=netbird-restore-rehearsal-COM-234
ALARM_NAME=netbird-restore-rehearsal-COM-234-cpu
SSM_WAIT_SECONDS=${SSM_WAIT_SECONDS:-900}
COMMAND_WAIT_SECONDS=${COMMAND_WAIT_SECONDS:-1500}
EXPECT_PEERS=${EXPECT_PEERS:-69}
EXPECT_SETUP_KEY_PEERS=${EXPECT_SETUP_KEY_PEERS:-1}
EXPECT_ROUTERS=${EXPECT_ROUTERS:-6}
ROUTING_PEER_ID=${ROUTING_PEER_ID:-d9gbdrj6leos73em57u0}

export AWS_REGION=$REGION AWS_DEFAULT_REGION=$REGION TZ=UTC AWS_PAGER="" MSYS_NO_PATHCONV=1

# ---------------------------------------------------------------------------------------------
# The read-only check script that runs on the copy. Quoted heredoc: nothing expands locally.
# ---------------------------------------------------------------------------------------------
remote_script() {
  cat <<'REMOTE'
#!/bin/bash
# COM-234 read-only checks on the THROWAWAY restored copy (root, SSM Run Command).
# Output: "CHECK <name> PASS|WARN|FAIL <detail>" verdicts and "T <event> <UTC>" timings.
# Never prints a secret or a personal value.
set -uo pipefail
DOMAIN=netbird.autoguru.com.au
PROD_INSTANCE=i-06766fc0cc0e7815e
API_WAIT_SECONDS=${API_WAIT_SECONDS:-600}
check() { echo "CHECK $1 $2 ${3:-}"; }
iso() { date -u -d "$1" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || echo unknown; }

TOKEN=$(curl -sS -m 3 -X PUT -H 'X-aws-ec2-metadata-token-ttl-seconds: 600' http://169.254.169.254/latest/api/token)
IID=$(curl -sS -m 3 -H "X-aws-ec2-metadata-token: $TOKEN" http://169.254.169.254/latest/meta-data/instance-id)
if [ -z "$IID" ] || [ "$IID" = "$PROD_INSTANCE" ]; then
  echo "REFUSING: this instance ($IID) is the production control plane or unidentified"
  exit 99
fi
UPTIME=$(cut -d. -f1 /proc/uptime)
BOOT_EPOCH=$(( $(date +%s) - UPTIME ))
BOOT=$(date -u -d "@$BOOT_EPOCH" +%Y-%m-%dT%H:%M:%SZ)
echo "instance_id=$IID checks_started=$(date -u +%Y-%m-%dT%H:%M:%SZ) uptime_s=$UPTIME"
echo "T boot $BOOT"

# 1. No user data and nothing on the copy that would act on production.
ud=$(curl -sS -m 3 -o /dev/null -w '%{http_code}' -H "X-aws-ec2-metadata-token: $TOKEN" \
  http://169.254.169.254/latest/user-data 2>/dev/null)
if [ "$ud" = 404 ]; then check no_user_data PASS "IMDS user-data returns 404"
else check no_user_data FAIL "IMDS user-data returned $ud"; fi
client=$(systemctl is-active netbird 2>/dev/null || true)
if [ "$client" = active ]; then check no_netbird_client FAIL "a netbird client service is active on the copy"
else check no_netbird_client PASS "netbird client service: ${client:-absent}"; fi
echo "systemd_timers=$(systemctl list-timers --all --no-legend 2>/dev/null \
  | awk '{for (i = 1; i <= NF; i++) if ($i ~ /\.timer$/) print $i}' | sort | tr '\n' ' ')"
echo "root_crontab_entries=$(crontab -l 2>/dev/null | grep -cvE '^[[:space:]]*(#|$)')"

# 2. Egress: TCP 443 is open (the accepted residual), every other port is closed.
c443=$(curl -sS -m 8 -o /dev/null -w '%{http_code}' https://ssm.ap-southeast-2.amazonaws.com/ 2>/dev/null)
c80=$(curl -sS -m 8 -o /dev/null -w '%{http_code}' http://example.com/ 2>/dev/null)
echo "egress_tcp443_http_code=$c443 egress_tcp80_http_code=$c80"
if [ "$c80" = 000 ]; then check egress_only_443 PASS "TCP 80 blocked, TCP 443 answered $c443"
else check egress_only_443 FAIL "TCP 80 reached example.com ($c80)"; fi

# 3. Containers, from the compose labels (project "artifacts").
docker ps -a --format '{{.Names}} {{.Image}} {{.Status}}' | sed 's/^/container /'
cid() { docker ps -aq --filter "label=com.docker.compose.service=$1" | head -1; }
down=""
for svc in management signal relay dashboard coturn; do
  id=$(cid "$svc")
  state=$( [ -n "$id" ] && docker inspect -f '{{.State.Status}}' "$id" )
  [ "$state" = running ] || down="$down $svc:${state:-missing}"
done
if [ -z "$down" ]; then check containers_up PASS "management signal relay dashboard coturn running"
else check containers_up FAIL "not running:$down"; fi
M=$(cid management)
if [ -z "$M" ]; then
  check management_container FAIL "no management container"
  exit 1
fi
read -r M_STARTED M_RESTARTS <<<"$(docker inspect -f '{{.State.StartedAt}} {{.RestartCount}}' "$M")"
echo "T mgmt_started $(iso "$M_STARTED")"
if [ "$M_RESTARTS" = 0 ]; then check management_restarts PASS "RestartCount=0"
else check management_restarts WARN "RestartCount=$M_RESTARTS"; fi

# 4. Management API answers locally. Unauthenticated, so 401 proves the service is up.
# --resolve pins the name to 127.0.0.1: the public name resolves to the production EIP.
deadline=$(( $(date +%s) + API_WAIT_SECONDS ))
code=000
while :; do
  code=$(curl -sS -m 5 -o /dev/null -w '%{http_code}' --resolve "$DOMAIN:33073:127.0.0.1" \
    "https://$DOMAIN:33073/api/users" 2>/dev/null)
  [ "$code" = 401 ] && break
  [ "$(date +%s)" -ge "$deadline" ] && break
  sleep 5
done
if [ "$code" = 401 ]; then
  echo "T api_401 $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  check management_api PASS "unauthenticated GET /api/users on :33073 returned 401 with a valid certificate"
else
  check management_api FAIL "GET /api/users on :33073 returned $code"
fi
dash=$(curl -sS -m 8 -o /dev/null -w '%{http_code}' --resolve "$DOMAIN:443:127.0.0.1" "https://$DOMAIN/" 2>/dev/null)
if [ "$dash" = 200 ]; then check dashboard PASS "GET / on :443 returned 200"
else check dashboard WARN "GET / on :443 returned $dash"; fi

# 5. Management logs since this boot only (the restored log file also holds production history).
logs=$(docker logs --since "$BOOT" "$M" 2>&1)
decrypt_errors=$(printf '%s\n' "$logs" | grep -iE 'decrypt|message authentication failed' | grep -ciE 'err|fail|unable|authentication')
fatal=$(printf '%s\n' "$logs" | grep -cE '(^|[[:space:]])(FATA|PANI|fatal|panic)([[:space:]]|:|$)')
oidc=$(printf '%s\n' "$logs" | grep -c 'loaded OIDC configuration')
echo "mgmt_log_lines_since_boot=$(printf '%s\n' "$logs" | wc -l) oidc_config_loaded=$oidc fatal_lines=$fatal"
if [ "$decrypt_errors" = 0 ]; then check management_no_decrypt_errors PASS "0 decrypt errors since boot"
else check management_no_decrypt_errors FAIL "$decrypt_errors decrypt error line(s) since boot"; fi

# 6. management.json, store.db and the datastore key.
mount_src() { docker inspect -f "{{range .Mounts}}{{if eq .Destination \"$2\"}}{{.Source}}{{end}}{{end}}" "$1"; }
MJ=$(mount_src "$M" /etc/netbird/management.json)
DATADIR=$(mount_src "$M" /var/lib/netbird)
LE=$(mount_src "$M" /etc/letsencrypt)
echo "paths management_json=$MJ datadir=$DATADIR letsencrypt=$LE"
python3 - "$MJ" "$DATADIR" /opt/netbird/setup.env /opt/netbird/src/infrastructure_files/setup.env <<'PY'
import base64, ctypes, ctypes.util, hashlib, json, os, sqlite3, sys

def check(name, status, detail=""):
    print("CHECK %s %s %s" % (name, status, detail))

def env_int(name, default):
    try:
        return int(os.environ.get(name, default))
    except ValueError:
        return default

mj_path, datadir, setup_envs = sys.argv[1], sys.argv[2], sys.argv[3:]
try:
    with open(mj_path) as f:
        cfg = json.load(f)
except Exception as e:
    check("management_json", "FAIL", "unreadable (%s)" % type(e).__name__)
    sys.exit(0)

print("management_json_keys=" + ",".join(sorted(cfg)))
missing = [k for k in ("DataStoreEncryptionKey", "Datadir", "HttpConfig", "Signal", "StoreConfig") if k not in cfg]
check("management_json", "FAIL" if missing else "PASS",
      ("missing " + ",".join(missing)) if missing else "present with the required keys")
http = cfg.get("HttpConfig") or {}
refresh = http.get("IdpSignKeyRefreshEnabled")
check("idp_signkey_refresh", "PASS" if refresh is True else "FAIL", "IdpSignKeyRefreshEnabled=%s" % refresh)
oidc = http.get("OIDCConfigEndpoint") or ""
print("oidc_config_endpoint_host=%s" % (oidc.split("/")[2] if "//" in oidc else "unset"))
urls = ((cfg.get("PKCEAuthorizationFlow") or {}).get("ProviderConfig") or {}).get("RedirectURLs") or []
ports = {u.split("//", 1)[-1].split("/", 1)[0].rsplit(":", 1)[-1] for u in urls if "localhost" in u}
want = {"53000", "8976", "35000", "43000"}
check("pkce_redirect_ports", "PASS" if ports == want else "WARN", ",".join(sorted(ports)) or "none")
print("store_engine=%s" % (cfg.get("StoreConfig") or {}).get("Engine"))

key = cfg.get("DataStoreEncryptionKey") or ""
try:
    raw = base64.b64decode(key, validate=True) if key else b""
except Exception:
    raw = b""
print("datastore_key_sha256_prefix=%s" % (hashlib.sha256(key.encode()).hexdigest()[:12] if key else "none"))
check("datastore_key_present", "PASS" if len(raw) == 32 else "FAIL", "set=%s decoded_bytes=%d" % (bool(key), len(raw)))
for env in setup_envs:
    try:
        with open(env) as f:
            vals = [l.split("=", 1)[1].strip().strip("\"'") for l in f if l.startswith("NETBIRD_DATASTORE_ENCRYPTION_KEY=")]
    except OSError:
        print("setup_env path=%s present=False" % env)
        continue
    print("setup_env path=%s present=True key_recorded=%s key_matches_management_json=%s"
          % (env, bool(vals), (vals[-1] == key) if vals else "n/a"))

def gcm_decryptor(k):
    """AES-256-GCM open, the format of netbird util/crypt FieldEncrypt: base64(nonce12 || ct || tag16)."""
    try:
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
        a = AESGCM(k)
        return "cryptography", lambda n, c: a.decrypt(n, c, None)
    except Exception:
        pass
    lib = ctypes.CDLL(os.environ.get("LIBCRYPTO") or ctypes.util.find_library("crypto") or "libcrypto.so.3")
    vp, ip = ctypes.c_void_p, ctypes.c_int
    lib.EVP_CIPHER_CTX_new.restype = vp
    lib.EVP_aes_256_gcm.restype = vp
    lib.EVP_CIPHER_CTX_free.argtypes = [vp]
    lib.EVP_DecryptInit_ex.argtypes = [vp, vp, vp, ctypes.c_char_p, ctypes.c_char_p]
    lib.EVP_CIPHER_CTX_ctrl.argtypes = [vp, ip, ip, vp]
    lib.EVP_DecryptUpdate.argtypes = [vp, ctypes.c_char_p, ctypes.POINTER(ip), ctypes.c_char_p, ip]
    lib.EVP_DecryptFinal_ex.argtypes = [vp, ctypes.c_char_p, ctypes.POINTER(ip)]
    EVP_CTRL_GCM_SET_IVLEN, EVP_CTRL_GCM_SET_TAG = 0x9, 0x11

    def dec(nonce, data):
        body, tag = data[:-16], data[-16:]
        ctx = lib.EVP_CIPHER_CTX_new()
        if not ctx:
            raise RuntimeError("EVP_CIPHER_CTX_new")
        try:
            n, fin = ip(0), ip(0)
            out = ctypes.create_string_buffer(len(body) + 32)
            tagbuf = ctypes.create_string_buffer(tag, 16)
            ok = (lib.EVP_DecryptInit_ex(ctx, lib.EVP_aes_256_gcm(), None, None, None) == 1
                  and lib.EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN, len(nonce), None) == 1
                  and lib.EVP_DecryptInit_ex(ctx, None, None, k, nonce) == 1
                  and lib.EVP_DecryptUpdate(ctx, out, ctypes.byref(n), body, len(body)) == 1
                  and lib.EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_TAG, 16, ctypes.cast(tagbuf, vp)) == 1
                  and lib.EVP_DecryptFinal_ex(ctx, ctypes.create_string_buffer(32), ctypes.byref(fin)) == 1)
            if not ok:
                raise ValueError("authentication failed")
            return out.raw[:n.value]
        finally:
            lib.EVP_CIPHER_CTX_free(ctx)
    return "libcrypto", dec

db = os.path.join(datadir, "store.db")
if not os.path.isfile(db):
    check("store_db", "FAIL", "store.db not found in the management data volume")
    sys.exit(0)
print("store_db_bytes=%d events_db_present=%s" % (os.path.getsize(db), os.path.isfile(os.path.join(datadir, "events.db"))))
con = sqlite3.connect("file:%s?mode=ro" % db, uri=True)
tables = {r[0] for r in con.execute("select name from sqlite_master where type='table'")}

def scalar(sql, args=()):
    try:
        return con.execute(sql, args).fetchone()[0]
    except sqlite3.Error as e:
        return "error:%s" % type(e).__name__

counts = {t: (scalar("select count(*) from %s" % t) if t in tables else "absent")
          for t in ("accounts", "users", "peers", "setup_keys", "groups", "policies",
                    "networks", "network_routers", "network_resources", "routes")}
print("store_counts " + " ".join("%s=%s" % kv for kv in counts.items()))
ok = counts["accounts"] == 1 and isinstance(counts["peers"], int) and counts["peers"] > 0 \
    and isinstance(counts["users"], int) and counts["users"] > 0
check("store_db", "PASS" if ok else "FAIL", "accounts=%s users=%s peers=%s" % (counts["accounts"], counts["users"], counts["peers"]))
exp_peers = env_int("EXPECT_PEERS", 69)
check("peer_count", "PASS" if counts["peers"] == exp_peers else "WARN",
      "restored=%s reference=%s (reference is production on 2026-10-09)" % (counts["peers"], exp_peers))
sk = scalar("select count(*) from peers where user_id is null or user_id = ''") if "peers" in tables else "absent"
exp_sk = env_int("EXPECT_SETUP_KEY_PEERS", 1)
check("setup_key_peers", "PASS" if sk == exp_sk else "WARN", "restored=%s reference=%s" % (sk, exp_sk))
if "network_routers" in tables:
    bound = scalar("select count(*) from network_routers where peer = ?", (os.environ.get("ROUTING_PEER_ID", ""),))
    exp_r = env_int("EXPECT_ROUTERS", 6)
    check("routers_bound_to_routing_peer", "PASS" if bound == exp_r else "WARN", "restored=%s reference=%s" % (bound, exp_r))
if "accounts" in tables:
    cols = [r[1] for r in con.execute("pragma table_info(accounts)")]
    wanted = [c for c in cols if c in (
        "settings_peer_login_expiration_enabled", "settings_peer_login_expiration",
        "settings_peer_inactivity_expiration_enabled", "settings_peer_inactivity_expiration",
        "settings_jwt_groups_enabled", "settings_groups_propagation_enabled")]
    if wanted:
        row = con.execute("select %s from accounts limit 1" % ",".join(wanted)).fetchone()
        shown = []
        for c, v in zip(wanted, row):
            if c in ("settings_peer_login_expiration", "settings_peer_inactivity_expiration") and isinstance(v, int):
                v = "%gh" % (v / 3.6e12)
            shown.append("%s=%s" % (c.replace("settings_", ""), v))
        print("account_settings " + " ".join(shown))

# The key matches the datastore when it opens (authenticates) the encrypted user fields.
if len(raw) == 32 and "users" in tables:
    engine, dec = gcm_decryptor(raw)
    ucols = {r[1] for r in con.execute("pragma table_info(users)")}
    res = {}
    for field in ("email", "name"):
        okc = fail = plain = at = 0
        if field in ucols:
            for (v,) in con.execute("select %s from users where %s is not null and %s != ''" % (field, field, field)):
                try:
                    data = base64.b64decode(v, validate=True)
                except Exception:
                    plain += 1
                    continue
                if len(data) < 12 + 16:
                    plain += 1
                    continue
                try:
                    pt = dec(data[:12], data[12:])
                    okc += 1
                    at += b"@" in pt
                except Exception:
                    fail += 1
        res[field] = (okc, fail, plain, at)
        print("users_%s decrypted_ok=%d decrypt_failed=%d not_encrypted=%d%s"
              % (field, okc, fail, plain, (" with_at_sign=%d" % at) if field == "email" else ""))
    e_ok, e_fail = res["email"][0], res["email"][1]
    n_fail = res["name"][1]
    if e_fail or n_fail:
        check("datastore_key_matches", "FAIL", "%d email and %d name values failed GCM authentication (%s)" % (e_fail, n_fail, engine))
    elif e_ok:
        check("datastore_key_matches", "PASS", "all %d encrypted emails and %d names authenticate with the key (%s)" % (e_ok, res["name"][0], engine))
    else:
        check("datastore_key_matches", "WARN", "no encrypted user fields to test")
con.close()
PY

# 7. TLS certificate files and what each port serves (dates and fingerprints only).
LIVE="$LE/live/$DOMAIN"
if [ -n "$LE" ] && [ -s "$LIVE/fullchain.pem" ] && [ -s "$LIVE/privkey.pem" ]; then
  disk_end=$(openssl x509 -in "$LIVE/fullchain.pem" -noout -enddate | cut -d= -f2)
  disk_fp=$(openssl x509 -in "$LIVE/fullchain.pem" -noout -fingerprint -sha256 | cut -d= -f2)
  days=$(( ( $(date -d "$disk_end" +%s) - $(date +%s) ) / 86400 ))
  echo "disk_cert not_after=$(iso "$disk_end") days_left=$days subject=$(openssl x509 -in "$LIVE/fullchain.pem" -noout -subject | sed 's/^subject= *//')"
  if [ "$days" -ge 14 ]; then check tls_cert_files PASS "fullchain.pem and privkey.pem present, $days days left"
  else check tls_cert_files WARN "fullchain.pem and privkey.pem present, only $days days left"; fi
  for port in 443 33073 10000; do
    served=$(timeout 15 openssl s_client -connect "127.0.0.1:$port" -servername "$DOMAIN" </dev/null 2>/dev/null | openssl x509 2>/dev/null)
    if [ -z "$served" ]; then check "tls_served_$port" FAIL "no certificate served"; continue; fi
    s_end=$(printf '%s\n' "$served" | openssl x509 -noout -enddate | cut -d= -f2)
    s_fp=$(printf '%s\n' "$served" | openssl x509 -noout -fingerprint -sha256 | cut -d= -f2)
    if [ "$s_fp" = "$disk_fp" ]; then check "tls_served_$port" PASS "serves the disk certificate, not_after $(iso "$s_end")"
    else check "tls_served_$port" WARN "serves a different certificate, not_after $(iso "$s_end")"; fi
  done
else
  check tls_cert_files FAIL "fullchain.pem or privkey.pem missing under the letsencrypt volume"
fi
echo "checks_finished=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
REMOTE
}

# ---------------------------------------------------------------------------------------------
INSTANCE_ID=""
CLEANUP_ONLY=false
while [ $# -gt 0 ]; do
  case $1 in
    --print-remote-script) remote_script; exit 0 ;;
    --cleanup-only) CLEANUP_ONLY=true ;;
    --instance-id) INSTANCE_ID=${2:-}; shift ;;
    -h|--help) sed -n '2,34p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 64 ;;
  esac
  shift
done
# Refuse production before any AWS call.
if [ "$INSTANCE_ID" = "$PROD_INSTANCE" ]; then
  echo "REFUSED: $PROD_INSTANCE is the production control plane. This script never acts on it." >&2
  exit 3
fi

log() { echo "[$(date -u +%H:%M:%SZ)] $*"; }
trap 'echo "ERROR: line $LINENO: \"$BASH_COMMAND\" exited with status $?" >&2' ERR

to_epoch() {
  date -u -d "$1" +%s 2>/dev/null || date -j -u -f '%Y-%m-%dT%H:%M:%S' "${1:0:19}" +%s 2>/dev/null || echo ""
}
duration() {
  local a b
  a=$(to_epoch "$1"); b=$(to_epoch "$2")
  if [ -z "$a" ] || [ -z "$b" ]; then echo "n/a"; return; fi
  local d=$(( b - a ))
  if [ "$d" -lt 0 ]; then echo "n/a"; return; fi
  if [ "$d" -ge 3600 ]; then printf '%dh %02dm' $(( d / 3600 )) $(( d % 3600 / 60 ))
  else printf '%dm %02ds' $(( d / 60 )) $(( d % 60 )); fi
}

account=$(aws sts get-caller-identity --query Account --output text)
if [ "$account" != "$ACCOUNT" ]; then
  echo "REFUSED: this session is in account $account, expected $ACCOUNT (shared)." >&2
  exit 3
fi
log "Caller: $(aws sts get-caller-identity --query Arn --output text)"

# From here on, any unexpected exit (a failed AWS call, an expired SSO session) still runs the
# cleanup, best effort, so the copy never outlives the session.
CLEANED=false
# shellcheck disable=SC2329 # invoked by the EXIT trap
finish() {
  local rc=$?
  set +e
  if ! $CLEANED; then
    echo "Unexpected exit (status $rc): running the cleanup now." >&2
    cleanup
    proof
    echo "Re-run with --cleanup-only if the proof above is not all zero." >&2
  fi
  return "$rc"
}
trap finish EXIT

FAILS=0
WARNS=0
record() { # record <name> <PASS|WARN|FAIL> <detail>
  echo "CHECK $1 $2 $3"
  case $2 in FAIL) FAILS=$((FAILS + 1)) ;; WARN) WARNS=$((WARNS + 1)) ;; esac
}

START_UTC="" RP_CREATED="" RP_AMI="" LAUNCH_TIME="" SSM_ONLINE="" BOOT="" MGMT_STARTED="" API_401=""
REMOTE_OUTPUT=""

validate() {
  if [ -z "$INSTANCE_ID" ]; then
    local found
    found=$(aws ec2 describe-instances \
      --filters "Name=tag:Purpose,Values=$PURPOSE" "Name=instance-state-name,Values=pending,running" \
      --query 'Reservations[].Instances[].InstanceId' --output text)
    if [ -z "$found" ] || [ "$found" = None ]; then
      record copy_found FAIL "no running instance tagged Purpose=$PURPOSE: run restore-rehearsal-launch.sh first"
      return 0
    fi
    if [ "$(wc -w <<<"$found")" -ne 1 ]; then
      record copy_found FAIL "more than one rehearsal copy running ($found): cleanup removes them all"
      return 0
    fi
    INSTANCE_ID=$found
  fi
  [ "$INSTANCE_ID" != "$PROD_INSTANCE" ] || { echo "REFUSED: production instance" >&2; exit 3; }
  log "Validating the copy $INSTANCE_ID"

  # Posture through the API. Any FAIL here skips the checks on the copy.
  local desc tag_purpose public_ip tokens profile subnet state groups ingress eips user_data unencrypted
  desc=$(aws ec2 describe-instances --instance-ids "$INSTANCE_ID" --query "Reservations[0].Instances[0].[
      Tags[?Key=='Purpose']|[0].Value, PublicIpAddress, MetadataOptions.HttpTokens, IamInstanceProfile.Arn,
      SubnetId, State.Name, LaunchTime, Tags[?Key=='RehearsalStartUtc']|[0].Value,
      Tags[?Key=='RecoveryPointCreated']|[0].Value, Tags[?Key=='SourceRecoveryPoint']|[0].Value]" --output text)
  read -r tag_purpose public_ip tokens profile subnet state LAUNCH_TIME START_UTC RP_CREATED RP_AMI <<<"$desc"
  if [ "$tag_purpose" != "$PURPOSE" ]; then
    echo "REFUSED: $INSTANCE_ID is not tagged Purpose=$PURPOSE (tag: $tag_purpose)." >&2
    exit 3
  fi
  local before=$FAILS
  [ "$state" = running ] || [ "$state" = pending ] || record copy_state FAIL "state $state"
  if [ "$public_ip" = None ]; then record no_public_ip PASS "no public IP"; else record no_public_ip FAIL "public IP $public_ip"; fi
  eips=$(aws ec2 describe-addresses --filters "Name=instance-id,Values=$INSTANCE_ID" --query 'length(Addresses)' --output text)
  if [ "$eips" = 0 ]; then record no_eip PASS "no Elastic IP"; else record no_eip FAIL "$eips Elastic IP(s) associated"; fi
  if [ "$subnet" = "$SUBNET_ID" ]; then record private_subnet PASS "$subnet"; else record private_subnet FAIL "subnet $subnet"; fi
  groups=$(aws ec2 describe-instances --instance-ids "$INSTANCE_ID" \
    --query 'Reservations[0].Instances[0].SecurityGroups[].GroupName' --output text)
  if [ "$groups" = "$SG_NAME" ]; then
    local sg_id
    sg_id=$(aws ec2 describe-instances --instance-ids "$INSTANCE_ID" \
      --query 'Reservations[0].Instances[0].SecurityGroups[0].GroupId' --output text)
    ingress=$(aws ec2 describe-security-group-rules --filters "Name=group-id,Values=$sg_id" \
      --query 'length(SecurityGroupRules[?!IsEgress])' --output text)
    if [ "$ingress" = 0 ]; then record no_inbound PASS "$sg_id has 0 inbound rules"
    else record no_inbound FAIL "$sg_id has $ingress inbound rule(s)"; fi
  else
    record no_inbound FAIL "security groups '$groups', expected only $SG_NAME"
  fi
  case $profile in
    *"/$INSTANCE_PROFILE") record ssm_only_profile PASS "$INSTANCE_PROFILE" ;;
    *) record ssm_only_profile FAIL "instance profile $profile" ;;
  esac
  if [ "$tokens" = required ]; then record imdsv2 PASS "HttpTokens=required"; else record imdsv2 FAIL "HttpTokens=$tokens"; fi
  user_data=$(aws ec2 describe-instance-attribute --instance-id "$INSTANCE_ID" --attribute userData \
    --query 'UserData.Value' --output text)
  if [ "$user_data" = None ] || [ -z "$user_data" ]; then record no_user_data_attribute PASS "userData attribute empty"
  else record no_user_data_attribute FAIL "userData attribute is set"; fi
  unencrypted=$(aws ec2 describe-volumes --filters "Name=attachment.instance-id,Values=$INSTANCE_ID" \
    --query 'length(Volumes[?!Encrypted])' --output text)
  if [ "$unencrypted" = 0 ]; then record ebs_encrypted PASS "all attached volumes encrypted"
  else record ebs_encrypted FAIL "$unencrypted unencrypted volume(s)"; fi
  if [ "$FAILS" -gt "$before" ]; then
    log "Posture check failed: not running anything on the copy."
    return 0
  fi

  # Wait for the SSM agent on the copy.
  log "Waiting for SSM registration (up to ${SSM_WAIT_SECONDS}s)"
  local deadline ping
  deadline=$(( $(date +%s) + SSM_WAIT_SECONDS ))
  while :; do
    ping=$(aws ssm describe-instance-information --filters "Key=InstanceIds,Values=$INSTANCE_ID" \
      --query 'InstanceInformationList[0].PingStatus' --output text)
    [ "$ping" = Online ] && break
    if [ "$(date +%s)" -ge "$deadline" ]; then
      record ssm_online FAIL "not Online after ${SSM_WAIT_SECONDS}s (PingStatus $ping)"
      return 0
    fi
    sleep 10
  done
  SSM_ONLINE=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  record ssm_online PASS "Online at $SSM_ONLINE"

  # Run the read-only checks on the copy.
  local b64 params command_id status
  # gzip keeps the request small (about 8 KB); the copy decodes it with base64 and gunzip.
  b64=$(remote_script | gzip -9 -c | base64 | tr -d '\n\r')
  # shellcheck disable=SC2016 # $? and $rc belong to the shell on the copy, not this one
  params=$(printf '{"commands":["echo %s | base64 -d | gunzip > /tmp/com-234-checks.sh && EXPECT_PEERS=%s EXPECT_SETUP_KEY_PEERS=%s EXPECT_ROUTERS=%s ROUTING_PEER_ID=%s bash /tmp/com-234-checks.sh; rc=$?; rm -f /tmp/com-234-checks.sh; exit $rc"],"executionTimeout":["%s"]}' \
    "$b64" "$EXPECT_PEERS" "$EXPECT_SETUP_KEY_PEERS" "$EXPECT_ROUTERS" "$ROUTING_PEER_ID" "$((COMMAND_WAIT_SECONDS - 60))")
  command_id=$(aws ssm send-command --instance-ids "$INSTANCE_ID" --document-name AWS-RunShellScript \
    --comment "COM-234 restore rehearsal read-only checks" --parameters "$params" \
    --query Command.CommandId --output text)
  log "SSM command $command_id sent, waiting for it to finish"
  deadline=$(( $(date +%s) + COMMAND_WAIT_SECONDS ))
  while :; do
    sleep 10
    status=$(aws ssm get-command-invocation --command-id "$command_id" --instance-id "$INSTANCE_ID" \
      --query Status --output text 2>/dev/null || echo Pending)
    case $status in Success|Failed|TimedOut|Cancelled) break ;; esac
    if [ "$(date +%s)" -ge "$deadline" ]; then
      record remote_checks FAIL "command $command_id still $status after ${COMMAND_WAIT_SECONDS}s"
      return 0
    fi
  done
  REMOTE_OUTPUT=$(aws ssm get-command-invocation --command-id "$command_id" --instance-id "$INSTANCE_ID" \
    --query StandardOutputContent --output text)
  echo "----- output of the checks on the copy (command $command_id, status $status) -----"
  printf '%s\n' "$REMOTE_OUTPUT"
  echo "----- end of output -----"
  [ "$status" = Success ] || record remote_checks FAIL "command status $status"
  local f w
  f=$(grep -c '^CHECK [^ ]* FAIL' <<<"$REMOTE_OUTPUT" || true)
  w=$(grep -c '^CHECK [^ ]* WARN' <<<"$REMOTE_OUTPUT" || true)
  FAILS=$((FAILS + f))
  WARNS=$((WARNS + w))
  grep -q '^CHECK management_api PASS' <<<"$REMOTE_OUTPUT" || { [ "$f" -gt 0 ] || record management_api FAIL "no PASS line"; }
  BOOT=$(awk '$1=="T" && $2=="boot" {print $3}' <<<"$REMOTE_OUTPUT")
  MGMT_STARTED=$(awk '$1=="T" && $2=="mgmt_started" {print $3}' <<<"$REMOTE_OUTPUT")
  API_401=$(awk '$1=="T" && $2=="api_401" {print $3}' <<<"$REMOTE_OUTPUT")
}

cleanup() {
  log "Cleanup: removing every resource tagged Purpose=$PURPOSE"
  local ids id sid tag
  ids=$(aws ec2 describe-instances \
    --filters "Name=tag:Purpose,Values=$PURPOSE" "Name=instance-state-name,Values=pending,running,stopping,stopped,shutting-down" \
    --query 'Reservations[].Instances[].InstanceId' --output text)
  for id in $ids; do
    if [ "$id" = "$PROD_INSTANCE" ]; then echo "REFUSED: production id in the cleanup list" >&2; exit 3; fi
    tag=$(aws ec2 describe-instances --instance-ids "$id" \
      --query "Reservations[0].Instances[0].Tags[?Key=='Purpose']|[0].Value" --output text)
    [ "$tag" = "$PURPOSE" ] || { echo "REFUSED: $id lost its Purpose tag" >&2; exit 3; }
    aws ec2 terminate-instances --instance-ids "$id" --query 'TerminatingInstances[0].CurrentState.Name' --output text >/dev/null
    log "Terminating $id"
  done
  if [ -n "$ids" ]; then
    # shellcheck disable=SC2086 # word splitting of the id list is intended
    aws ec2 wait instance-terminated --instance-ids $ids
    log "Terminated: $ids"
  fi
  if [ "$(aws cloudwatch describe-alarms --alarm-names "$ALARM_NAME" --query 'length(MetricAlarms)' --output text)" != 0 ]; then
    aws cloudwatch delete-alarms --alarm-names "$ALARM_NAME"
    log "Deleted alarm $ALARM_NAME"
  fi
  local vols
  vols=$(aws ec2 describe-volumes --filters "Name=tag:Purpose,Values=$PURPOSE" "Name=status,Values=available" \
    --query 'Volumes[].VolumeId' --output text)
  for id in $vols; do aws ec2 delete-volume --volume-id "$id"; log "Deleted leftover volume $id"; done
  sid=$(aws ec2 describe-security-groups --filters "Name=vpc-id,Values=$VPC_ID" "Name=group-name,Values=$SG_NAME" \
    --query 'SecurityGroups[0].GroupId' --output text)
  if [ -n "$sid" ] && [ "$sid" != None ]; then
    local out
    for _ in $(seq 1 30); do
      [ "$(aws ec2 describe-network-interfaces --filters "Name=group-id,Values=$sid" \
        --query 'length(NetworkInterfaces)' --output text)" = 0 ] && break
      sleep 10
    done
    for _ in $(seq 1 20); do
      if out=$(aws ec2 delete-security-group --group-id "$sid" 2>&1); then
        log "Deleted security group $sid"
        break
      fi
      case $out in
        *DependencyViolation*) sleep 15 ;;
        *) echo "$out" >&2; break ;;
      esac
    done
  fi
}

proof() {
  echo "----- cleanup proof (describe calls, $(date -u +%Y-%m-%dT%H:%M:%SZ)) -----"
  local live vols enis sgs alarms tagged leftover=0 arn
  live=$(aws ec2 describe-instances \
    --filters "Name=tag:Purpose,Values=$PURPOSE" "Name=instance-state-name,Values=pending,running,stopping,stopped,shutting-down" \
    --query 'length(Reservations[].Instances[])' --output text)
  vols=$(aws ec2 describe-volumes --filters "Name=tag:Purpose,Values=$PURPOSE" --query 'length(Volumes)' --output text)
  enis=$(aws ec2 describe-network-interfaces --filters "Name=tag:Purpose,Values=$PURPOSE" --query 'length(NetworkInterfaces)' --output text)
  sgs=$(aws ec2 describe-security-groups --filters "Name=tag:Purpose,Values=$PURPOSE" --query 'length(SecurityGroups)' --output text)
  alarms=$(aws cloudwatch describe-alarms --alarm-names "$ALARM_NAME" --query 'length(MetricAlarms)' --output text)
  echo "instances_not_terminated=$live volumes=$vols network_interfaces=$enis security_groups=$sgs alarms=$alarms"
  for n in "$live" "$vols" "$enis" "$sgs" "$alarms"; do [ "$n" = 0 ] || leftover=1; done
  echo "terminated_instances=$(aws ec2 describe-instances --filters "Name=tag:Purpose,Values=$PURPOSE" \
    "Name=instance-state-name,Values=terminated" --query 'Reservations[].Instances[].InstanceId' --output text)"
  tagged=$(aws resourcegroupstaggingapi get-resources --tag-filters "Key=Purpose,Values=$PURPOSE" \
    --query 'ResourceTagMappingList[].ResourceARN' --output text)
  for arn in $tagged; do
    case $arn in
      *:instance/i-*)
        if [ "$(aws ec2 describe-instances --instance-ids "${arn##*/}" --query 'Reservations[0].Instances[0].State.Name' --output text)" = terminated ]; then
          echo "tagging_api $arn (terminated instance record, expires on its own)"
        else
          echo "tagging_api $arn NOT TERMINATED"; leftover=1
        fi ;;
      *) echo "tagging_api $arn LEFT OVER"; leftover=1 ;;
    esac
  done
  [ -n "$tagged" ] || echo "tagging_api: no resources tagged Purpose=$PURPOSE"
  echo "----- end of proof -----"
  return $leftover
}

if ! $CLEANUP_ONLY; then
  validate
fi
cleanup
CLEAN=0
proof || CLEAN=1
CLEANED=true

echo
echo "===== COM-234 restore rehearsal summary ====="
if ! $CLEANUP_ONLY; then
  echo "copy: ${INSTANCE_ID:-none}  recovery point: ${RP_AMI:-n/a} (backup started ${RP_CREATED:-n/a})"
  echo "restore start (launch request): ${START_UTC:-n/a}"
  echo "instance launched:   ${LAUNCH_TIME:-n/a}  (+$(duration "$START_UTC" "$LAUNCH_TIME"))"
  echo "copy booted:         ${BOOT:-n/a}  (+$(duration "$START_UTC" "$BOOT"))"
  echo "management started:  ${MGMT_STARTED:-n/a}  (+$(duration "$START_UTC" "$MGMT_STARTED"))"
  echo "SSM online (seen):   ${SSM_ONLINE:-n/a}  (+$(duration "$START_UTC" "$SSM_ONLINE"))"
  echo "API 401 (seen):      ${API_401:-n/a}  (+$(duration "$START_UTC" "$API_401"))"
  echo "RTO on the copy (launch request to management running): $(duration "$START_UTC" "$MGMT_STARTED")"
  echo "RPO (age of the restored data at the restore start):     $(duration "$RP_CREATED" "$START_UTC")"
  echo "checks: $FAILS FAIL, $WARNS WARN"
fi
echo "cleanup: $([ "$CLEAN" = 0 ] && echo complete || echo INCOMPLETE)"
if [ "$CLEAN" != 0 ]; then exit 2; fi
if ! $CLEANUP_ONLY && [ "$FAILS" -gt 0 ]; then exit 1; fi
exit 0
