"""Offline tests for netbird/scripts/control-plane-settings-guard.sh.

Run: python3 -m unittest discover -s netbird/ci -v   (needs bash on PATH)

The script runs against stub docker, aws and curl commands, so these tests cover its logic, not the
live box: they cannot prove that the real management API accepts the PUT, that the instance role
can read the PAT secret, or that SSM runs the script as expected. The PUT body is compared with
what the dashboard's Settings > Authentication page sends (all returned settings, two fields set).
"""

import copy
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "..", "scripts", "control-plane-settings-guard.sh").replace("\\", "/")
PAT = "nbp_TESTTOKENtesttokenTESTTOKENtest0000"

ACCOUNT = {
    "id": "d0acc0unt",
    "domain": "autoguru.com.au",
    "settings": {
        "peer_login_expiration_enabled": True,
        "peer_login_expiration": 86400,
        "peer_inactivity_expiration_enabled": True,
        "peer_inactivity_expiration": 28800,
        "regular_users_view_blocked": True,
        "groups_propagation_enabled": True,
        "jwt_groups_enabled": True,
        "jwt_groups_claim_name": "groups",
        "jwt_allow_groups": [],
        "routing_peer_dns_resolution_enabled": True,
        "network_range": "100.64.0.0/10",
        "extra": {
            "peer_approval_enabled": False,
            "user_approval_required": False,
            "network_traffic_logs_enabled": False,
            "network_traffic_logs_groups": [],
            "network_traffic_packet_counter_enabled": False,
        },
    },
    "onboarding": {"onboarding_flow_pending": False, "signup_form_pending": False},
}

MGMT_JSON = {
    "DataStoreEncryptionKey": "c3RhYmxla2V5",
    "HttpConfig": {"IdpSignKeyRefreshEnabled": True},
    "PKCEAuthorizationFlow": {"ProviderConfig": {"RedirectURLs": [
        "http://localhost:53000", "http://localhost:8976", "http://localhost:35000", "http://localhost:43000"]}},
}

GOOD_ENV = ('NETBIRD_MANAGEMENT_TAG=0.74.7\n'
            'NETBIRD_AUTH_PKCE_REDIRECT_URL_PORTS="53000,8976,35000,43000"\n'
            'NETBIRD_MGMT_IDP_SIGNKEY_REFRESH=true\n')

DOCKER = r'''#!/bin/bash
[ -f "$FX/no_container" ] && [ "$1" = ps ] && exit 0
case "$1" in
  ps) echo cid123 ;;
  inspect)
    case "$3" in
      *Mounts*) echo "$FX/infra/artifacts/management.json" ;;
      *Config.Image*) echo "netbirdio/management:0.74.7@sha256:b63f" ;;
    esac ;;
esac
'''

AWS = r'''#!/bin/bash
case "$1 $2" in
  "secretsmanager get-secret-value")
    [ -f "$FX/pat" ] || { echo "ResourceNotFoundException" >&2; exit 254; }
    cat "$FX/pat" ;;
  "cloudwatch put-metric-data") echo "$*" >> "$FX/metrics.log" ;;
  *) echo "unexpected aws $*" >&2; exit 2 ;;
esac
'''

CURL = r'''#!/bin/bash
exec "$NB_TEST_PYTHON" "$NB_TEST_CURL_PY" "$@"
'''

CURL_PY = r'''
import json, os, sys
fx = os.environ["FX"]
args = sys.argv[1:]
with open(os.path.join(fx, "curl.log"), "a") as log:
    log.write(json.dumps(args) + "\n")
method, data, headers, url = "GET", None, [], None
i = 0
while i < len(args):
    a = args[i]
    if a == "-X": method = args[i + 1]; i += 1
    elif a == "--data": data = args[i + 1]; i += 1
    elif a == "-H": headers.append(args[i + 1]); i += 1
    elif a in ("--max-time", "--resolve"): i += 1
    elif a.startswith("https://"): url = a
    i += 1
for h in headers:
    if h.startswith("@"):
        with open(h[1:]) as f:
            with open(os.path.join(fx, "auth_header_seen"), "w") as out:
                out.write(f.read())
if os.path.exists(os.path.join(fx, "api_down")):
    sys.stderr.write("curl: (7) Failed to connect\n"); sys.exit(7)
state = os.path.join(fx, "accounts.json")
if method == "GET" and url.endswith("/api/accounts"):
    sys.stdout.write(open(state).read()); sys.exit(0)
if method == "PUT" and "/api/accounts/" in url:
    body = json.load(open(data[1:]))
    json.dump(body, open(os.path.join(fx, "put_body.json"), "w"))
    if not os.path.exists(os.path.join(fx, "put_noop")):
        accounts = json.load(open(state))
        accounts[0]["settings"] = body["settings"]
        json.dump(accounts, open(state, "w"))
    sys.stdout.write("{}"); sys.exit(0)
sys.stderr.write(f"unexpected {method} {url}\n"); sys.exit(22)
'''


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


class SettingsGuardTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp().replace("\\", "/")
        self.fx = f"{self.tmp}/fx"
        self.bin = f"{self.tmp}/bin"
        os.makedirs(f"{self.fx}/infra/artifacts")
        os.makedirs(self.bin)
        for name, body in (("docker", DOCKER), ("aws", AWS), ("curl", CURL),
                           ("python3", f'#!/bin/bash\nexec "{sys.executable.replace(chr(92), "/")}" "$@"\n')):
            path = f"{self.bin}/{name}"
            with open(path, "w", newline="\n") as f:
                f.write(body)
            os.chmod(path, 0o755)
        with open(f"{self.tmp}/curl_stub.py", "w") as f:
            f.write(CURL_PY)
        self.write_accounts([ACCOUNT])
        self.write_json(f"{self.fx}/infra/artifacts/management.json", MGMT_JSON)
        self.write_text(f"{self.fx}/infra/setup.env", GOOD_ENV)
        self.write_text(f"{self.fx}/opt-setup.env", GOOD_ENV)
        self.write_text(f"{self.fx}/pat", PAT)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write_text(self, path, text):
        with open(path, "w", newline="\n") as f:
            f.write(text)

    def write_json(self, path, obj):
        with open(path, "w") as f:
            json.dump(obj, f)

    def write_accounts(self, accounts):
        self.write_json(f"{self.fx}/accounts.json", accounts)

    def run_guard(self, mode="fix"):
        env = dict(os.environ, FX=self.fx, PATH=f"{self.bin}{os.pathsep}{os.environ['PATH']}",
                   NB_TEST_PYTHON=sys.executable.replace("\\", "/"), NB_TEST_CURL_PY=f"{self.tmp}/curl_stub.py",
                   NB_GUARD_OPERATOR_SETUP_ENV=f"{self.fx}/opt-setup.env", NB_GUARD_MODE=mode)
        proc = subprocess.run([shutil.which("bash", path=env["PATH"]) or "bash", SCRIPT],
                              capture_output=True, text=True, env=env)
        metrics = ""
        if os.path.exists(f"{self.fx}/metrics.log"):
            metrics = read(f"{self.fx}/metrics.log")
        return proc.returncode, proc.stdout + proc.stderr, metrics

    def assert_metrics(self, metrics, drift, failures):
        self.assertIn(f"MetricName=SettingsDrift,Value={drift},", metrics)
        self.assertIn(f"MetricName=SettingsGuardFailures,Value={failures},", metrics)

    def test_clean_state_passes_without_writing(self):
        code, out, metrics = self.run_guard()
        self.assertEqual(code, 0, out)
        self.assert_metrics(metrics, 0, 0)
        self.assertFalse(os.path.exists(f"{self.fx}/put_body.json"))

    def test_user_approval_reset_is_corrected_with_a_dashboard_shaped_put(self):
        drifted = copy.deepcopy(ACCOUNT)
        drifted["settings"]["extra"]["user_approval_required"] = True
        self.write_accounts([drifted])
        code, out, metrics = self.run_guard()
        self.assertEqual(code, 0, out)
        self.assert_metrics(metrics, 1, 0)
        body = json.loads(read(f"{self.fx}/put_body.json"))
        self.assertEqual(body, {"settings": ACCOUNT["settings"]})  # every setting kept, one field fixed
        self.assertIn("Authorization: Token nbp_", read(f"{self.fx}/auth_header_seen"))
        self.assertNotIn(PAT, read(f"{self.fx}/curl.log"))  # never on a command line
        self.assertNotIn(PAT, out)

    def test_check_mode_reports_but_does_not_write(self):
        drifted = copy.deepcopy(ACCOUNT)
        drifted["settings"]["extra"]["peer_approval_enabled"] = True
        self.write_accounts([drifted])
        code, out, metrics = self.run_guard(mode="check")
        self.assertEqual(code, 1, out)
        self.assert_metrics(metrics, 1, 0)
        self.assertFalse(os.path.exists(f"{self.fx}/put_body.json"))

    def test_a_correction_that_does_not_stick_is_a_failure(self):
        drifted = copy.deepcopy(ACCOUNT)
        drifted["settings"]["extra"]["user_approval_required"] = True
        self.write_accounts([drifted])
        self.write_text(f"{self.fx}/put_noop", "")
        code, out, metrics = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assert_metrics(metrics, 1, 1)

    def test_management_json_drift_is_reported_not_edited(self):
        cfg = copy.deepcopy(MGMT_JSON)
        cfg["HttpConfig"]["IdpSignKeyRefreshEnabled"] = False
        cfg["PKCEAuthorizationFlow"]["ProviderConfig"]["RedirectURLs"] = ["http://localhost:53000"]
        path = f"{self.fx}/infra/artifacts/management.json"
        self.write_json(path, cfg)
        code, out, metrics = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assert_metrics(metrics, 2, 0)
        self.assertEqual(json.loads(read(path)), cfg)

    def test_live_like_source_checkout_setup_env_is_reported(self):
        # 2026-10-08 state: the checkout's setup.env pins 0.74.6 and has no JWKS refresh line.
        self.write_text(f"{self.fx}/infra/setup.env",
                        'NETBIRD_MANAGEMENT_TAG=0.74.6\nNETBIRD_AUTH_PKCE_REDIRECT_URL_PORTS="53000,8976,35000,43000"\n')
        code, out, metrics = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assert_metrics(metrics, 2, 0)
        self.assertIn("NETBIRD_MGMT_IDP_SIGNKEY_REFRESH", out)
        self.assertIn("0.74.6", out)

    def test_unprovisioned_pat_is_a_failure(self):
        self.write_text(f"{self.fx}/pat", "x8#generated-by-cdk")
        code, out, metrics = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assert_metrics(metrics, 0, 1)
        self.assertFalse(os.path.exists(f"{self.fx}/curl.log"))

    def test_api_down_is_a_failure(self):
        self.write_text(f"{self.fx}/api_down", "")
        code, out, metrics = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assert_metrics(metrics, 0, 1)

    def test_more_than_one_account_is_a_failure(self):
        self.write_accounts([ACCOUNT, dict(ACCOUNT, id="other")])
        code, out, metrics = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assert_metrics(metrics, 0, 1)
        self.assertFalse(os.path.exists(f"{self.fx}/put_body.json"))

    def test_missing_management_container_is_a_failure(self):
        self.write_text(f"{self.fx}/no_container", "")
        code, out, metrics = self.run_guard()
        self.assertEqual(code, 1, out)
        self.assertIn("MetricName=SettingsGuardFailures,Value=1,", metrics)


if __name__ == "__main__":
    unittest.main()
