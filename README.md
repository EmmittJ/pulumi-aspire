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

Pulumi Aspire implements Aspire's deployment-target pattern (the same model as the built-in Azure Container Apps, Kubernetes, and Docker Compose integrations) and uses Pulumi's Automation API for stack operations:

- **Core package** – the compute-environment base class, per-resource deployment targets, value/secret resolver, container-registry base, and the Automation API runner
- **Provider packages** – cloud-specific resource translation (Azure today)

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for details.

## 📋 Commands

| Command | Description |
|---------|-------------|
| `aspire run` | Run locally; the Pulumi environment and registry stay out of the dashboard |
| `aspire publish` | Write a reviewable `pulumi preview` artifact to the output directory |
| `aspire deploy` | Provision the registry, push images, and run `pulumi up` |
| `aspire destroy` | Run `pulumi destroy` for the environment and its registry |

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

- ✅ Azure Container Apps deployment (compute, endpoints, container registry, managed identity)
- 🔨 Database resources (Redis, PostgreSQL, SQL Server) → Azure-managed equivalents
- 📋 Additional providers (AWS, Kubernetes)

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
