# 🔐 DevSecOps - Security Workflows

This repository contains reusable GitHub Actions workflows for **SonarQube** static analysis (TypeScript and .NET) and **Gitleaks** secret scanning. These workflows help automate **code quality and security checks** across multiple repositories in the organization.

---

## 📌 Workflows Available

### 🚀 sonarqube-typescript.yml
**Purpose:** Runs SonarQube analysis for TypeScript projects.

#### ✅ Steps Included:
- 📥 Checkout repository
- ⚙️ Setup Node.js
- 📦 Install SonarScanner
- 🧪 Run tests & collect coverage (if enabled)
- 🔎 Execute SonarQube analysis

#### 📄 Usage Example:
```yaml
name: SonarQube Analysis

on:
  push:
    branches:
      - develop

jobs:
  sonarqube:
    uses: autoguru-au/devsecops/.github/workflows/sonarqube-typescript.yml@main
    with:
      enable_tests: true
    secrets: inherit
```

---

### 🚀 sonarqube-dotnet.yml
**Purpose:** Runs SonarQube analysis for .NET projects.

#### ✅ Steps Included:
- 📥 Checkout repository
- ⚙️ Setup .NET environment
- 📦 Install SonarScanner
- 🧪 Run tests & collect coverage (if enabled)
- 🔎 Execute SonarQube analysis

#### 📄 Usage Example:
```yaml
name: SonarQube Analysis

on:
  push:
    branches:
      - develop

jobs:
  sonarqube:
    uses: autoguru-au/devsecops/.github/workflows/sonarqube-dotnet.yml@main
    with:
      enable_tests: true
    secrets: inherit
```

---

### 🕵️ gitleaks.yml
**Purpose:** Detects secrets checked into source or present in the Git history using the official Gitleaks binary. Produces a SARIF report and uploads it to the repository **Security** tab (code scanning) and as a workflow artefact.

#### ✅ Steps Included:
- 📥 Checkout repository with full history (`fetch-depth: 0`)
- ⬇️ Download a version-pinned Gitleaks binary and verify its SHA256
- 🎯 Determine the scan range from the triggering event (see *Scan modes* below)
- 🔎 Run `gitleaks detect` with `--redact` (logs never expose secret material)
- 📤 Upload the SARIF report as an artefact and to GitHub code scanning

#### 🎯 Scan modes:
The workflow chooses what to scan based on `github.event_name` so that PR checks stay fast on monorepos while baseline coverage is preserved:

| Trigger | Range scanned | Typical use |
|---|---|---|
| `pull_request` / `pull_request_target` | `origin/<base_ref>..HEAD` | PR gate: only commits introduced by the PR. Fast even on large histories. |
| `push` | `<before>..<after>` (or full history on the first push to a new branch) | Per-push check on default branch and feature branches. |
| `workflow_dispatch`, `schedule`, any other event | Full git history | Baseline scan: surfaces historical findings. Use this from a weekly cron once a baseline is established. |

#### ⚙️ Inputs:
| Name | Default | Description |
|---|---|---|
| `fail_on_finding` | `false` | If `true`, the job exits non-zero when secrets are found. Phase 1 keeps `false` to avoid breaking existing builds while teams triage findings. |
| `config_path` | `""` | Optional path to a `.gitleaks.toml` for repo-specific rules / allowlists. Empty uses Gitleaks defaults. |
| `gitleaks_version` | pinned | Gitleaks release tag (e.g. `v8.30.1`). Bump together with `gitleaks_sha256`. |
| `gitleaks_sha256` | pinned | SHA256 of the matching `linux_x64` tarball. Must match `gitleaks_version`. |

#### 📄 Usage Example:
```yaml
name: Gitleaks

on:
  push:
  pull_request:

jobs:
  gitleaks:
    uses: autoguru-au/devsecops/.github/workflows/gitleaks.yml@main
    with:
      fail_on_finding: false
```

---

## 📖 How to Use

1. 📂 **Add the appropriate workflow reference** in your repository inside `.github/workflows/`.
2. ⚙️ **Configure the inputs** (`enable_tests` for SonarQube, `fail_on_finding` for Gitleaks) as required.
3. 🔑 **Ensure your repository has the required secrets:**
   - SonarQube workflows: `SONAR_HOST_URL`, `SONAR_TOKEN`
   - Gitleaks workflow: no secrets required (the binary runs against the checked-out source).

---

## 🔒 Permissions & Access
If your repository is **internal**, ensure **GitHub Actions is enabled at the organization level** to allow reusable workflows.

### ⚙️ Implementation Steps:
- Integrate the workflow in your repository as described above.
- No additional manual configuration is required.

---

## 📈 Future Enhancements
- Enhance automation & reporting features.

---

## 📬 Support
For any issues or questions, please reach out to the **DevSecOps Team**.

