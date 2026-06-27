# Vite + YARP Static Files Sample

YARP reverse proxy serving a Vite frontend, deployed to Azure Container Apps using Pulumi.

## Architecture

**Run Mode (Local Development):**
```mermaid
flowchart LR
    Browser --> YARP
    YARP --> Vite[Vite Dev Server<br/>HMR enabled]
```

**Publish Mode (Cloud Deployment):**
```mermaid
flowchart LR
    Browser --> ACA[Azure Container Apps<br/>YARP serving static files]
```

## What This Demonstrates

- **AddViteApp**: Vite-based frontend application
- **AddYarp**: Reverse proxy with dual-mode routing
- **AddPulumiAzureEnvironment**: Azure Container Apps deployment via Pulumi
- **PublishWithStaticFiles**: Automatic static file serving in production

## Prerequisites

1. **Azure CLI** - Logged in with `az login`
2. **Pulumi CLI** - Logged in with `pulumi login`

## Running Locally

```bash
aspire run
```

## Deploying to Azure

```bash
aspire deploy
```

## Commands Summary

| Command | Description |
|---------|-------------|
| `aspire run` | Run locally with Vite HMR |
| `aspire deploy` | Deploy to Azure via Pulumi Automation API |
| `aspire do pulumi-preview-dev` | Preview changes |
| `aspire do pulumi-destroy-dev` | Tear down resources |

## Key Aspire Patterns

**Dual-Mode YARP** - Run mode proxies to Vite, publish mode serves static files:
```csharp
var frontend = builder.AddViteApp("frontend", "./frontend");

builder.AddYarp("app")
    .WithConfiguration(c =>
    {
        if (builder.ExecutionContext.IsRunMode)
            c.AddRoute("{**catch-all}", frontend); // Run: proxy to Vite HMR
    })
    .PublishWithStaticFiles(frontend); // Publish: serve static files
```

**Pulumi Azure Environment** - Deploys to Azure Container Apps:
```csharp
builder.AddPulumiAzureEnvironment("dev", "vite-yarp-static")
    .WithLocation("eastus");
```

## Sample Outputs

After deployment, you'll see outputs like:
```
app-fqdn                    : "app--xxxxx.eastus.azurecontainerapps.io"
containerRegistryLoginServer: "acrxxxxx.azurecr.io"
managedEnvironmentName      : "vite-yarp-static-dev-env"
resourceGroupName           : "vite-yarp-static-dev-rg"
```
