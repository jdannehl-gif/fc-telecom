{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "Domain": "REPLACE.onmicrosoft.com",
    "TenantId": "REPLACE-tenant-guid",
    "ClientId": "REPLACE-app-registration-client-id",
    "CallbackPath": "/signin-oidc",
    "SignedOutCallbackPath": "/signout-callback-oidc"
  },

  "ConnectionStrings": {
    "//": "In Azure this carries no credential: Authentication=Active Directory Default resolves the managed identity.",
    "Default": "Server=tcp:REPLACE.database.windows.net,1433;Database=fctelecom;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30"
  },

  "Documents": {
    "BlobServiceUri": "https://REPLACE.blob.core.windows.net",
    "ContainerName": "documents",
    "//": "Download links are minted per request and expire quickly. There is no permanent URL anywhere in this system.",
    "SasLifetimeMinutes": 5
  },

  "Security": {
    "FieldEncryption": {
      "//": "Key Vault references in Azure, user secrets locally. Never a literal value in this file. Generate with: openssl rand -base64 32",
      "EncryptionKeyBase64": "",
      "SearchHashKeyBase64": ""
    },
    "//": "Honoured only in DEBUG builds, and asserted unreachable in Release by a test.",
    "EnableDevAuthBypass": false
  },

  "Monitoring": {
    "Provider": "Simulated",
    "RawRetentionDays": 45,
    "//": "Below this coverage fraction, availability rollups are flagged LowConfidence and the UI shows coverage alongside the figure.",
    "MinimumCoverageForConfidence": 0.90
  },

  "Contracts": {
    "AlertThresholdDays": [ 180, 120, 90, 60, 30 ]
  },

  "Notifications": {
    "//": "Master switch, off by default. A demo import that sends four hundred emails on day one is how a rollout becomes an incident.",
    "Enabled": false,
    "MaxDeliveryAttempts": 8
  },

  "Integrations": {
    "ItGlue": {
      "BaseUrl": "https://api.itglue.com",
      "//": "IT Glue publishes 3000 requests per rolling 5-minute window. We sit at 80% of that ceiling.",
      "MaxRequestsPer5Min": 2400,
      "ApiKeySecretName": "itglue-api-token"
    }
  },

  "RateLimits": {
    "PerUserPerMinute": 600,
    "AgentPerMinute": 120,
    "ExpensivePerMinute": 6
  },

  "SeedDemoData": false,

  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
      }
    }
  },

  "AllowedHosts": "*"
}
