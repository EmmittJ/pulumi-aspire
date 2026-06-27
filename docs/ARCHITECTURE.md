# Pulumi Aspire - Architecture

## Overview

**Pulumi Aspire** enables Aspire applications to be deployed to cloud infrastructure using Pulumi. The architecture is centered on a single integration layer:

1. **SDK Packages (.NET)**: NuGet packages that integrate with Aspire's model and use Pulumi's Automation API for stack operations

## Design Principles

### 1. Translation in C#

All resource translation happens in the SDK packages (C#):

- **Adding cloud providers = adding NuGet packages**
- **Full access to Aspire's type system** (interfaces, annotations, DI)
- **Uses Pulumi Automation API directly** for stack operations
- **Deployment flow is centralized** in the .NET SDK and shared hosting abstractions

### 2. Automation API-First

We use Pulumi's **Automation API** rather than relying on an external language host protocol:

- The base `PulumiEnvironmentResource` handles all Automation API orchestration
- Provider packages only implement `CreateResourcesAsync()` to create cloud resources
- Stack lifecycle (create, up, preview, destroy) is managed by the base class

### 3. Aspire-Native Patterns

We follow Aspire's established patterns:

- **Pipeline Steps** via `PipelineStepAnnotation` (not the obsolete `IDistributedApplicationPublisher`)
- **Event Subscribers** via `IDistributedApplicationEventingSubscriber`
- **Compute Environments** via `IComputeEnvironmentResource`
- **Simple Output References** matching `BicepOutputReference` pattern

## Package Structure

```
pulumi-aspire/
├── src/
│   ├── EmmittJ.Aspire.Hosting.Pulumi/           # Core package
│   │   ├── IPulumiEnvironmentResource.cs        # Compute environment interface
│   │   ├── PulumiEnvironmentResource.cs         # Base class with Automation API
│   │   ├── PulumiPublishingContext.cs           # Context for resource creation
│   │   ├── PulumiOutputReference.cs             # Output reference
│   │   ├── PulumiAnnotations.cs                 # Resource annotations
│   │   ├── PulumiComputeResourceContext.cs      # Compute resource helpers
│   │   └── PulumiProvisioningResource.cs        # Provisioning resource
│   │
│   └── EmmittJ.Aspire.Hosting.Pulumi.Azure/     # Azure provider package
│       ├── PulumiAzureEnvironmentResource.cs    # Azure Container Apps
│       ├── PulumiAzureExtensions.cs             # Extension methods
│       ├── PulumiAzureInfrastructure.cs         # Event subscriber
│       └── PulumiAzureContainerAppCustomizationAnnotation.cs
├── tests/
│   └── EmmittJ.Aspire.Hosting.Pulumi.Tests/
└── samples/
    └── SampleAppHost/
```

## Key Components

### 1. IPulumiEnvironmentResource

Marker interface for Pulumi-managed compute environments:

```csharp
public interface IPulumiEnvironmentResource : IComputeEnvironmentResource
{
    string StackName => Name;           // Stack name = resource name
    string? ProjectName { get; }
    Task CreateResourcesAsync(PulumiPublishingContext context);
}
```

### 2. PulumiEnvironmentResource (Base Class)

Handles all Automation API orchestration:

```csharp
public abstract class PulumiEnvironmentResource : Resource, IPulumiEnvironmentResource
{
    protected PulumiEnvironmentResource(string name) : base(name)
    {
        // Register pipeline steps
        Annotations.Add(new PipelineStepAnnotation(CreatePipelineStepsAsync));
    }

    // Pipeline steps: deploy, preview, destroy, print-summary
    protected virtual async Task<IEnumerable<PipelineStep>> CreatePipelineStepsAsync(...);

    // Automation API operations
    protected virtual async Task<WorkspaceStack> GetOrCreateStackAsync(...);
    protected virtual async Task DeployAsync(PipelineStepContext context);
    protected virtual async Task PreviewAsync(PipelineStepContext context);
    protected virtual async Task DestroyAsync(PipelineStepContext context);

    // Provider-specific (abstract)
    public abstract Task CreateResourcesAsync(PulumiPublishingContext context);
}
```

**Key Design Points:**
- Uses `PulumiFn.Create()` for inline Pulumi programs
- Pipeline steps integrate with Aspire's DAG-based execution
- Provider packages only override `CreateResourcesAsync()`

### 3. PulumiPublishingContext

Context passed to providers during resource creation:

```csharp
public sealed class PulumiPublishingContext
{
    // Aspire model access
    public DistributedApplicationModel Model { get; }
    public IPulumiEnvironmentResource Environment { get; }
    public ILogger Logger { get; }

    // Resource tracking
    public IReadOnlyDictionary<IResource, PulumiResource> TranslatedResources { get; }
    public void RegisterResource(IResource aspire, PulumiResource pulumi);
    public T? GetResource<T>(IResource aspire) where T : PulumiResource;

    // Stack outputs
    public void AddOutput(string name, Output<string> value);
    public void AddOutput(string name, string value);
    public IDictionary<string, object?> BuildOutputs();
}
```

### 4. PulumiOutputReference

Simple reference to Pulumi outputs (matches Aspire's `BicepOutputReference` pattern):

```csharp
public sealed class PulumiOutputReference(string name, IResource resource)
    : IManifestExpressionProvider, IValueWithReferences
{
    public string Name { get; }
    public IResource Resource { get; }
    public Output<string>? Output { get; set; }
    public string? Value { get; internal set; }
    public string ValueExpression => $"{{{Resource.Name}.outputs.{Name}}}";
}
```

## Azure Provider Implementation

### PulumiAzureEnvironmentResource

```csharp
public class PulumiAzureEnvironmentResource : PulumiEnvironmentResource
{
    public string Location { get; set; } = "eastus";
    public string? ResourceGroupName { get; set; }
    public string? ManagedEnvironmentName { get; set; }

    public override async Task CreateResourcesAsync(PulumiPublishingContext context)
    {
        // Create shared infrastructure
        var resourceGroup = GetOrCreateResourceGroup(context);
        var managedEnvironment = GetOrCreateManagedEnvironment(context, resourceGroup);

        // Process each compute resource
        foreach (var resource in context.Model.GetComputeResources())
        {
            if (resource is IComputeResource computeResource)
            {
                await CreateContainerAppAsync(context, computeResource, 
                    resourceGroup, managedEnvironment);
            }
        }

        // Export outputs
        context.AddOutput("resourceGroupName", resourceGroup.Name);
        context.AddOutput("managedEnvironmentName", managedEnvironment.Name);
    }
}
```

### Extension Methods

```csharp
public static class PulumiAzureExtensions
{
    public static IResourceBuilder<PulumiAzureEnvironmentResource> AddPulumiAzureEnvironment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        var resource = new PulumiAzureEnvironmentResource(name);
        builder.Services.AddSingleton(resource);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDistributedApplicationEventingSubscriber, 
                PulumiAzureInfrastructure>());
        return builder.AddResource(resource);
    }

    public static IResourceBuilder<PulumiAzureEnvironmentResource> WithLocation(...);
    public static IResourceBuilder<PulumiAzureEnvironmentResource> WithResourceGroup(...);
    public static IResourceBuilder<T> PublishAsPulumiContainerApp<T>(...);
}
```

### Annotations

```csharp
// Skip during Pulumi translation
public sealed class SkipPulumiTranslationAnnotation : IResourceAnnotation;

// Pulumi resource options
public sealed record PulumiOptionsAnnotation(CustomResourceOptions? Options) : IResourceAnnotation;

// Customize Pulumi resources
public class PulumiCustomizationAnnotation<TResource> : IResourceAnnotation
    where TResource : Pulumi.Resource;
```

## Developer Workflow

```bash
# Local development - unchanged
aspire run

# Cloud deployment
aspire deploy                          # Uses Pulumi Automation API

# Other operations
aspire do pulumi-preview-{env}         # Preview changes
aspire do pulumi-destroy-{env}         # Destroy resources
```

## Environment Name = Stack Name

```csharp
builder.AddPulumiAzureEnvironment("dev");      // → stack "dev"
builder.AddPulumiAzureEnvironment("staging");  // → stack "staging"
builder.AddPulumiAzureEnvironment("prod");     // → stack "prod"
```

## Resource Mapping

### Current (Implemented)

| Aspire Resource | Azure Resource |
|-----------------|----------------|
| `IComputeResource` | `Azure.App.ContainerApp` |
| Container images | Deployed to Container Apps |
| HTTP endpoints | Container Apps ingress |

### Future (Planned)

| Resource Type | Azure | AWS | Kubernetes |
|--------------|-------|-----|------------|
| `PostgresServerResource` | PostgreSQL FlexibleServer | RDS | Helm Chart |
| `RedisResource` | Redis Cache | ElastiCache | Helm Chart |
| `SqlServerServerResource` | Azure SQL | RDS | Helm Chart |

## Implementation Status

### Phase 1: Foundation ✅

- [x] Core package with Automation API
- [x] `IPulumiEnvironmentResource` interface
- [x] `PulumiEnvironmentResource` base class
- [x] `PulumiPublishingContext`
- [x] `PulumiOutputReference` (simplified)
- [x] Pipeline steps (deploy, preview, destroy)
- [x] Azure Container Apps translation

### Phase 2: Databases (Planned)

- [ ] Redis → Azure Redis Cache
- [ ] PostgreSQL → Azure PostgreSQL
- [ ] SQL Server → Azure SQL
- [ ] Connection string propagation

### Phase 3: AWS Provider (Planned)

- [ ] `EmmittJ.Aspire.Hosting.Pulumi.Aws`
- [ ] ECS/Fargate for compute
- [ ] ElastiCache, RDS for databases

### Phase 4: Kubernetes Provider (Planned)

- [ ] `EmmittJ.Aspire.Hosting.Pulumi.Kubernetes`
- [ ] Deployments, Services
- [ ] Helm Charts for databases

## Dependencies

| Package | Dependencies |
|---------|--------------|
| Core | `Pulumi`, `Pulumi.Automation`, `Aspire.Hosting` |
| Azure | Core + `Pulumi.AzureNative` |

## References

- [Aspire Hosting](https://github.com/dotnet/aspire/tree/main/src/Aspire.Hosting)
- [Pulumi Automation API](https://www.pulumi.com/docs/guides/automation-api/)
- [pulumi-language-dotnet](https://github.com/pulumi/pulumi-dotnet/tree/main/pulumi-language-dotnet)
