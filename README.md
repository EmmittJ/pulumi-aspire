# Pulumi Aspire

[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Aspire](https://img.shields.io/nuget/v/Aspire.Hosting?label=Aspire&color=512BD4)](https://aspire.dev/)
[![Pulumi](https://img.shields.io/badge/Pulumi-3.x-blueviolet)](https://www.pulumi.com/)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Deploy Aspire applications to cloud infrastructure using Pulumi's Infrastructure as Code.

## 🎯 Overview

**Pulumi Aspire** is an experimental Aspire hosting integration that swaps the execution engine of Aspire's native Azure provisioning pipeline for Pulumi. You keep Aspire's own environment modeling — `AddAzureContainerAppEnvironment` and friends — and add one line, `builder.UsePulumiProvisioning()`, to have every Azure template deployed with `pulumi up` into a real Pulumi stack instead of ARM deployments.

It currently focuses on:

- ☁️ **Azure Container Apps deployment** – this is the provider translation implemented today
- 🔧 **Preserving the whole Aspire experience** – `aspire run` stays untouched, and `aspire deploy` keeps its normal flow (subscription/resource-group/location prompts included); Pulumi replaces only the execution engine
- 🧪 **A real Pulumi stack** – the deployed stack supports `pulumi preview`, drift detection, and `pulumi destroy` out-of-band
- 🧩 **Zero pipeline modification** – every native step keeps running; the integration is a single internal DI seam swap

## 📦 Packages

| Package | Description | Status |
|---------|-------------|--------|
| `EmmittJ.Aspire.Hosting.Pulumi` | Core Pulumi Automation API runner | Not yet published |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure` | `UsePulumiProvisioning` + Bicep → Pulumi azure-native translation | Not yet published |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure.Seams` | Internal-seam boundary into Aspire's Azure provisioning pipeline | Not yet published |

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

// A completely standard Azure Container Apps environment...
builder.AddAzureContainerAppEnvironment("my-app");

// ...deployed by Pulumi instead of ARM — one line. The Pulumi project defaults to the AppHost name;
// the stack is the deployment environment (dev/staging/prod), selected at deploy time with
// `aspire deploy --environment <name>`.
builder.UsePulumiProvisioning();

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

Pulumi Aspire uses **internal-seam reuse**: Aspire's default Azure provisioning pipeline runs completely unmodified — prepare, login validation, provisioning-context creation (the interactive subscription/resource-group/location prompts), per-resource provision ordering and reporting, and the container registry login — and `UsePulumiProvisioning` swaps the one internal DI seam every native provision step resolves at execution time. Each template arrives with parameters already resolved by Aspire's own machinery, gets translated to Pulumi azure-native resources, and is deployed with `pulumi up` into a cumulative single stack; the deployed outputs are back-propagated into Aspire's model so every downstream native step (registry login, deployment-target parameters) works exactly as after an ARM deployment.

The seam is internal to `Aspire.Hosting.Azure`, so a small shim assembly borrows Aspire's own test-assembly identity (`InternalsVisibleTo` + public signing) to reach it — a deliberate trade-off, pinned by tests that fail loudly on Aspire upgrades. See the [internal-seam reuse spike](docs/spikes/internal-seam-reuse.md) for the full findings and caveats.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the implementation details and extension points.

## 📋 Commands

| Command | Description |
|---------|-------------|
| `aspire run` | Run locally; completely untouched |
| `aspire deploy` | The normal Aspire deploy flow, with every Azure template deployed via `pulumi up` |
| `aspire destroy` | Runs `pulumi destroy` against the stack (each resource torn down through Pulumi), then the native resource-group deletion |
| `pulumi preview` / `pulumi destroy` | Work directly against the deployed stack, out-of-band |

> ℹ️ `aspire destroy` prompts twice unless `--yes` is passed: once for the Pulumi stack's resources and
> once for the native resource-group deletion. Running `pulumi destroy` first keeps the stack's state
> consistent — it ends up empty instead of stale.

## ⚙️ Configuration

### Provisioning Options

```csharp
builder.UsePulumiProvisioning(options =>
{
    options.ProjectName = "my-app";        // Pulumi project (default: the AppHost name)
    options.StackName = "prod-eu";         // Pulumi stack (default: the deployment environment)
    options.ConfigureResource = resource =>
    {
        // Per-resource escape hatch for Pulumi-only concerns:
        // providers, aliases, protect flags, or property overrides.
        if (resource.ArmType == "Microsoft.App/containerApps")
        {
            resource.Options.Protect = true;
        }
    };
});
```

The target subscription, resource group, and location are **not** Pulumi settings: they come from Aspire's
own provisioning flow (interactive prompts, `Azure:*` configuration, deployment state), exactly as with the
default provisioner.

### Multiple Environments

```csharp
// One AppHost deploys to many Pulumi stacks. The stack is the Aspire deployment
// environment, selected at deploy time — no per-environment registration.
builder.AddAzureContainerAppEnvironment("my-app");
builder.UsePulumiProvisioning();
```

```bash
aspire deploy --environment dev      # Pulumi stack: dev
aspire deploy --environment staging  # Pulumi stack: staging
aspire deploy --environment prod     # Pulumi stack: prod
```

> The environment name maps to the Pulumi stack (precedence: `--environment` > `DOTNET_ENVIRONMENT` >
> `ASPIRE_ENVIRONMENT`), matching how Aspire partitions deployment state per environment. Project and stack
> names may only contain alphanumeric characters, hyphens, underscores, or periods (per
> [Pulumi's naming rules](https://www.pulumi.com/docs/iac/concepts/stacks/)); pin either explicitly with
> `options.ProjectName` / `options.StackName`.

## 🗺️ Potential follow-ups

The current translation focuses on Azure Container Apps. Possible future work includes:

- 🔨 Translation coverage for the Azure App Service environment's templates (the seam itself is environment-agnostic)
- 📋 Additional compute targets and cloud providers, via the same recipe against their provisioning seams

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
