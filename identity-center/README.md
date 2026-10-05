# Identity Center

Infrastructure as Code for AutoGuru's IAM Identity Center **groups**: the groups themselves, their
memberships, their application assignments, and the permission sets that belong to them.

This directory is the single definition of these groups. Other repositories read groups (the AIOps
UUID map, GuruShare's group mirror, ignite's MLflow instance list) but must not declare an
`AWS::IdentityStore::Group`. Add or change a group here, by pull request.

- Instance `ssoins-8259316042a4e826`, identity store `d-97675b6074`.
- Owned by the Organizations management account `406422318285`, `ap-southeast-2`. There is no
  delegated administrator, so that is the only account that can write Identity Center.
- Deployed from a CodePipeline in **autoguru-shared** (`791686214595`).

## What is here

| Group | Members | Application | Permission set |
| --- | --- | --- | --- |
| `App-AIOps` | console-managed (predates this stack) | AIOps (assigned in the console) | none |
| `App-TechLeadership` | console-managed (predates this stack) | assigned in the console | none |
| `AIOps-Mlflow-Production` | none | none | `AIOpsMlflowProduction` in Production, still declared in autoguru `master-06-permissionsets.yml` |
| `App-AIOps-Evaluate` | Amir Zahedi, Adam Webb | AIOps | `AIOpsEvaluation` in shared (AI-777) |
| `App-AIOps-Rater` | none yet: the second rater is not named | AIOps | **never**: raters rank and label in the console only |
| `App-AIOps-Unblind` | Amir Zahedi | AIOps | none |

Not here, on purpose:

- **`App-AllStaff`**: autoguru's `AllStaffGroupSyncFunction` owns its membership and finds it by name.
- **Permission sets other than the ones above**: they stay in autoguru
  `eng/Infrastructure/src/01-cloud-foundation/01-stacks/01-master/master-06-permissionsets.yml`.
- **`AIOpsEvaluationSet` and `AIOpsGoldenRegistrar`**: these wait on the CISO's approval (AIOps PRD OQ-45).

Groups are `Retain`. A deleted group cannot come back with the same `GroupId`, and AIOps keys on
the id, so removing a group from code leaves it in place live. Delete it by hand once nothing refers
to it. Permission sets, assignments and memberships keep CloudFormation's default, so removing one
from code revokes it.

`tests/` pins the AI-777 limits: the exact `AIOpsEvaluation` policy; no `states:StartExecution`, S3
or `bedrock:InvokeModel`; no permission set for the Rater group; and the exact members of each group.

## Stacks

AWS CDK in C#, like `netbird/`, under `identity-center/cdk`.

| Stack | Account | Deployed by |
| --- | --- | --- |
| `IdentityCenter` | management `406422318285` | the pipeline, after manual approval |
| `IdentityCenterDeployRoles` | management `406422318285` | a management-account admin, by hand, once, before the pipeline |
| `IdentityCenterPipeline` | shared `791686214595` | a shared-account admin, by hand |

The pipeline (`identity-center`) runs on a push to `main` that touches `identity-center/**`:

1. **Synth**: CodeBuild runs `dotnet test` and `cdk synth IdentityCenter`.
2. **Deploy**: it creates change set `identity-center-pipeline` on stack `IdentityCenter` in the
   management account, waits for **manual approval**, then executes it.

The pipeline does not update itself, and it does not deploy the roles it deploys with. A merge to
`main` can change what Identity Center looks like, never the pipeline or its access.

### What the deploy role hands to the shared account

Anyone who can drive the `identity-center-pipeline` role, or approve an execution, can change
Identity Center. Identity Center can then grant any permission set in any member account. The
execution role refuses account assignments in the management account itself. Nothing else is
fenced off, so pull-request review on this directory and the manual approval are the controls.
Keep `codepipeline:PutApprovalResult` on this pipeline for people who would be trusted to change
Identity Center directly.

## First-time setup

Run these in order. Steps 1 and 2 are pull requests in `autoguru-au/autoguru`.

1. **autoguru, release the MLflow group, part 1.** In `master-06-permissionsets.yml`:
   - set `AIOpsMlflowProductionGroup` to `DeletionPolicy: Retain`;
   - set the description to the one in `IdentityCenterStack.cs`;
   - change the assignment's `PrincipalId` from `!GetAtt AIOpsMlflowProductionGroup.GroupId` to the
     literal `c9ce8408-20d1-7092-0364-a9fef116a23f`.

   Deploy it. The change set lists the assignment as a replacement, because the template text
   changed. Execution compares the resolved value, which is the same id, and leaves it alone.
2. **autoguru, release the MLflow group, part 2.** Remove `AIOpsMlflowProductionGroup` from the
   template and deploy it. The group stays live because the deployed template already says Retain.
3. **Confirm the AIOps application ARN.** In the management account, check that
   `aws sso-admin list-applications --instance-arn arn:aws:sso:::instance/ssoins-8259316042a4e826`
   lists `IdentityCenterInstance.AiopsSamlApplicationArn` (in `cdk/Shared.cs`) as the AIOps
   application. The ARN was inferred from the SAML metadata URL. Correct it in code if it's wrong.
4. **Deploy the deploy roles** (management account, administrator credentials):

   ```bash
   cd identity-center/cdk && cdk deploy IdentityCenterDeployRoles
   ```

   They go first. The pipeline's artifact bucket and key policies name the deploy role, and S3 and
   KMS reject a policy whose principal doesn't exist. The deploy role trusts the shared account
   only when the caller is the `identity-center-pipeline` role (`aws:PrincipalArn`), so it doesn't
   need that role to exist yet.
5. **Import the existing groups** (management account, administrator credentials):

   ```bash
   cd identity-center/cdk && cdk import IdentityCenter --resource-mapping ../import-mapping.json
   ```

   This creates stack `IdentityCenter` holding only the three groups in `import-mapping.json`. The
   other resources aren't in the mapping and are skipped, so nothing is created and nothing about
   the live groups changes. Do this before step 6. Otherwise the pipeline's first run would try to
   create these groups again.
6. **Deploy the pipeline** (shared account):

   ```bash
   cd identity-center/cdk && AWS_PROFILE=shared cdk deploy IdentityCenterPipeline
   ```

   It starts its first run on creation. The change set should only add resources: the three AIOps
   groups, their memberships and application assignments, `AIOpsEvaluation` and its assignment.
   It must not modify or replace an imported group. Review it in the management account, then
   approve the run.

After that, every change is a pull request here. The pipeline applies it after approval.

## Local commands

```bash
cd identity-center
dotnet test tests -c Release      # the limits above
cd cdk && cdk synth               # all three templates into cdk.out/
```
