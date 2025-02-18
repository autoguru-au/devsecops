# 🔐 DevSecOps - SonarQube Workflows

This repository contains reusable GitHub Actions workflows for SonarQube analysis in **TypeScript** and **.NET** projects. These workflows help automate **code quality checks** across multiple repositories in the organization.

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

## 📖 How to Use

1. 📂 **Add the appropriate workflow reference** in your repository inside `.github/workflows/`.
2. ⚙️ **Configure the `enable_tests` parameter** to `true` or `false` as required.
3. 🔑 **Ensure your repository has the required secrets:**
   - `SONAR_HOST_URL`
   - `SONAR_TOKEN`

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

