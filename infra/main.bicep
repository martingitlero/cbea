// ---------------------------------------------------------------------------
// CurlyBlue / GameBackend - daily reward service infrastructure
//
// Deploys the minimum production-shaped footprint for the ClaimDailyRewardV1
// Azure Function (.NET 8 isolated worker, Functions v4) on a Linux Consumption
// plan, with a system-assigned managed identity and workspace-based
// Application Insights.
//
// Deploy:
//   az deployment group create -g <rg> -f infra/main.bicep \
//     -p environment=dev playFabTitleId=<titleId> \
//        playFabSecretUri=https://<vault>.vault.azure.net/secrets/playfab-dev-secret-key
//
// NOTE: this template contains NO secret values. The PlayFab developer secret
// is referenced by Key Vault URI only (see playFabSecretUri).
// ---------------------------------------------------------------------------

targetScope = 'resourceGroup'

@description('Deployment environment. Drives resource naming, SKUs and retention.')
@allowed([
  'dev'
  'staging'
  'prod'
])
param environment string

@description('Azure region. Defaults to the resource group region so the whole stack stays colocated.')
param location string = resourceGroup().location

@description('Short application name used as the resource name prefix.')
@minLength(3)
@maxLength(11)
param appName string = 'curlyblue'

@description('PlayFab Title ID. This is a public identifier, not a secret - it ships in the Unity client.')
param playFabTitleId string = ''

@description('''
Key Vault secret identifier for the PlayFab developer secret key, e.g.
https://<vault>.vault.azure.net/secrets/playfab-dev-secret-key
Leave empty to omit the setting entirely (useful for a bare dev deploy).
The URI itself is not sensitive - it names a secret, it does not contain one.
''')
param playFabSecretUri string = ''

@description('Reward amount granted per UTC day. Surfaced as config so it can be tuned without a redeploy of code.')
param dailyRewardAmount int = 50

@description('Virtual currency / inventory item id granted by the daily reward.')
param dailyRewardCurrencyId string = 'currency.soft'

@description('Tags applied to every resource.')
param tags object = {
  application: appName
  environment: environment
  workload: 'daily-reward'
  managedBy: 'bicep'
}

// ---------------------------------------------------------------------------
// Naming + per-environment configuration
// ---------------------------------------------------------------------------

// uniqueString(resourceGroup().id) keeps names globally unique and deterministic:
// re-running the template targets the same resources instead of creating new ones.
var suffix = uniqueString(resourceGroup().id)
var namePrefix = '${appName}-${environment}'

// Storage account names: 3-24 chars, lowercase alphanumeric only. Components are
// truncated individually (max 2 + 4 + 4 + 13 = 23) so the result always fits.
var storageAccountName = toLower('st${take(replace(appName, '-', ''), 4)}${take(environment, 4)}${suffix}')
var functionAppName = '${namePrefix}-func-${take(suffix, 6)}'
var hostingPlanName = '${namePrefix}-plan'
var appInsightsName = '${namePrefix}-appi'
var logAnalyticsName = '${namePrefix}-log'

// Per-environment choices kept in one map so the delta between environments is
// reviewable at a glance rather than scattered through the resource bodies.
var envConfig = {
  dev: {
    storageSku: 'Standard_LRS'
    logRetentionDays: 30
    // Cap Consumption fan-out in non-prod: a runaway loop should cost pennies, not a budget.
    functionAppScaleLimit: 5
  }
  staging: {
    storageSku: 'Standard_LRS'
    logRetentionDays: 30
    functionAppScaleLimit: 20
  }
  prod: {
    // GRS in prod only: Functions' own control data lives here, and paying for
    // geo-redundancy in dev/staging buys nothing.
    storageSku: 'Standard_GRS'
    logRetentionDays: 90
    functionAppScaleLimit: 200
  }
}
var cfg = envConfig[environment]

// ---------------------------------------------------------------------------
// Storage - required by the Functions host for triggers, leases and state
// ---------------------------------------------------------------------------

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: cfg.storageSku
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    // No blob in this account should ever be anonymously readable.
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true // required by AzureWebJobsStorage on Consumption
    defaultToOAuthAuthentication: false
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    encryption: {
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: false
      services: {
        blob: {
          enabled: true
        }
        file: {
          enabled: true
        }
        queue: {
          enabled: true
        }
        table: {
          enabled: true
        }
      }
    }
  }
}

// EndpointSuffix is intentionally omitted so the template stays cloud-agnostic
// and free of hardcoded *.core.windows.net URLs; the SDK defaults correctly.
// listKeys() is evaluated by ARM at deploy time - no key is ever stored in source.
var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value}'

// ---------------------------------------------------------------------------
// Observability - workspace-based Application Insights
// ---------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: cfg.logRetentionDays
    features: {
      searchVersion: 1
    }
  }
}

// Classic (non-workspace) App Insights is retired; WorkspaceResourceId is required.
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
    // The Functions host exports via APPLICATIONINSIGHTS_CONNECTION_STRING below,
    // not via a legacy instrumentation key.
    DisableLocalAuth: false
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ---------------------------------------------------------------------------
// Compute - Y1 Dynamic (Consumption) Linux plan
// ---------------------------------------------------------------------------

resource hostingPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: hostingPlanName
  location: location
  tags: tags
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'functionapp'
  properties: {
    // reserved: true == Linux. Consumption scales to zero, which suits a
    // once-per-player-per-day endpoint with a spiky login-hour profile.
    reserved: true
  }
}

// App settings are assembled as an array so the Key Vault reference can be
// appended conditionally without duplicating the whole block.
var baseAppSettings = [
  {
    name: 'AzureWebJobsStorage'
    value: storageConnectionString
  }
  {
    name: 'FUNCTIONS_EXTENSION_VERSION'
    value: '~4'
  }
  {
    name: 'FUNCTIONS_WORKER_RUNTIME'
    value: 'dotnet-isolated'
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsights.properties.ConnectionString
  }
  {
    name: 'ENVIRONMENT'
    value: environment
  }
  {
    name: 'PLAYFAB_TITLE_ID'
    value: playFabTitleId
  }
  {
    name: 'DAILY_REWARD_AMOUNT'
    value: string(dailyRewardAmount)
  }
  {
    name: 'DAILY_REWARD_CURRENCY_ID'
    value: dailyRewardCurrencyId
  }
]

// PLACEHOLDER Key Vault reference. The literal below is an ARM Key Vault
// reference expression, not a secret: at runtime App Service resolves it using
// the Function App's managed identity, which must hold `get` on the vault's
// secrets (grant via RBAC role "Key Vault Secrets User" or an access policy).
var keyVaultAppSettings = empty(playFabSecretUri) ? [] : [
  {
    name: 'PLAYFAB_DEV_SECRET_KEY'
    value: '@Microsoft.KeyVault(SecretUri=${playFabSecretUri})'
  }
]

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    // System-assigned identity is where "secrets" actually live: nothing is
    // stored here, the app proves who it is to Key Vault / Azure resources.
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      // Windows equivalent would be netFrameworkVersion: 'v8.0'; on Linux the
      // stack is selected with linuxFxVersion.
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      functionAppScaleLimit: cfg.functionAppScaleLimit
      // Only PlayFab calls this endpoint; the browser CORS list stays empty.
      cors: {
        allowedOrigins: []
        supportCredentials: false
      }
      appSettings: concat(baseAppSettings, keyVaultAppSettings)
    }
  }
}

// ---------------------------------------------------------------------------
// Diagnostics - ship Function App platform logs to the same workspace as traces
// ---------------------------------------------------------------------------

resource functionAppDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: functionApp
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        category: 'FunctionAppLogs'
        enabled: true
        retentionPolicy: {
          enabled: false
          days: 0
        }
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
        retentionPolicy: {
          enabled: false
          days: 0
        }
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Outputs (identifiers only - never secrets)
// ---------------------------------------------------------------------------

@description('Name of the deployed Function App, for use by a future CD deploy step.')
output functionAppName string = functionApp.name

@description('Full URL of the ClaimDailyRewardV1 endpoint to register in PlayFab.')
output claimDailyRewardUrl string = 'https://${functionApp.properties.defaultHostName}/api/ClaimDailyRewardV1'

@description('System-assigned managed identity principal id. Grant this Key Vault secret read access.')
output functionAppPrincipalId string = functionApp.identity.principalId
