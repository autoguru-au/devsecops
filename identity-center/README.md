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
| `App-AIOps-Evaluate` | Amir Zahedi, Adam Webb, Rachel White | AIOps | `AIOpsEvaluation` in shared (AI-777) |
| `App-AIOps-Rater` | none yet: the second rater is not named | AIOps | **never**: raters rank and label in the console only |
| `App-AIOps-Unblind` | Amir Zahedi, Rachel White | AIOps | none |
| `App-AILeadership` | Amir Zahedi, Anthony Keller, Luke Atkins, Mike Nadelko | none (GuruShare's AI Team leadership reads it) | none |

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
Identity Center. Identity Center can then grant any permission set in any member account.

The CloudFormation execution role has an explicit action list, not `sso:*`. It is fenced in four
ways:

- it cannot create, delete or provision an account assignment in the management account;
- it cannot create, update or delete the Identity Center instance or its attribute configuration;
- it cannot delete a group (every group here is `Retain`, so it never needs to);
- it can assign groups to the AIOps application only.

The deploy role also cannot create an import change set, so a template cannot take over a
permission set or group it did not create, such as one already provisioned in the management
account.

One gap stays open. Group ids are not known until a group exists, so membership writes cover every
group in the identity store. A template could add someone to any existing group, including one
that already holds access to the management account. Pull-request review on this directory and the
manual approval are the controls for that, and for everything else. Keep
`codepipeline:PutApprovalResult` on this pipeline for people who would be trusted to change Identity
Center directly.

The approval sends no notification. The pipeline waits until someone opens it, so whoever merges
should tell an approver. Nobody has been named to receive one yet; add an SNS topic to the approval
action when someone is.

## First-time setup

Run these in order. Steps 1 and 2 are pull requests in `autoguru-au/autoguru`.

1. **autoguru, release the MLflow group, part 1.** In `master-06-permissionsets.yml`:
   - set `AIOpsMlflowProductionGroup` to `DeletionPolicy: Retain`;
   - set the description to the one in `IdentityCenterStack.cs`;
   - change the assignment's `PrincipalId` from `!GetAtt AIOpsMlflowProductionGroup.GroupId` to the
     literal `c9ce8408-20d1-7092-0364-a9fef116a23f`.

   Before executing, open `AIOpsMlflowProductionAssignment` in the change set. Its `Replacement`
   must be `Conditional`, which means CloudFormation decides at execution by comparing resolved
   values. The resolved id is unchanged, so it should leave the assignment alone. If it says
   `True`, stop.
   CloudFormation would create the assignment again and then delete the old physical one, which is
   the same grant, and that removes Production MLflow access. Delete the change set and add
   `DeletionPolicy: Retain` and `UpdateReplacePolicy: Retain` to the assignment first.
2. **autoguru, release the MLflow group, part 2.** Remove `AIOpsMlflowProductionGroup` from the
   template and deploy it. The group stays live because the deployed template already says Retain.
3. **Confirm the AIOps application ARN.** In the management account, check that
   `aws sso-admin list-applications --instance-arn arn:aws:sso:::instance/ssoins-8259316042a4e826`
   lists `IdentityCenterInstance.AiopsSamlApplicationArn` (in `cdk/Shared.cs`) as the AIOps
   application. The ARN was inferred from the SAML metadata URL. Correct it in code if it's wrong.
4. **Import the existing groups** (management account, administrator credentials):

   ```bash
   cd identity-center/cdk && cdk import IdentityCenter --resource-mapping ../import-mapping.json
   ```

   This creates stack `IdentityCenter` holding only the three groups in `import-mapping.json`. The
   other resources aren't in the mapping and are skipped, so nothing is created and nothing about
   the live groups changes. Do this before step 6. Otherwise the pipeline's first run would try to
   create these groups again. It doesn't need the deploy roles, and it goes before them so that
   steps 5 and 6 can run back to back.
5. **Deploy the deploy roles** (management account, administrator credentials):

   ```bash
   cd identity-center/cdk && cdk deploy IdentityCenterDeployRoles
   ```

   They go before the pipeline. The pipeline's artifact bucket and key policies name the deploy
   role, and S3 and KMS reject a policy whose principal doesn't exist. The deploy role trusts the
   shared account only when the caller is the `identity-center-pipeline` role (`aws:PrincipalArn`),
   so it doesn't need that role to exist yet.

   That is also why step 6 follows straight away. Until the pipeline stack creates
   `identity-center-pipeline`, the name is unclaimed, and anyone in the shared account who can
   create an IAM role could create one by that name and assume the deploy role. Check first that
   `aws iam get-role --role-name identity-center-pipeline` in the shared account says
   `NoSuchEntity`. If step 6 fails, delete the deploy roles stack until it can be retried.
6. **Deploy the pipeline** (shared account), immediately after step 5:

   ```bash
   cd identity-center/cdk && AWS_PROFILE=shared cdk deploy IdentityCenterPipeline
   ```

   It starts its first run on creation. The change set should only add resources: the three AIOps
   groups and `App-AILeadership`, their memberships, the AIOps application assignments,
   `AIOpsEvaluation` and its assignment.
   It must not modify or replace an imported group. Review it in the management account, then
   approve the run.

After that, every change is a pull request here. The pipeline applies it after approval.

## Local commands

```bash
cd identity-center
dotnet test tests -c Release      # the limits above
cd cdk && cdk synth               # all three templates into cdk.out/
```
