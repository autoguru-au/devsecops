#!/usr/bin/env python3
"""Deploy guard for the Netbird stacks (COM-219): review a CloudFormation change set, then run it.

The Netbird instances hold state that a rebuild destroys. A replaced control plane loses the
management datastore, management.json and the certificates. A replaced routing peer re-enrols with
a new peer ID, and every network router bound to the old ID stops routing (COM-175). CloudFormation
replaces an instance silently whenever an immutable property changes (for example a re-resolved
AMI), and CDK expresses UserDataCausesReplacement as a new logical ID (Remove + Add).

  check    Describe the change set on each stack and refuse it if it removes, replaces or may
           replace a protected resource (instance, Elastic IP, volume, KMS key, secret), unless
           --allow-replacement is given. Writes a table to the job summary and the stacks that have
           changes to the step output `stacks`. A stack with no change set, or an empty one, is a
           no-op.
  execute  Check again, then execute each change set by name and wait for the stack to settle.
           What runs is exactly the change set the reviewer saw, never a fresh one.

Any state the script does not recognise is a failure, never a silent pass.

Test hook: with NB_GUARD_FIXTURE_DIR set, describe-change-set reads <dir>/<stack>.json (and
<dir>/<stack>.<NextToken>.json for later pages) instead of calling AWS, and <dir>/<stack>.notfound
simulates a missing change set. execute refuses to run in fixture mode.
"""

import argparse
import json
import os
import subprocess
import sys
import time

PROTECTED_TYPES = {
    "AWS::EC2::Instance",
    "AWS::EC2::EIP",
    "AWS::EC2::Volume",
    "AWS::KMS::Key",
    "AWS::SecretsManager::Secret",
}
NO_CHANGES_MARKERS = ("didn't contain changes", "No updates are to be performed")
FIXTURE_DIR = os.environ.get("NB_GUARD_FIXTURE_DIR")


class ChangeSetNotFound(Exception):
    pass


class GuardError(Exception):
    pass


def aws(*args):
    cmd = ["aws", "--output", "json", *args]
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        if "ChangeSetNotFound" in proc.stderr:
            raise ChangeSetNotFound(proc.stderr.strip())
        raise GuardError(f"{' '.join(cmd)} failed (rc={proc.returncode}): {proc.stderr.strip()}")
    return json.loads(proc.stdout) if proc.stdout.strip() else {}


def describe_page(stack, change_set, token):
    if FIXTURE_DIR:
        if os.path.exists(os.path.join(FIXTURE_DIR, f"{stack}.notfound")):
            raise ChangeSetNotFound(f"fixture: no change set {change_set} on {stack}")
        name = f"{stack}.json" if token is None else f"{stack}.{token}.json"
        with open(os.path.join(FIXTURE_DIR, name), encoding="utf-8") as f:
            return json.load(f)
    args = ["cloudformation", "describe-change-set", "--stack-name", stack, "--change-set-name", change_set]
    if token:
        args += ["--next-token", token]
    return aws(*args)


def describe(stack, change_set):
    """All pages of one change set: (first page, combined Changes)."""
    first = describe_page(stack, change_set, None)
    changes = list(first.get("Changes", []))
    token = first.get("NextToken")
    while token:
        page = describe_page(stack, change_set, token)
        changes += page.get("Changes", [])
        token = page.get("NextToken")
    return first, changes


def verdict(rc, allow_replacement):
    """(blocked, label) for one ResourceChange."""
    rtype = rc.get("ResourceType", "")
    action = rc.get("Action", "")
    replacement = rc.get("Replacement", "")
    if rtype not in PROTECTED_TYPES:
        return False, "ok"
    destructive = (
        action == "Remove"
        or action == "Dynamic"
        or (action == "Modify" and replacement in ("True", "Conditional"))
    )
    if action not in ("Add", "Modify", "Remove", "Import", "Dynamic"):
        destructive = True  # an action this guard does not know: fail closed
    if not destructive:
        return False, "ok"
    if allow_replacement:
        return False, "ALLOWED BY INPUT (destructive)"
    return True, "BLOCKED"


def check(stacks, change_set, allow_replacement):
    """Returns (stacks_with_changes, blocked, summary_lines)."""
    with_changes, blocked_any = [], False
    lines = [f"### Netbird change set `{change_set}`", ""]
    if allow_replacement:
        lines += ["> **allow_instance_replacement is ON.** Destructive changes to protected resources "
                  "are allowed for this run. The reviewer must confirm this is a planned rebuild.", ""]
    for stack in stacks:
        try:
            first, changes = describe(stack, change_set)
        except ChangeSetNotFound:
            lines += [f"**{stack}**: no change set (CDK found nothing to deploy). No-op.", ""]
            continue
        status = first.get("Status", "")
        reason = first.get("StatusReason", "") or ""
        if status == "FAILED" and any(m in reason for m in NO_CHANGES_MARKERS):
            lines += [f"**{stack}**: the change set is empty. No-op.", ""]
            continue
        if status != "CREATE_COMPLETE":
            raise GuardError(f"{stack}: change set {change_set} status is {status} ({reason})")
        execution = first.get("ExecutionStatus", "")
        if execution != "AVAILABLE":
            raise GuardError(f"{stack}: change set {change_set} execution status is {execution}, not AVAILABLE")
        if not changes:
            raise GuardError(f"{stack}: change set {change_set} is CREATE_COMPLETE but lists no changes")
        with_changes.append(stack)
        lines += [f"**{stack}**: {len(changes)} change(s)", "",
                  "| Logical ID | Type | Action | Replacement | Verdict |",
                  "|---|---|---|---|---|"]
        for change in changes:
            if change.get("Type") != "Resource":
                raise GuardError(f"{stack}: unexpected change type {change.get('Type')!r}")
            rc = change.get("ResourceChange", {})
            blocked, label = verdict(rc, allow_replacement)
            blocked_any = blocked_any or blocked
            lines.append("| {} | {} | {} | {} | {} |".format(
                rc.get("LogicalResourceId", "?"), rc.get("ResourceType", "?"),
                rc.get("Action", "?"), rc.get("Replacement", "-") or "-", label))
        lines.append("")
    if blocked_any:
        lines += ["**Refused.** This change set removes, replaces or may replace a protected resource. "
                  "Fix the cause (for example an unpinned AMI or a user-data change on the routing peer), "
                  "or re-run with allow_instance_replacement only for a planned, backed-up rebuild.", ""]
    return with_changes, blocked_any, lines


def emit(lines, stacks_with_changes):
    text = "\n".join(lines)
    print(text)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(text + "\n")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as f:
            f.write(f"stacks={' '.join(stacks_with_changes)}\n")


def wait_for_stack(stack, timeout_s=3600, poll_s=15):
    deadline = time.time() + timeout_s
    while True:
        status = aws("cloudformation", "describe-stacks", "--stack-name", stack)["Stacks"][0]["StackStatus"]
        if not status.endswith("_IN_PROGRESS"):
            return status
        if time.time() > deadline:
            raise GuardError(f"{stack}: still {status} after {timeout_s}s")
        time.sleep(poll_s)


def execute(stacks, change_set, allow_replacement):
    if FIXTURE_DIR:
        raise GuardError("execute is disabled in fixture mode")
    with_changes, blocked, lines = check(stacks, change_set, allow_replacement)
    emit(lines, with_changes)
    if blocked:
        return 1
    missing = [s for s in stacks if s not in with_changes]
    if missing:
        # The plan job reported changes on these stacks, so their change sets must still be there.
        raise GuardError(f"no executable change set {change_set} on {', '.join(missing)}; re-run the whole workflow")
    for stack in with_changes:
        print(f"Executing change set {change_set} on {stack}")
        aws("cloudformation", "execute-change-set", "--stack-name", stack, "--change-set-name", change_set)
        status = wait_for_stack(stack)
        print(f"{stack}: {status}")
        if status not in ("UPDATE_COMPLETE", "CREATE_COMPLETE"):
            raise GuardError(f"{stack}: deploy ended in {status}; see the stack events in the CloudFormation console")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("command", choices=["check", "execute"])
    parser.add_argument("--change-set", required=True)
    parser.add_argument("--stack", action="append", required=True, dest="stacks")
    parser.add_argument("--allow-replacement", action="store_true")
    args = parser.parse_args()
    try:
        if args.command == "check":
            with_changes, blocked, lines = check(args.stacks, args.change_set, args.allow_replacement)
            emit(lines, with_changes)
            return 1 if blocked else 0
        return execute(args.stacks, args.change_set, args.allow_replacement)
    except GuardError as e:
        print(f"::error::{e}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
