# Pulumi Aspire

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Aspire](https://img.shields.io/nuget/v/Aspire.Hosting?label=Aspire&color=512BD4)](https://aspire.dev/)
[![Pulumi](https://img.shields.io/badge/Pulumi-3.x-blueviolet)](https://www.pulumi.com/)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Deploy Aspire applications to cloud infrastructure using Pulumi's Infrastructure as Code.

## 🎯 Overview

**Pulumi Aspire** bridges Aspire's distributed application model with Pulumi's Infrastructure as Code capabilities. It enables you to:

- ☁️ **Deploy to Azure Container Apps** – AWS, GCP, and Kubernetes support planned
- 🔧 **Keep Aspire's DX** – `aspire run` for local dev, `aspire deploy` for cloud deployment
- 🏗️ **Infrastructure as Code** – Full Pulumi stack management, state, and drift detection
- 🔌 **Extensible** – Add cloud providers via NuGet packages, not code changes

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Add Pulumi Azure environment
var azure = builder.AddPulumiAzureEnvironment("dev", "my-app")
    .WithLocation("eastus");

// Your Aspire resources - automatically deployed to Azure Container Apps
var cache = builder.AddRedis("cache");
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(cache);

builder.Build().Run();
```

## 📦 Packages

| Package | Description | NuGet |
|---------|-------------|-------|
| `EmmittJ.Aspire.Hosting.Pulumi` | Core abstractions and Automation API integration | Coming Soon |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure` | Azure Container Apps deployment | Coming Soon |

## 🚀 Getting Started

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (or later)
- [Pulumi CLI](https://www.pulumi.com/docs/install/)
- [Aspire CLI](https://aspire.dev/get-started/install-cli/)
- Cloud provider CLI (e.g., [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli))

### Installation

Add the Pulumi Azure package to your AppHost project:

```bash
dotnet add package EmmittJ.Aspire.Hosting.Pulumi.Azure
```

### Quick Start

1. **Configure your AppHost:**

```csharp
using EmmittJ.Aspire.Hosting.Pulumi.Azure;

var builder = DistributedApplication.CreateBuilder(args);

// Add Pulumi Azure environment
// "dev" = environment name (becomes part of stack name)
// "my-app" = project name (groups stacks in Pulumi console)
var azure = builder.AddPulumiAzureEnvironment("dev", "my-app")
    .WithLocation("eastus");

// Add your resources
var frontend = builder.AddViteApp("frontend", "./frontend");

builder.AddYarp("app")
    .WithExternalHttpEndpoints()
    .PublishWithStaticFiles(frontend);

builder.Build().Run();
```

2. **Run locally** (unchanged Aspire experience):

```bash
aspire run
```

3. **Deploy to Azure:**

```bash
aspire deploy
```

## 🏗️ Architecture

Pulumi Aspire consists of two main components that work together to provide flexible deployment options:

### SDK Packages (.NET)

NuGet packages that integrate with Aspire's model using Pulumi's Automation API:

- **Core Package** – Base classes, pipeline steps, and dual-mode runner infrastructure
- **Provider Packages** – Cloud-specific resource translation (Azure, AWS, etc.)

### Language Host (Go)

A minimal Pulumi language plugin (`pulumi-language-aspire`) that enables `pulumi up` to work with Aspire projects.

### Execution Modes

Pulumi Aspire supports two execution modes:

| Mode | Command | Description |
|------|---------|-------------|
| **Automation API** | `aspire deploy` | Direct deployment using Pulumi's Automation API. Best for CI/CD and programmatic control. |
| **Engine Mode** | `pulumi up` | Uses Pulumi CLI with the custom language host. Best for interactive development and Pulumi ecosystem integration. |

```
# Automation API Mode
aspire deploy → SDK Packages → Automation API → Cloud Resources

# Engine Mode  
pulumi up → pulumi-language-aspire → aspire deploy → SDK Packages → Cloud Resources
```

### Design Principles

1. **Translation in C#, Not Go** – All resource translation happens in SDK packages
2. **Dual-Mode Support** – Works with both `aspire deploy` and `pulumi up`
3. **Aspire-Native Patterns** – Follows Aspire's pipeline steps, event subscribers, and compute environments

For detailed architecture, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## 📋 Commands

### Primary Commands

| Command | Mode | Description |
|---------|------|-------------|
| `aspire run` | Local | Run locally with Aspire's development experience |
| `aspire deploy` | Automation API | Deploy to cloud using Pulumi Automation API |
| `pulumi up` | Engine | Deploy using Pulumi CLI with language host |
| `pulumi preview` | Engine | Preview changes without deploying |
| `pulumi destroy` | Engine | Destroy all cloud resources |

### Aspire Pipeline Steps

| Command | Description |
|---------|-------------|
| `aspire do pulumi-preview-{env}` | Preview changes for a specific environment |
| `aspire do pulumi-destroy-{env}` | Destroy resources for a specific environment |

### Pulumi Stack Management

```bash
pulumi stack ls              # List stacks
pulumi stack select prod     # Switch environments
pulumi config set key value  # Configure stack
pulumi stack output          # View outputs (URLs, resource names)
```

## ⚙️ Configuration

### Azure Environment Options

```csharp
builder.AddPulumiAzureEnvironment("dev", "my-app")
    .WithLocation("eastus")                    // Azure region
    .WithResourceGroup("my-existing-rg")       // Use existing resource group
    .WithManagedEnvironment("my-existing-env"); // Use existing Container Apps environment
```

### Customizing Container Apps

```csharp
builder.AddContainer("api", "myimage")
    .PublishAsPulumiContainerApp((containerApp, context) =>
    {
        // Customize the Azure Container App resource
    });
```

### Multiple Environments

```csharp
// Each environment creates a separate Pulumi stack
builder.AddPulumiAzureEnvironment("dev", "my-app");    // Stack: my-app/my-app-dev
builder.AddPulumiAzureEnvironment("staging", "my-app"); // Stack: my-app/my-app-staging
builder.AddPulumiAzureEnvironment("prod", "my-app");    // Stack: my-app/my-app-prod
```

## 🗺️ Roadmap

### ✅ Phase 1: Foundation (Complete)

- [x] Core package with Automation API
- [x] Azure Container Apps translation
- [x] Pipeline steps (deploy, preview, destroy)
- [x] Language host (Go)
- [x] Dual-mode execution (Automation API + Engine mode)
- [x] DI-injectable runner infrastructure

### 🔨 Phase 2: Databases

- [ ] Redis → Azure Redis Cache
- [ ] PostgreSQL → Azure PostgreSQL
- [ ] SQL Server → Azure SQL
- [ ] Connection string propagation

### 📋 Phase 3: AWS Provider

- [ ] `EmmittJ.Aspire.Hosting.Pulumi.Aws`
- [ ] ECS/Fargate for compute
- [ ] ElastiCache, RDS for databases

### 📋 Phase 4: Kubernetes Provider

- [ ] `EmmittJ.Aspire.Hosting.Pulumi.Kubernetes`
- [ ] Deployments, Services
- [ ] Helm Charts for databases

## 🧪 Samples

Check out the [samples](samples/) directory:

- **[vite-yarp-static](samples/vite-yarp-static/)** – Vite frontend with YARP reverse proxy, deployed to Azure Container Apps

## 🤝 Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🔗 Resources

- [Aspire Documentation](https://aspire.dev/)
- [Pulumi Documentation](https://www.pulumi.com/docs/)
- [Pulumi Automation API](https://www.pulumi.com/docs/guides/automation-api/)
- [Architecture Documentation](docs/ARCHITECTURE.md)
