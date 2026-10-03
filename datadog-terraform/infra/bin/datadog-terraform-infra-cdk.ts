#!/usr/bin/env node
import 'source-map-support/register';
import * as cdk from 'aws-cdk-lib';
import { DatadogTerraformInfraStack } from '../lib/datadog-terraform-infra-stack';

const app = new cdk.App();

const SHARED_ACCOUNT_ID = '791686214595';
const REGION = 'ap-southeast-2';

new DatadogTerraformInfraStack(app, 'DatadogTerraformInfraStack', {
  env: { account: SHARED_ACCOUNT_ID, region: REGION },
  description: 'AWS prerequisites for Datadog Terraform monitor management (S3, DynamoDB, KMS, IAM OIDC)',
});
