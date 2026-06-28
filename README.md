# Pulumi Aspire

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Aspire](https://img.shields.io/nuget/v/Aspire.Hosting?label=Aspire&color=512BD4)](https://aspire.dev/)
[![Pulumi](https://img.shields.io/badge/Pulumi-3.x-blueviolet)](https://www.pulumi.com/)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Deploy Aspire applications to cloud infrastructure using Pulumi's Infrastructure as Code.

## 🎯 Overview

**Pulumi Aspire** is an experimental Aspire hosting integration that lets an AppHost publish or deploy selected compute resources to Azure Container Apps through Pulumi.

It currently focuses on:

- ☁️ **Azure Container Apps deployment** – this is the provider implemented today
- 🔧 **Preserving the local Aspire experience** – `aspire run` stays available for development
- 🧪 **Pulumi-backed preview and apply workflows** – `aspire publish` and `aspire deploy` are driven by Pulumi Automation API
- 🧩 **Provider-specific extensions** – the core package can be extended with additional cloud providers over time

## 📦 Packages

| Package | Description | Status |
|---------|-------------|--------|
| `EmmittJ.Aspire.Hosting.Pulumi` | Core abstractions, lifecycle hooks, and Pulumi Automation API integration | Not yet published |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure` | Azure Container Apps provider for compute and resource translation | Not yet published |

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

Pulumi Aspire follows Aspire's deployment-target and publisher model. The core package provides the provider-agnostic lifecycle hooks and Pulumi integration points, while the Azure package translates Aspire compute resources into Azure Container Apps resources.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the implementation details and extension points.

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

## 🗺️ Potential follow-ups

The current implementation focuses on Azure Container Apps. Possible future work includes:

- 🔨 Additional resource translations for data services and other Aspire compute patterns
- 📋 Additional cloud providers beyond Azure

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
