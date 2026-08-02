# Pulumi Aspire

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Aspire](https://img.shields.io/nuget/v/Aspire.Hosting?label=Aspire&color=512BD4)](https://aspire.dev/)
[![Pulumi](https://img.shields.io/badge/Pulumi-3.x-blueviolet)](https://www.pulumi.com/)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Deploy Aspire applications to cloud infrastructure using Pulumi's Infrastructure as Code.

## 🎯 Overview

**Pulumi Aspire** is an experimental Aspire hosting integration that hands the deployment of Aspire's native compute environments (Azure Container Apps today) to Pulumi. You keep Aspire's own environment modeling — `AddAzureContainerAppEnvironment` and friends — and decorate the environment with `PublishAsPulumi` to have Pulumi preview, apply, and destroy the infrastructure instead of Aspire's built-in provisioner.

It currently focuses on:

- ☁️ **Azure Container Apps deployment** – this is the provider translation implemented today
- 🔧 **Preserving the local Aspire experience** – `aspire run` stays available for development
- 🧪 **Pulumi-backed preview and apply workflows** – `aspire publish` and `aspire deploy` are driven by Pulumi Automation API
- 🧩 **Provider-agnostic core** – the adoption seam is provider-neutral and can be extended to other native environments over time

## 📦 Packages

| Package | Description | Status |
|---------|-------------|--------|
| `EmmittJ.Aspire.Hosting.Pulumi` | Core adoption seam: `PublishAsPulumi`, pipeline lifecycle steps, and Pulumi Automation API integration | Not yet published |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure` | Bicep → Pulumi azure-native translation for adopted Azure environments | Not yet published |

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
var builder = DistributedApplication.CreateBuilder(args);

// Model the environment with Aspire's own integration, then hand deployment to Pulumi.
// The resource name becomes the Pulumi project ("my-app-pulumi"); the stack is the
// deployment environment (dev/staging/prod), selected at deploy time with
// `aspire deploy --environment <name>`.
var azureOptions = new AzureAdoptionOptions { Location = "eastus" };
builder.AddAzureContainerAppEnvironment("my-app")
    .PublishAsPulumi(
        PulumiStepSuppressionSelector.AzureContainerApps,
        context => context.TranslateAzureEnvironmentAsync(azureOptions),
        registryPhase: PulumiAzureAdoptionExtensions.CreateAzureRegistryPhase(azureOptions));

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

Pulumi Aspire uses an **adopt-and-traverse** model: Aspire's native compute environment keeps modeling the application (deployment targets, container registries, Bicep templates), while `PublishAsPulumi` suppresses the environment's native execution steps and splices Pulumi-driven publish/deploy/destroy steps into the Aspire pipeline. The Azure package translates the adopted environment's Bicep templates into Pulumi azure-native resources.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the implementation details and extension points.

## 📋 Commands

| Command | Description |
|---------|-------------|
| `aspire run` | Run locally; the Pulumi environment resource stays out of the dashboard |
| `aspire publish` | Write a reviewable `pulumi preview` artifact to the output directory |
| `aspire deploy` | Provision the registry (when a registry phase is configured), push images, and run `pulumi up` |
| `aspire destroy` | Run `pulumi destroy` for the environment (and its registry stack) |

## ⚙️ Configuration

### Azure Adoption Options

```csharp
var azureOptions = new AzureAdoptionOptions
{
    Location = "eastus",                 // Azure region
    ResourceGroupName = "my-rg",         // Resource group name override
    UseExistingResourceGroup = true,     // Adopt an existing resource group instead of creating one
};
```

### Registry-First Phase

Container images must land in a registry before `pulumi up` wires them into compute resources. Pass a
registry phase to provision the environment's container registry in its own Pulumi stack ahead of
Aspire's image push step (`CreateAzureRegistryPhase` also authenticates Docker via `az acr login`):

```csharp
builder.AddAzureContainerAppEnvironment("my-app")
    .PublishAsPulumi(
        PulumiStepSuppressionSelector.AzureContainerApps,
        context => context.TranslateAzureEnvironmentAsync(azureOptions),
        registryPhase: PulumiAzureAdoptionExtensions.CreateAzureRegistryPhase(azureOptions));
```

### Custom Programs

The Pulumi program is just an async callback over the publishing context — traverse the adopted
environment's deployment targets and registries, translate them, and export outputs:

```csharp
environment.PublishAsPulumi(
    PulumiStepSuppressionSelector.AzureContainerApps,
    async context =>
    {
        var translation = await context.TranslateAzureEnvironmentAsync(azureOptions);
        // Post-process translation.Templates / translation.Outputs, or add extra
        // Pulumi resources and context.AddOutput(...) entries here.
    });
```

### Multiple Environments

```csharp
// One adopted environment deploys to many Pulumi stacks. The stack is the Aspire deployment
// environment, selected at deploy time — you do NOT register one resource per environment.
builder.AddAzureContainerAppEnvironment("my-app")
    .PublishAsPulumi(/* ... */);
```

```bash
aspire deploy --environment dev      # Pulumi stack: my-app-pulumi/dev
aspire deploy --environment staging  # Pulumi stack: my-app-pulumi/staging
aspire deploy --environment prod     # Pulumi stack: my-app-pulumi/prod
```

> The environment name maps to the Pulumi stack (precedence: `--environment` > `DOTNET_ENVIRONMENT` >
> `ASPIRE_ENVIRONMENT`), matching how Aspire partitions deployment state per environment. Project and stack
> names may only contain alphanumeric characters, hyphens, underscores, or periods (per
> [Pulumi's naming rules](https://www.pulumi.com/docs/iac/concepts/stacks/)). When a registry phase is used,
> the container registry is provisioned as a sibling Pulumi project named `{project}-registry` with the
> same stack name.

To decouple the Pulumi stack from the Aspire environment name, override it explicitly with `WithStackName`:

```csharp
// Deploy always targets the "prod-eu" stack regardless of --environment.
builder.AddAzureContainerAppEnvironment("my-app")
    .PublishAsPulumi(
        PulumiStepSuppressionSelector.AzureContainerApps,
        context => context.TranslateAzureEnvironmentAsync(azureOptions),
        configureEnvironment: env => env.WithStackName("prod-eu"));
```

## 🗺️ Potential follow-ups

The current translation focuses on Azure Container Apps. Possible future work includes:

- 🔨 First-class translation for Azure App Service environments (`PulumiStepSuppressionSelector.AzureAppService` already ships)
- 📋 Additional compute targets (e.g., Kubernetes, Docker Compose) and cloud providers beyond Azure

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
