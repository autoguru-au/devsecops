# Shared Datadog Terraform backend

AWS resources that let a repository manage Datadog (monitors, dashboards, SLOs) with Terraform from
GitHub Actions. It lives in the autoguru-shared account (`791686214595`, `ap-southeast-2`) as the CDK
stack `DatadogTerraformInfraStack`, defined in [`infra/`](infra/).

This repository holds the backend only. Each team's Terraform, and the workflows that plan and apply
it, stay in that team's own repository.

## What the stack provides

| Resource | Name |
|---|---|
| S3 state bucket (versioned, KMS, backed up) | `autoguru-datadog-terraform-state` |
| DynamoDB lock table | `autoguru-datadog-terraform-locks` |
| KMS key for state | `alias/terraform-state-key` |

And, per consumer repository, a pair of GitHub OIDC roles and two secret shells:

| Consumer | State key prefix | Write role (`main` only) | Read-only role (pull requests) | Secrets |
|---|---|---|---|---|
| `autoguru-au/mfe` | `mfe-portals/` | `github-actions-terraform-datadog` | `github-actions-terraform-datadog-readonly` | `datadog/mfe-monitors/{api-key,app-key}` |
| `autoguru-au/ignite` | `ignite/` | `github-actions-terraform-datadog-ignite` | `github-actions-terraform-datadog-ignite-readonly` | `datadog/ignite-monitors/{api-key,app-key}` |

ignite's roles can only read and write objects and lock entries under its own prefix. mfe's roles
predate a second consumer and can reach the whole bucket.

## Using it from a consumer repository

```hcl
terraform {
  backend "s3" {
    bucket         = "autoguru-datadog-terraform-state"
    key            = "<prefix>/monitors/terraform.tfstate"
    region         = "ap-southeast-2"
    dynamodb_table = "autoguru-datadog-terraform-locks"
    encrypt        = true
    kms_key_id     = "alias/terraform-state-key"
  }
}
```

Read the Datadog keys from the consumer's secrets with `aws_secretsmanager_secret_version`, and assume
the read-only role in the plan workflow and the write role in the apply workflow. mfe's
`.platform/datadog-monitors/terraform/` and `.github/workflows/terraform-*.yml` are the reference.

## Adding a consumer

1. Add a role pair, secret shells and outputs to `infra/lib/datadog-terraform-infra-stack.ts`,
   following ignite's block: scope state access to the consumer's own prefix.
2. Open a pull request. The workflow posts `cdk diff`; it should show only additions.
3. After merge, run **Datadog Terraform Backend** (`datadog-terraform-infra.yml`) with
   `action: deploy`.
4. Create a Datadog API key and an application key with the scopes the consumer needs (at least
   `monitors_read` and `monitors_write`), preferably owned by a service account, and store them:

   ```bash
   aws secretsmanager put-secret-value --profile shared --region ap-southeast-2 \
     --secret-id datadog/<consumer>-monitors/api-key --secret-string '<DD_API_KEY>'
   aws secretsmanager put-secret-value --profile shared --region ap-southeast-2 \
     --secret-id datadog/<consumer>-monitors/app-key --secret-string '<DD_APP_KEY>'
   ```

## Deploying

Deploys are manual. Run the **Datadog Terraform Backend** workflow with `action: diff` to preview
and `action: deploy` to apply. Locally: `cd infra && npm ci && AWS_PROFILE=shared npx cdk diff`.

Never run `cdk destroy` on this stack. The bucket, KMS key and secrets are retained, but the lock
table is not, and every consumer's Terraform depends on all of them.
