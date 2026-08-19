{
  "ConnectionStrings": {
    "Default": "Server=localhost,11433;Database=fctelecom;User Id=sa;Password=LocalDev!Passw0rd;Encrypt=True;TrustServerCertificate=True;Connection Timeout=30"
  },

  "Documents": {
    "//": "Azurite. Guarded in DI so this connection-string path cannot be taken in Azure.",
    "ConnectionString": "UseDevelopmentStorage=true",
    "BlobServiceUri": "",
    "ContainerName": "documents"
  },

  "Security": {
    "FieldEncryption": {
      "//": "Development-only keys, committed deliberately so a fresh clone runs. They encrypt nothing but seeded documentation-range addresses. NEVER reuse these anywhere real.",
      "EncryptionKeyBase64": "ZmMtdGVsZWNvbS1sb2NhbC1kZXYtZW5jLWtleS0wMSE=",
      "SearchHashKeyBase64": "ZmMtdGVsZWNvbS1sb2NhbC1kZXYtaGFzaC1rZXktMSE="
    },
    "EnableDevAuthBypass": true
  },

  "Monitoring": {
    "Provider": "Simulated"
  },

  "SeedDemoData": true,

  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.EntityFrameworkCore.Database.Command": "Information"
      }
    }
  }
}
