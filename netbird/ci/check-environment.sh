#!/bin/bash
# Fail unless the GitHub deployment environment that gates Netbird deploys is protected (COM-219).
#
# A job that names an environment which does not exist makes GitHub create it with NO protection
# rules, and an admin can remove the rules later. Either way the deploy job would start without an
# approval. This check runs first in the plan and deploy jobs and refuses to go on unless the
# environment has required reviewers with prevent-self-review, no admin bypass, and a deployment
# branch policy that admits main only.
#
# Usage: check-environment.sh <owner/repo> <environment>   (needs GH_TOKEN with actions:read)
set -euo pipefail

repo="${1:?owner/repo}"
env_name="${2:?environment name}"

if ! body=$(gh api "repos/$repo/environments/$env_name"); then
  echo "::error::environment '$env_name' cannot be read; create it with its protection rules first (netbird/README.md, Deploy guard)"
  exit 1
fi

problems=$(jq -r '
  ([.protection_rules[]? | select(.type == "required_reviewers")][0]) as $r
  | [ if $r == null then "no required reviewers"
      elif (($r.reviewers // []) | length) == 0 then "the required reviewers list is empty"
      elif ($r.prevent_self_review != true) then "prevent_self_review is off"
      else empty end,
      if .can_admins_bypass != false then "admins can bypass the protection rules" else empty end,
      if .deployment_branch_policy == null then "no deployment branch policy (any branch can deploy)"
      elif .deployment_branch_policy.custom_branch_policies != true then "the branch policy is not a custom main-only policy"
      else empty end
    ] | .[]' <<<"$body")

if [ -z "$problems" ]; then
  # A custom policy is only as narrow as its patterns: require exactly one, the branch "main".
  if ! policies=$(gh api "repos/$repo/environments/$env_name/deployment-branch-policies"); then
    problems="cannot read the deployment branch policies"
  elif [ "$(jq -c '[.branch_policies[] | {name, type}]' <<<"$policies")" != '[{"name":"main","type":"branch"}]' ]; then
    problems="the branch policies are not exactly [main]: $(jq -c '[.branch_policies[] | {name, type}]' <<<"$policies")"
  fi
fi

if [ -n "$problems" ]; then
  while IFS= read -r p; do echo "::error::environment '$env_name': $p"; done <<<"$problems"
  exit 1
fi
echo "environment '$env_name' is protected: $(jq -c '{reviewers: [.protection_rules[]? | select(.type == "required_reviewers") | .reviewers[].reviewer | (.login // .slug)], can_admins_bypass}' <<<"$body"), branches [main]"
