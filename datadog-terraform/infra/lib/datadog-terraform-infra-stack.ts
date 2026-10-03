import * as cdk from 'aws-cdk-lib';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import type { Construct } from 'constructs';

const GITHUB_REPO = 'autoguru-au/mfe';
const REGION = 'ap-southeast-2';

export class DatadogTerraformInfraStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    // ──────────────────────────────────────────
    // KMS Key — encryption for Terraform state
    // ──────────────────────────────────────────
    const stateKey = new kms.Key(this, 'TerraformStateKey', {
      alias: 'terraform-state-key',
      description: 'Encryption key for Datadog Terraform state bucket',
      enableKeyRotation: true,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    // Allow AWS Backup service to use the key so it can back up the encrypted state bucket
    // (the bucket is selected by sydneyplans org policy via the backup=true tag).
    stateKey.addToResourcePolicy(
      new iam.PolicyStatement({
        sid: 'AllowAWSBackupServiceToUseKey',
        effect: iam.Effect.ALLOW,
        principals: [new iam.ServicePrincipal('backup.amazonaws.com')],
        actions: ['kms:Decrypt', 'kms:GenerateDataKey', 'kms:DescribeKey', 'kms:CreateGrant'],
        resources: ['*'],
      })
    );

    // ──────────────────────────────────────────
    // S3 Bucket — Terraform state backend
    // ──────────────────────────────────────────
    const stateBucket = new s3.Bucket(this, 'TerraformStateBucket', {
      bucketName: 'autoguru-datadog-terraform-state',
      versioned: true,
      encryption: s3.BucketEncryption.KMS,
      encryptionKey: stateKey,
      enforceSSL: true,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });
    cdk.Tags.of(stateBucket).add('backup', 'true');

    // ──────────────────────────────────────────
    // DynamoDB Table — Terraform state locking
    // ──────────────────────────────────────────
    const lockTable = new dynamodb.Table(this, 'TerraformLockTable', {
      tableName: 'autoguru-datadog-terraform-locks',
      partitionKey: { name: 'LockID', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      pointInTimeRecovery: true,
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });
    cdk.Tags.of(lockTable).add('backup', 'true');

    // ──────────────────────────────────────────
    // Secrets Manager — shells for DD API/app keys
    // Actual values populated manually after deploy.
    // ──────────────────────────────────────────
    new secretsmanager.Secret(this, 'DatadogApiKey', {
      secretName: 'datadog/mfe-monitors/api-key',
      description: 'Datadog API key for MFE monitor Terraform management',
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    new secretsmanager.Secret(this, 'DatadogAppKey', {
      secretName: 'datadog/mfe-monitors/app-key',
      description: 'Datadog application key for MFE monitor Terraform management',
      removalPolicy: cdk.RemovalPolicy.RETAIN,
    });

    // ──────────────────────────────────────────
    // GitHub OIDC Provider — already exists in this account
    // ──────────────────────────────────────────
    const oidcProvider = iam.OpenIdConnectProvider.fromOpenIdConnectProviderArn(
      this,
      'GitHubOidcProvider',
      `arn:aws:iam::${this.account}:oidc-provider/token.actions.githubusercontent.com`
    );

    // ──────────────────────────────────────────
    // Shared policy statements
    // ──────────────────────────────────────────
    const secretsReadPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['secretsmanager:GetSecretValue'],
      resources: [`arn:aws:secretsmanager:${REGION}:${this.account}:secret:datadog/mfe-monitors/*`],
    });

    const stateReadPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['s3:GetObject', 's3:ListBucket'],
      resources: [stateBucket.bucketArn, `${stateBucket.bucketArn}/*`],
    });

    const stateWritePolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['s3:PutObject'],
      resources: [`${stateBucket.bucketArn}/*`],
    });

    const lockReadPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['dynamodb:GetItem'],
      resources: [lockTable.tableArn],
    });

    const lockWritePolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['dynamodb:PutItem', 'dynamodb:DeleteItem'],
      resources: [lockTable.tableArn],
    });

    const kmsPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['kms:Decrypt', 'kms:GenerateDataKey'],
      resources: [stateKey.keyArn],
    });

    // ──────────────────────────────────────────
    // IAM Role (write) — main branch only
    // ──────────────────────────────────────────
    const writeRole = new iam.Role(this, 'TerraformWriteRole', {
      roleName: 'github-actions-terraform-datadog',
      assumedBy: new iam.FederatedPrincipal(
        oidcProvider.openIdConnectProviderArn,
        {
          StringEquals: {
            'token.actions.githubusercontent.com:aud': 'sts.amazonaws.com',
          },
          StringLike: {
            'token.actions.githubusercontent.com:sub': `repo:${GITHUB_REPO}:ref:refs/heads/main`,
          },
        },
        'sts:AssumeRoleWithWebIdentity'
      ),
    });

    writeRole.addToPolicy(secretsReadPolicy);
    writeRole.addToPolicy(stateReadPolicy);
    writeRole.addToPolicy(stateWritePolicy);
    writeRole.addToPolicy(lockReadPolicy);
    writeRole.addToPolicy(lockWritePolicy);
    writeRole.addToPolicy(kmsPolicy);

    // ──────────────────────────────────────────
    // IAM Role (read-only) — pull requests
    // ──────────────────────────────────────────
    const readOnlyRole = new iam.Role(this, 'TerraformReadOnlyRole', {
      roleName: 'github-actions-terraform-datadog-readonly',
      assumedBy: new iam.FederatedPrincipal(
        oidcProvider.openIdConnectProviderArn,
        {
          StringEquals: {
            'token.actions.githubusercontent.com:aud': 'sts.amazonaws.com',
          },
          StringLike: {
            'token.actions.githubusercontent.com:sub': `repo:${GITHUB_REPO}:pull_request`,
          },
        },
        'sts:AssumeRoleWithWebIdentity'
      ),
    });

    readOnlyRole.addToPolicy(secretsReadPolicy);
    readOnlyRole.addToPolicy(stateReadPolicy);
    readOnlyRole.addToPolicy(lockReadPolicy);
    readOnlyRole.addToPolicy(lockWritePolicy);
    readOnlyRole.addToPolicy(kmsPolicy);

    // ──────────────────────────────────────────
    // CloudFormation Exports
    // ──────────────────────────────────────────
    new cdk.CfnOutput(this, 'TerraformStateBucketName', {
      value: stateBucket.bucketName,
      exportName: 'DatadogTerraformStateBucketName',
    });

    new cdk.CfnOutput(this, 'TerraformStateBucketArn', {
      value: stateBucket.bucketArn,
      exportName: 'DatadogTerraformStateBucketArn',
    });

    new cdk.CfnOutput(this, 'TerraformLockTableName', {
      value: lockTable.tableName,
      exportName: 'DatadogTerraformLockTableName',
    });

    new cdk.CfnOutput(this, 'TerraformLockTableArn', {
      value: lockTable.tableArn,
      exportName: 'DatadogTerraformLockTableArn',
    });

    new cdk.CfnOutput(this, 'TerraformStateKeyArn', {
      value: stateKey.keyArn,
      exportName: 'DatadogTerraformStateKeyArn',
    });

    new cdk.CfnOutput(this, 'TerraformWriteRoleArn', {
      value: writeRole.roleArn,
      exportName: 'DatadogTerraformWriteRoleArn',
    });

    new cdk.CfnOutput(this, 'TerraformReadOnlyRoleArn', {
      value: readOnlyRole.roleArn,
      exportName: 'DatadogTerraformReadOnlyRoleArn',
    });
  }
}
