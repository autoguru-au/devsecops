"""Tests for change_set_guard.py. Run: python3 -m unittest discover -s netbird/ci -v

The fixtures follow the shape of `aws cloudformation describe-change-set` output. The two
replacement cases mirror the real landmines: the 2026-10-07 change set that replaced the control
plane on ImageId, and CDK's UserDataCausesReplacement on the routing peer (a new logical ID, so the
old instance shows up as Remove).
"""

import json
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "change_set_guard.py")
CS = "gha-netbird-1-1"


def rc(logical, rtype, action, replacement=None):
    change = {"LogicalResourceId": logical, "ResourceType": rtype, "Action": action}
    if replacement is not None:
        change["Replacement"] = replacement
    return {"Type": "Resource", "ResourceChange": change}


def page(changes, status="CREATE_COMPLETE", execution="AVAILABLE", reason=None, token=None):
    out = {"ChangeSetName": CS, "Status": status, "ExecutionStatus": execution, "Changes": changes}
    if reason:
        out["StatusReason"] = reason
    if token:
        out["NextToken"] = token
    return out


class GuardTest(unittest.TestCase):
    def run_guard(self, fixtures, stacks, *extra, notfound=()):
        with tempfile.TemporaryDirectory() as d:
            for name, body in fixtures.items():
                with open(os.path.join(d, name), "w", encoding="utf-8") as f:
                    json.dump(body, f)
            for stack in notfound:
                open(os.path.join(d, f"{stack}.notfound"), "w").close()
            out_file = os.path.join(d, "github_output")
            env = dict(os.environ, NB_GUARD_FIXTURE_DIR=d, GITHUB_OUTPUT=out_file)
            env.pop("GITHUB_STEP_SUMMARY", None)
            args = [sys.executable, SCRIPT, "check", "--change-set", CS]
            for s in stacks:
                args += ["--stack", s]
            proc = subprocess.run(args + list(extra), capture_output=True, text=True, env=env)
            stacks_out = None
            if os.path.exists(out_file):
                with open(out_file, encoding="utf-8") as f:
                    for line in f:
                        if line.startswith("stacks="):
                            stacks_out = line.strip()[len("stacks="):]
            return proc.returncode, proc.stdout + proc.stderr, stacks_out

    def test_control_plane_ami_replacement_is_blocked(self):
        fx = {"NetbirdControlPlaneStack.json": page([
            rc("ControlPlaneE65BD0FC", "AWS::EC2::Instance", "Modify", "True"),
            rc("ControlPlaneEip", "AWS::EC2::EIP", "Modify", "False"),
        ])}
        code, out, stacks = self.run_guard(fx, ["NetbirdControlPlaneStack"])
        self.assertEqual(code, 1, out)
        self.assertIn("BLOCKED", out)
        self.assertEqual(stacks, "NetbirdControlPlaneStack")

    def test_routing_peer_new_logical_id_is_blocked(self):
        fx = {"NetbirdRoutingPeerStack.json": page([
            rc("RoutingPeerBBCF648333ceb47ca44a93d5", "AWS::EC2::Instance", "Remove"),
            rc("RoutingPeerBBCF6483aaaaaaaaaaaaaaaa", "AWS::EC2::Instance", "Add"),
            rc("RoutingPeerEip", "AWS::EC2::EIP", "Modify", "False"),
        ])}
        code, out, _ = self.run_guard(fx, ["NetbirdRoutingPeerStack"])
        self.assertEqual(code, 1, out)

    def test_allow_replacement_lets_a_planned_rebuild_through(self):
        fx = {"NetbirdRoutingPeerStack.json": page([
            rc("RoutingPeerBBCF648333ceb47ca44a93d5", "AWS::EC2::Instance", "Remove"),
            rc("RoutingPeerBBCF6483aaaaaaaaaaaaaaaa", "AWS::EC2::Instance", "Add"),
        ])}
        code, out, stacks = self.run_guard(fx, ["NetbirdRoutingPeerStack"], "--allow-replacement")
        self.assertEqual(code, 0, out)
        self.assertIn("ALLOWED BY INPUT", out)
        self.assertEqual(stacks, "NetbirdRoutingPeerStack")

    def test_in_place_user_data_update_and_new_resources_pass(self):
        fx = {"NetbirdControlPlaneStack.json": page([
            rc("ControlPlaneE65BD0FC", "AWS::EC2::Instance", "Modify", "False"),
            rc("TlsReloadAssociation", "AWS::SSM::Association", "Add"),
            rc("ManagementLogGroup", "AWS::Logs::LogGroup", "Add"),
            rc("ControlPlaneSgE7733C9A", "AWS::EC2::SecurityGroup", "Modify", "True"),
        ])}
        code, out, stacks = self.run_guard(fx, ["NetbirdControlPlaneStack"])
        self.assertEqual(code, 0, out)
        self.assertNotIn("BLOCKED", out)
        self.assertEqual(stacks, "NetbirdControlPlaneStack")

    def test_conditional_replacement_is_blocked(self):
        fx = {"NetbirdControlPlaneStack.json": page([
            rc("ControlPlaneE65BD0FC", "AWS::EC2::Instance", "Modify", "Conditional"),
        ])}
        code, out, _ = self.run_guard(fx, ["NetbirdControlPlaneStack"])
        self.assertEqual(code, 1, out)

    def test_removing_the_eip_or_the_setup_key_secret_is_blocked(self):
        fx = {"NetbirdRoutingPeerStack.json": page([
            rc("RoutingPeerEip", "AWS::EC2::EIP", "Remove"),
            rc("NetbirdSetupKey782EF6B4", "AWS::SecretsManager::Secret", "Remove"),
        ])}
        code, out, _ = self.run_guard(fx, ["NetbirdRoutingPeerStack"])
        self.assertEqual(code, 1, out)
        self.assertEqual(out.count("BLOCKED"), 2, out)

    def test_empty_change_set_is_a_no_op(self):
        fx = {"NetbirdControlPlaneStack.json": page([], status="FAILED", execution="UNAVAILABLE",
                                                    reason="The submitted information didn't contain changes. "
                                                           "Submit different information to create a change set.")}
        code, out, stacks = self.run_guard(fx, ["NetbirdControlPlaneStack"])
        self.assertEqual(code, 0, out)
        self.assertEqual(stacks, "")

    def test_missing_change_set_is_a_no_op(self):
        code, out, stacks = self.run_guard({}, ["NetbirdControlPlaneStack"], notfound=["NetbirdControlPlaneStack"])
        self.assertEqual(code, 0, out)
        self.assertEqual(stacks, "")

    def test_failed_change_set_is_an_error(self):
        fx = {"NetbirdControlPlaneStack.json": page([], status="FAILED", execution="UNAVAILABLE",
                                                    reason="Template format error")}
        code, out, _ = self.run_guard(fx, ["NetbirdControlPlaneStack"])
        self.assertEqual(code, 1, out)
        self.assertIn("::error::", out)

    def test_obsolete_change_set_is_an_error(self):
        fx = {"NetbirdControlPlaneStack.json": page([rc("A", "AWS::SSM::Association", "Add")],
                                                    execution="OBSOLETE")}
        code, out, _ = self.run_guard(fx, ["NetbirdControlPlaneStack"])
        self.assertEqual(code, 1, out)

    def test_blocked_change_on_a_later_page_is_found(self):
        fx = {
            "NetbirdControlPlaneStack.json": page([rc("A", "AWS::SSM::Association", "Add")], token="p2"),
            "NetbirdControlPlaneStack.p2.json": page([rc("ControlPlaneE65BD0FC", "AWS::EC2::Instance", "Modify", "True")]),
        }
        code, out, _ = self.run_guard(fx, ["NetbirdControlPlaneStack"])
        self.assertEqual(code, 1, out)
        self.assertIn("ControlPlaneE65BD0FC", out)

    def test_two_stacks_one_no_op(self):
        fx = {"NetbirdControlPlaneStack.json": page([rc("A", "AWS::SSM::Association", "Add")])}
        code, out, stacks = self.run_guard(fx, ["NetbirdControlPlaneStack", "NetbirdRoutingPeerStack"],
                                           notfound=["NetbirdRoutingPeerStack"])
        self.assertEqual(code, 0, out)
        self.assertEqual(stacks, "NetbirdControlPlaneStack")

    def test_execute_refuses_fixture_mode(self):
        with tempfile.TemporaryDirectory() as d:
            env = dict(os.environ, NB_GUARD_FIXTURE_DIR=d)
            proc = subprocess.run([sys.executable, SCRIPT, "execute", "--change-set", CS,
                                   "--stack", "NetbirdControlPlaneStack"], capture_output=True, text=True, env=env)
            self.assertEqual(proc.returncode, 1)


if __name__ == "__main__":
    unittest.main()
