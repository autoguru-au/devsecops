# Historical Secrets Remediation Playbook

> **Status:** Draft v1 — awaiting CISO review (Jira [COM-120](https://autoguru.atlassian.net/browse/COM-120))
> **Owner:** Security programme (Guillermo Fernandez)
> **Last reviewed:** 2026-05-21
> **Applies to:** Findings produced by the reusable Gitleaks workflow (`autoguru-au/devsecops/.github/workflows/gitleaks.yml`) and any out-of-band discovery of secrets in Git history.

---

## 1. Purpose

When Gitleaks (or any other detection mechanism) surfaces a credential, token, key, or other secret material in a Git repository — current state or history — this playbook is the single source of truth for what happens next: who acts, in what order, how fast, where it gets recorded.

**It exists because** without a documented procedure, organisations either (a) panic and over-rotate everything, (b) silently allowlist findings without rotating, or (c) leave findings open while debating ownership. All three cause material risk.

The playbook is deliberately **conservative on speed, strict on completion**: an incident that takes a few extra hours but ends in clean rotation + verification beats one that is "closed in an hour" by allowlist.

## 2. Scope

| In scope | Out of scope |
|---|---|
| Findings from `gitleaks.yml` in any AutoGuru-managed repo | Live exploitation / active intrusion (escalate to general incident response) |
| Historical secrets discovered manually (e.g. PR review, audit) | Forgotten secrets in non-Git systems (S3 buckets, wikis, screenshots) — track separately |
| Secrets in private, internal, and public repos under `autoguru-au` | Vendor / partner repos AutoGuru does not control |
| Suspected exposure (someone forwarded a `.env` over Slack) | Loss of personal devices — laptop policy applies |

## 3. Glossary

- **Finding** — a single Gitleaks (or equivalent) report entry: file, line, commit, rule that matched, redacted sample.
- **Secret material** — the literal value of the credential. Never copy this into chat, tickets, or this playbook. Reference by commit SHA + line, never by content.
- **Rotation** — generating a new credential at the issuing service and invalidating the old one (NOT just generating a new one). A rotation that does not invalidate the old credential is not a rotation.
- **Containment** — preventing further use of the leaked credential while rotation is in flight. May include: temporarily revoking, scoping permissions down, IP-restricting, or shutting down the service that uses it.
- **Owner** — the team or individual in CODEOWNERS for the file where the finding lives. For repos without CODEOWNERS, the last committer of the offending file. The owner runs their own incident; the Security Lead is escalation.
- **Security Lead** — currently Guillermo Fernandez. Escalation point and final approver of incident closure. (Open action: see Appendix B.1 for the proposed `@autoguru-au/security` team.)
- **Out-of-band exposure** — evidence that the secret was used by someone other than its intended consumer between leak and rotation (CloudTrail/audit logs show foreign IP, foreign user agent, foreign access pattern, etc.).

## 4. Severity classification

### 4.1 Severity matrix

| Severity | Criteria | Rotation SLA from triage |
|---|---|---|
| **Critical** | Any of: production AWS access key, production database credential, payment processor key (Stripe live), PII access token, Auth0 production tenant secret, signing key with active sessions, repo is **public**, or **out-of-band exposure confirmed**. | **8 hours** |
| **High** | Non-prod AWS access key, internal service API token with elevated scope, Mandrill/email-sending key, third-party API token with write scope, repo is private but with broad org-wide read. | **48 hours** |
| **Medium** | Observability/monitoring credentials (Datadog, Sentry, New Relic), read-only third-party tokens, dev/staging database creds without prod data. | **5 days** |
| **Low** | Already-known-expired credentials confirmed by audit, internal-only tooling tokens with no external reachability, demo / test fixtures committed accidentally where the credential never existed in any system. | **14 days** |

SLA starts when the finding is **triaged** (severity assigned), not when it is detected. Triage SLA is **4 business hours** for any finding regardless of severity.

### 4.2 SLA enforcement

- The owner records the triage timestamp in the incident ticket (see Section 7) at the moment severity is set.
- The Security Lead audits open incidents weekly. Any open incident past SLA is escalated to the CISO.
- Rationale: chosen scale is conservative against operational reality at AutoGuru as of 2026-05. SLAs are a programme decision and will be reviewed at Phase 2 of COM-108. Reference: Drata **DCF-24** (SLA for security bugs).

### 4.3 Severity decision aid

When in doubt between two tiers, pick the higher one. Specifically:

- "Could this credential, if used by an attacker, cost money, leak customer data, or alter production state?" → **Critical**.
- "Is the repo public, or has the secret been in Git for more than 30 days, or did Gitleaks ever report any out-of-band view of it?" → **escalate one tier up**.
- "Is the credential demonstrably already expired / revoked / replaced before the finding?" → **Low** (still rotate any sibling credentials and verify the assumption).

## 5. Secret type ownership

### 5.1 Categories observed in AutoGuru repos

Derived from inspection of `autoguru`, `mfe`, `ignite`, `platform-manager`, and `octane` (2026-05-21).

| Category | Examples (verbatim from codebase) | Likely repo locations |
|---|---|---|
| **AWS infrastructure credentials** | IAM access keys, SSM Parameter Store secure strings, Secrets Manager values referenced via `GetParameter`, `SecretValue` (`SSMUtils.cs`, `AGVaultRoles.cs`) | `autoguru` (Platform.Infrastructure), `ag-vault`, CDK stacks across repos |
| **Database credentials** | `MSSQL_SA_PASSWORD` env var (`EnvironmentVariableNames.cs`), RDS master credentials in Secrets Manager | `autoguru`, `platform-manager` |
| **Payment processor (Stripe)** | Stripe API keys (`StripeConfig.cs`, `StripeWebhookController.cs`, `VerifyApiController.cs`, `CreateStripePaymentHandler`) | `autoguru/Retail/FPCS.Web` |
| **Identity / SSO (Auth0)** | OAuth client secrets (`AuthenticationServiceCollectionExtensions.cs`) | `autoguru/Retail/FPCS.Web` |
| **Voice / SMS (Twilio)** | `TWILIO_ENABLED` flag and underlying account SID / auth token | CCV microservice |
| **Email (Mandrill)** | API key wired via SSM (see Jira COM-119 for the SonarQube notification wiring) | `autoguru` Platform.Infrastructure |
| **Observability** | Datadog API/app keys, Sentry DSN, New Relic license keys (`DD_*` env vars) | All repos with monitoring |
| **CI/CD tokens** | GitHub Actions PATs, Octopus deploy tokens, npm publish tokens, Docker Hub credentials | `.github/workflows/` in every repo |
| **Signing / crypto keys** | JWT signing keys, OAuth client secrets, encryption keys for at-rest data | Backend services |
| **Internal service tokens** | API keys between AutoGuru microservices | Backend services |

This list is **not exhaustive**. Owners discovering a category not listed here should propose an addition via PR to this file.

### 5.2 Ownership matrix

Ownership is determined first by CODEOWNERS in the affected repo. The matrix below names the **default rotation actor** for each category when CODEOWNERS does not resolve to a single team.

| Category | Default rotation actor | Escalation |
|---|---|---|
| AWS infrastructure credentials | `@autoguru-au/platform` (per `mfe/.github/CODEOWNERS`, owns Datadog Terraform + pipeline infra; AWS by extension) | CISO + Platform Lead |
| Database credentials | `@autoguru-au/platform` (Platform owns RDS) | CISO + DBA on-call |
| Stripe | Owner of `autoguru/Retail/FPCS.Web` (currently `@autoguru-au/bed-leads` via default CODEOWNERS rule) | CISO + Finance |
| Auth0 | Owner of FPCS auth path | CISO |
| Twilio | CCV microservice owner | CISO |
| Mandrill | Platform Infrastructure team (`autoguru` Platform.Infrastructure CDK) | CISO |
| Observability | `@autoguru-au/platform` + `@autoguru-au/redwood` (per `mfe/.github/CODEOWNERS`) | Platform Lead |
| CI/CD tokens | Owner of the workflow file (CODEOWNERS on `.github/workflows/`) | CISO + GitHub org admin |
| Signing / crypto keys | Owner of the service that issues them | CISO |
| Internal service tokens | Owner of the issuing service | CISO + consumer-service owner |

**No `@autoguru-au/security` team exists in GitHub** as of 2026-05-21. Until it does, the Security Lead role rests with Guillermo Fernandez. Tracked in Appendix B.1.

## 6. Process flow

Eight phases. Each one must be evidenced in the incident ticket — no phase is implicit.

```
   ┌──────────┐   ┌────────┐   ┌────────────┐   ┌──────────┐
   │ Detect   │──▶│ Triage │──▶│ Contain    │──▶│ Rotate   │
   └──────────┘   └────────┘   └────────────┘   └────┬─────┘
                                                     │
   ┌──────────┐   ┌────────┐   ┌────────────┐        │
   │ Closure  │◀──│ Comms  │◀──│ Verify     │◀───────┘
   └──────────┘   └────────┘   └────────────┘
```

### 6.1 Detect

**Source:** Gitleaks workflow run, manual review, audit, external tip. Each finding has a unique identity: repo + commit SHA + file + line + rule ID.

**Action:**
- The Gitleaks workflow has already produced a SARIF report and posted findings to GitHub Code Scanning (Security tab).
- For out-of-band detection (manual, audit, tip), the discoverer opens an incident ticket within **1 business hour** of becoming aware.

### 6.2 Triage

**Action by owner, within 4 business hours of detection:**
1. Confirm the finding is not a false positive (placeholder string, well-known test value, documentation). If FP, jump to Section 6.7 with reason recorded; do not allowlist without recording.
2. Identify which **secret category** (Section 5.1) it belongs to.
3. Assign **severity** (Section 4.1) — apply the decision aid (4.3) when borderline.
4. Record category, severity, triage timestamp, and chosen rotation actor in the incident ticket.
5. SLA clock starts.

### 6.3 Contain

**Goal:** prevent abuse of the leaked credential while rotation is in flight. Containment is required when severity is **Critical** or when out-of-band exposure is suspected; recommended otherwise.

**Options, pick the smallest action that achieves containment:**
- **Scope-down**: reduce the IAM policy / OAuth scope of the credential to the minimum its current consumers need (no destructive ops, no broad reads).
- **IP-restrict**: tighten the source IP allowlist on the credential.
- **Shadow-disable**: monitor the credential without disabling, so attempted abuse generates alerts.
- **Full disable**: only when severity is Critical and rotation will take longer than 1 hour, accepting that legitimate consumers may break.

Record the containment action and timestamp.

### 6.4 Rotate

**Action by rotation actor:**
1. Generate a **new** credential at the issuing service. Store it in the appropriate secret store (SSM Parameter Store with `SecureString` type, or AWS Secrets Manager — never in code, never in `.env` checked into Git).
2. Update consumers:
   - CDK stacks reading from SSM/Secrets Manager pick up the new value on next deploy. Trigger redeploy.
   - Lambda env vars wired to SSM update via `aws lambda update-function-configuration` if not auto-redeployed.
   - Long-running services may need restart.
3. **Verify consumers function** with the new credential **before** moving to Section 6.5.

Common rotation patterns:

| Category | Issuer action | Storage update | Consumer update |
|---|---|---|---|
| AWS IAM key | IAM console / `aws iam create-access-key` then `delete-access-key` for the old | SSM/Secrets Manager Parameter | Deploy / restart consumers |
| RDS user password | `ALTER LOGIN ... WITH PASSWORD = '...'` or RDS master rotation via Secrets Manager managed rotation | Secrets Manager Parameter | Connection pool reset |
| Stripe key | Roll in Stripe Dashboard → API Keys | SSM Parameter | Deploy FPCS.Web |
| Auth0 client secret | Auth0 Dashboard → Application → Settings | SSM Parameter | Deploy authentication service |
| GitHub PAT | github.com → Settings → Developer settings → PATs → Regenerate | GitHub Actions secret | None (pulled at workflow run) |
| Octopus deploy token | Octopus → Configuration → Users → API keys | GitHub Actions secret | None |

### 6.5 Invalidate

**This is a separate step.** Rotation generates a new credential; invalidation kills the old one. Both must happen.

**Action:**
- Delete the old credential at the issuing service. Verify via the issuer's API (e.g. `aws iam list-access-keys --user X` should not list the old key id).
- If the issuing service does not support immediate deletion, schedule deletion for the earliest available time and record the scheduled deletion timestamp.

A finding is **never closed** while the old credential is still valid at the issuer.

### 6.6 Verify

**Action:**
- Verify the leaked credential value, used against the issuer, returns "invalid" / "denied". Use a one-off check from a controlled environment; do not paste the credential into ad-hoc shells. Authorised actor only.
- Check audit logs (CloudTrail for AWS, Stripe audit log, Auth0 logs, etc.) covering the window from earliest known leak time to invalidation timestamp. Look for: use from unexpected IPs / user agents, unusual access patterns, calls the legitimate consumer would not make.
- Record findings: "no evidence of misuse" OR "evidence detected — escalate to Section 6.7 with sub-incident".

### 6.7 Communicate

**Internal notification at triage** (severity = Critical or High):
- Slack: post in the security channel (see Section 7.1 — **channel selection is an open programme action**, see Appendix B.2).
- Include: incident ticket link, category, severity, rotation actor, SLA deadline. **Never include the secret material**.

**Internal notification at closure** (all severities):
- Post resolution summary to the same channel.
- Include: rotation timestamp, invalidation timestamp, verification outcome, any out-of-band exposure detected.

**External notification** (customers, regulators) is triggered **only** by confirmed out-of-band exposure of customer data. In that case the Security Lead escalates to the CISO, and the procedure handed off is **Drata DCF-135 — Notice of Breach to Affected Users**. This playbook does not cover the external notification flow.

**Compliance evidence** uploads (Drata "Reports & Docs") happen at closure if the incident touches any of: DCF-17, DCF-24, DCF-278, DCF-783. See Appendix A.

### 6.8 Closure

A finding can only be closed when **all** of the following are evidenced in the ticket:

- [ ] Triage timestamp + assigned severity recorded
- [ ] Containment action recorded (or explicit "not applicable, severity is Medium/Low" justification)
- [ ] New credential issued + stored + consumers verified working
- [ ] Old credential invalidated at the issuer (with evidence)
- [ ] Verification outcome recorded (no abuse OR sub-incident opened)
- [ ] Communication posted (triage + closure)
- [ ] If the offending commit is in Git history, decision recorded: history rewrite via `git filter-repo` (rare, only when public exposure of high-impact secret) OR accept and rely on invalidation (default)

**Allowlist policy:** allowlisting a Gitleaks finding (`.gitleaks.toml` allowlist entry) is **only** acceptable when the finding is a confirmed false positive (placeholder, documented test fixture, well-known public example). Allowlist entries must include a comment with: ticket link, reason, and reviewer initials. Allowlisting to silence a real finding is a programme violation.

The Security Lead approves closure.

## 7. Communication & tracking

### 7.1 Channels

| Channel | Purpose | State |
|---|---|---|
| Slack `#security-incidents` (or equivalent — **name TBD**) | Triage notification and closure summary | **Open action — channel does not exist yet (Appendix B.2)** |
| Jira project COM, label `incident-secret` | Incident ticket of record. One ticket per finding. | Existing project; label introduced by this playbook. |
| GitHub Code Scanning Security tab (per repo) | Finding lifecycle: open / fixed / dismissed | Existing |
| Drata "Reports & Docs" | Compliance evidence at closure (when DCF-17 / DCF-24 / DCF-278 / DCF-783 are touched) | Existing |

Until the Slack channel exists (Appendix B.2), the Security Lead is notified directly (DM or email). The first incident under this playbook is gating on channel creation, not blocked by it.

### 7.2 Recordkeeping

**Per-incident ticket fields (in Jira COM with label `incident-secret`):**

| Field | Example |
|---|---|
| Title | `[Secret-Incident] <category> in <repo>/<file>:<line>` |
| Severity | one of Critical / High / Medium / Low |
| Category | from Section 5.1 |
| Rotation actor | GitHub handle or team |
| Detected at | ISO timestamp |
| Triaged at | ISO timestamp |
| Contained at | ISO timestamp (or "n/a — Medium/Low") |
| Rotated at | ISO timestamp |
| Invalidated at | ISO timestamp |
| Verified at | ISO timestamp |
| Closed at | ISO timestamp |
| Drata DCFs touched | comma-separated list |
| Linked finding | GitHub Code Scanning alert URL |
| Linked PR (containment / rotation) | PR URL |

**Programme-level aggregate**, reviewed monthly by the Security Lead:
- Open incidents by age vs SLA
- Closed incidents by category (which categories produce the most findings — informs Section 5.1 maintenance)
- Average time-to-rotation per severity

## 8. Walkthrough examples

Worked examples for the three category archetypes that drive most secret leak incidents at AutoGuru. Example C (GitHub PAT) was actually exercised end-to-end during this playbook's validation; A and B are illustrative and follow the same eight-phase structure.

### 8.1 Example A — AWS access key

**Hypothetical finding:** an IAM access key id matching the `AKIA[0-9A-Z]{16}` pattern committed in a `.env.development` file.

Walkthrough:

1. **Detect** — Gitleaks workflow posted SARIF to Code Scanning; finding visible in the repo Security tab, rule `aws-access-token` or `generic-api-key` depending on context.
2. **Triage** — Category: AWS infrastructure credentials. Severity: **Critical** if the key is in scope for a production account, **High** for non-prod. Apply the decision aid (4.3); if the repo is public or the commit is older than 30 days, escalate one tier.
3. **Contain** — IAM console → attach an inline policy that denies all actions on the IAM user (`{"Effect": "Deny", "Action": "*", "Resource": "*"}`). Alternatively `aws iam update-access-key --status Inactive` if you can accept legitimate consumer breakage.
4. **Rotate** — `aws iam create-access-key --user-name <user>`; new credential pair stored in SSM Parameter Store as `SecureString` (or Secrets Manager); CDK stack referencing the parameter redeployed; consumers verified.
5. **Invalidate** — `aws iam delete-access-key --access-key-id <old-id>`; confirm with `aws iam list-access-keys --user-name <user>` (old id no longer listed).
6. **Verify** — Authorised actor attempts an API call with the leaked key id from a controlled environment, expects `InvalidAccessKeyId`. Search CloudTrail for `userIdentity.accessKeyId == <leaked>` between earliest known leak time and the invalidation timestamp; look for foreign IPs or unusual access patterns.
7. **Communicate** — Triage post (severity Critical/High) and closure post in the security channel.
8. **Closure** — Ticket fields completed; Drata evidence uploaded under DCF-783 (rotation) and DCF-278 (key retirement).

> **Note on detection:** Gitleaks v8.30.1 default rules deliberately exclude the well-known AWS canonical example `AKIAIOSFODNN7EXAMPLE` from matching. This is correct behaviour — that value is published by AWS as a documentation placeholder, never accepted by any real service. Real production AWS keys still match.

### 8.2 Example B — Database connection string

**Hypothetical finding:** a SQL Server connection string with embedded password committed in source code or a `.env*` file.

Walkthrough:

1. **Detect** — Gitleaks rule `generic-api-key` (entropy-based) may or may not match a plain password depending on its character classes. Manual review of the file may be necessary; the finding may originate from PR review rather than the scanner.
2. **Triage** — Category: Database credentials. Severity: **Critical** if production database, **High** if staging, **Medium** if dev/sandbox without prod data.
3. **Contain** — restrict the SQL login source IPs via firewall to current consumer IPs only, or temporarily revoke the login pending rotation.
4. **Rotate** — `ALTER LOGIN <login> WITH PASSWORD = '<new>'`; new password stored in Secrets Manager (with managed rotation if supported); connection pool of consumers reset.
5. **Invalidate** — SQL auth has no "delete password"; invalidation is implicit in the rotation step. Verify by attempting a connection with the old password from a controlled environment → expected `Login failed for user`.
6. **Verify** — Query the SQL audit log for any successful login with the old hash between leak and rotation. Confirm no foreign IPs.
7. **Communicate** — Triage + closure posts.
8. **Closure** — Ticket fields completed; Drata evidence uploaded under DCF-783.

> **Detection nuance:** Plain-password connection strings without distinctive prefixes are the hardest class of secret to detect by pattern. Defense in depth: a `.gitleaks.toml` with a custom rule for connection-string-shaped strings, plus code review discipline preventing them being committed in the first place.

### 8.3 Example C — Third-party API token (GitHub PAT) — VALIDATED

**Actual finding from the validation run:** a GitHub PAT matching the `ghp_[A-Za-z0-9]{36}` pattern, committed in `tests/fixtures/COM-120-fake-secrets-DO-NOT-USE.md:50` (synthetic placeholder). Detected simultaneously by rules `github-pat` and `generic-api-key`.

Walkthrough exercised against the validation PR:

1. **Detect** — Gitleaks workflow `gitleaks-COM-120-validation` (caller of the reusable `gitleaks.yml`) generated a SARIF report. Workflow run https://github.com/autoguru-au/platform-manager/actions/runs/26191286634 — green job, 344 commits scanned in 1.26 s, 50 leaks found total (the validation fixture's 2 plus 48 pre-existing findings in repo history that are out of scope for this walkthrough). SARIF artefact id 7122079191 retained on the run.
2. **Triage** — Category: CI/CD tokens. The synthetic value is structurally a PAT but the body is `1111…AAAA` (rejected by github.com). For a *real* PAT finding, severity is **High** if write scope to org, **Medium** if read-only on a personal account, escalated one tier if the repo is public.
3. **Contain** — github.com → Settings → Developer settings → Personal access tokens → Revoke immediately. PATs have no scope-down option; the only containment action is revoke.
4. **Rotate** — Generate new PAT with the minimum scopes that the consumer actually needs (do not just regenerate with the same scope set; review). Update consumer (GitHub Actions secret, Octopus variable, etc.).
5. **Invalidate** — Step 3 already revoked the old PAT. Confirm via `gh api -H "Authorization: token <old>" /user` → expected `HTTP 401 Bad credentials`.
6. **Verify** — GitHub organisation audit log (admin-level) filtered by `actor:<username>` and time window from earliest known leak to revocation. Inspect for foreign IP user agents and unexpected `repo:read` or `repo:write` events.
7. **Communicate** — Triage and closure posts in the (TBD) security channel. For this walkthrough, the actor was the Security Lead and the post is the playbook itself.
8. **Closure** — Validation PR closed without merge, branch deleted (Section 9). Drata evidence linked to the run URL above.

## 9. Validation evidence

The first end-to-end run of this playbook took place on 2026-05-21 against the throwaway branch `test/COM-120-playbook-validation` in `autoguru-au/platform-manager`.

**Fixture file:** `tests/fixtures/COM-120-fake-secrets-DO-NOT-USE.md` — contained three synthetic secrets (AWS canonical example access key, fake SQL Server connection string, placeholder GitHub PAT). All three are non-exploitable by construction.

**Caller workflow:** `.github/workflows/gitleaks-COM-120-validation.yml` — invoked the reusable `autoguru-au/devsecops/.github/workflows/gitleaks.yml@feature/COM-115-reusable-gitleaks`.

**Outcome:**

- **Workflow run** https://github.com/autoguru-au/platform-manager/actions/runs/26191286634 — green.
- **Gitleaks scan** — 344 commits, ~8.4 MB scanned in 1.26 s. 50 leaks reported in total.
- **From the fixture file** (2 findings): rules `github-pat` and `generic-api-key`, both on the synthetic PAT (line 50). The AWS canonical example and the synthetic SQL connection string were **not** flagged by Gitleaks default rules — see notes in Sections 8.1 and 8.2 for why.
- **Pre-existing findings** in repo history (48 findings) — categorical breakdown reported separately to the Security Lead. **Out of scope** for this playbook validation; will be triaged by the platform-manager owners following this playbook once the reusable workflow is rolled out under COM-116.

**SARIF artefact** preserved on the run (id 7122079191, 195 KB, default 90-day retention). The artefact is the source of truth for the per-finding evidence; values are `--redact`-ed by Gitleaks so paths and commit references are visible but secret material is not.

**Two real defects in the COM-115 workflow were surfaced and fixed in the same session:**

- `permissions:` block missing `actions: read`, which prevented the SARIF post-processing from reading the workflow run metadata.
- Hard failure on consumer repos without GitHub Code Security enabled. Fixed with `continue-on-error: true` on the upload-to-code-scanning step so the SARIF artefact remains the durable evidence regardless of Code Scanning availability.

Both fixes are part of the COM-115 PR ([devsecops PR #5](https://github.com/autoguru-au/devsecops/pull/5)) and were verified against the same validation harness post-fix.

**Cleanup completed at end of validation:** PR closed without merge, branch `test/COM-120-playbook-validation` deleted from `autoguru-au/platform-manager`. Any Code Scanning alerts attached to the branch are auto-dismissed by GitHub when the branch is removed. The workflow run history remains accessible for audit.

---

## Appendix A — Drata control mapping

| Drata DCF | Title | Touched by this playbook at |
|---|---|---|
| **DCF-783** | Credentials Rotation | Section 6.4 (rotation cadence and execution) |
| **DCF-707** | Credentials Not Hard-Coded | Section 6.4 (storage in SSM/Secrets Manager, not source) |
| **DCF-24** | SLA for Security Bugs | Section 4 (severity matrix + SLAs) |
| **DCF-17** | Remediation Plan | This playbook is the documented remediation plan for the secret-leak class of finding |
| **DCF-135** | Notice of Breach to Affected Users | Section 6.7 (external notification trigger) |
| **DCF-8** | Disclosure Process for Customers | Section 6.7 (external notification) |
| **DCF-278** | Key Retirement | Section 6.5 (invalidation) |
| **DCF-229** | Vendor Default Accounts | Out of scope here, but findings of default vendor credentials should be treated under this playbook's severity matrix |

## Appendix B — Open programme actions

These are prerequisites or strong improvements identified while writing this playbook. They are out of scope for COM-120 but tracked here so the playbook owner can close them out as the programme matures.

### B.1 Create `@autoguru-au/security` GitHub team

No security team exists in GitHub today. The Security Lead role is held by an individual. Creating the team unlocks:
- Single CODEOWNERS entry across all repos for security-relevant paths
- Reviewer-of-record for `.gitleaks.toml` allowlist changes
- Audit-friendly assignment of incident tickets

**Suggested next step:** Jira ticket under COM project, "Create @autoguru-au/security GitHub team and onboard initial members".

### B.2 Create a Slack channel for security incidents

No dedicated channel exists. Without it, Section 6.7 (Communicate) cannot run repeatably.

**Suggested next step:** Jira ticket under COM project, "Create #security-incidents Slack channel; configure SNS subscription for AWS GuardDuty / Security Hub findings; wire to PagerDuty if applicable". Decision needed on whether to repurpose an existing channel (e.g. `#platform-alerts`) or create new.

### B.3 Programmatically detect `.gitleaks.toml` allowlist additions

A new allowlist entry should require Security Lead review. Today nothing enforces this.

**Suggested next step:** branch protection rule on `devsecops` and any consumer repos requiring `@autoguru-au/security` (or, until then, the Security Lead) as required reviewer on PRs that touch `.gitleaks.toml`.

### B.4 Incident dashboard

Aggregate view of open / closed secret incidents, age vs SLA, category breakdown. Required for the monthly Security Lead review (Section 7.2).

**Suggested next step:** Jira filter + dashboard; later, Grafana / Datadog dashboard if volume justifies.

### B.5 Enable GitHub Code Security on consumer repos

The Gitleaks reusable workflow uploads SARIF to two places: a workflow artefact (always works, durable evidence) and the repo's Security tab (only works if **GitHub Code Security** is enabled at the repo or organisation level). As of 2026-05 most private AutoGuru repos do **not** have it enabled, so findings live only in the artefact, not in the Security tab UI.

The COM-115 workflow is engineered to degrade gracefully (`continue-on-error: true` on the upload step), so the job stays green either way — but the Security tab UI is the better triage surface and should be enabled organisation-wide.

**Suggested next step:** GitHub org admin enables Code Security on the `autoguru-au` organisation (Settings → Code security and analysis), or enables it per-repo for the priority repos identified in COM-109. No code change required.

### B.6 Custom Gitleaks rules for AutoGuru-specific patterns

Default Gitleaks rules cover most well-known third-party services (AWS, Stripe, GitHub PATs, etc.) but do not cover:

- AutoGuru-internal service tokens (no distinctive prefix today)
- Connection strings to Azure SQL / RDS with embedded passwords (no high-entropy prefix to match)
- Mandrill API keys (key format may not match default rule)

**Suggested next step:** populate a centralised `.gitleaks.toml` in `autoguru-au/devsecops` with custom rules for these categories. The reusable workflow already accepts a `config_path` input — no workflow change required to start using it. Coordinate with the Platform team to enumerate distinctive patterns.

## Appendix C — Maintenance

This playbook is reviewed:

- **At every major change** to the Gitleaks workflow (`autoguru-au/devsecops/.github/workflows/gitleaks.yml`).
- **At every closed incident** — the reviewing actor checks whether the incident surfaced a gap and proposes a PR if so.
- **Quarterly** by the Security Lead regardless of activity, to verify the secret type inventory (Section 5.1) and ownership matrix (Section 5.2) still match reality.

Changes to this playbook are made via PR against `develop` in `autoguru-au/devsecops`, with the Security Lead as required reviewer.
