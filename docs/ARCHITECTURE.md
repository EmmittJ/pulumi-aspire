# Architecture

This document describes the internal structure of the Pulumi integration for maintainers. It complements the README by focusing on lifecycle and extension points rather than setup or end-user usage.

Pulumi Aspire deploys Aspire compute resources to the cloud using Pulumi's [Automation API](https://www.pulumi.com/docs/guides/automation-api/). It implements Aspire's deployment-target/publisher pattern so it participates in `aspire run`, `aspire publish`, and `aspire deploy` alongside other compute environments.

## Packages

| Package | Responsibility |
| --- | --- |
| `EmmittJ.Aspire.Hosting.Pulumi` | Provider-agnostic core: the compute-environment base class, deployment-target resource, value/secret resolver, container-registry base, output references, and the Automation API runner. |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure` | Azure Container Apps provider: translates compute resources to `ContainerApp` resources and provisions the supporting Azure infrastructure. |

## Core types

| Type | Role |
| --- | --- |
| `PulumiEnvironmentResource` | Abstract `IComputeEnvironmentResource`. Owns the pipeline steps and the inline Pulumi program. Providers implement `CreateStackResourcesAsync`. |
| `PulumiDeploymentTargetResource` | The per-compute-resource target attached via `DeploymentTargetAnnotation`. Holds output references and a print-summary step. Not added to the model. |
| `PulumiComputeResourceContext` | Abstract per-resource translator. Collects environment variables, args, and endpoints, and resolves Aspire structured values to Pulumi `Output<T>` (wrapping secrets). Providers build the cloud resource. |
| `PulumiContainerRegistryResource` | Abstract registry provisioned as its own Pulumi stack so images can be pushed before the environment deploys. |
| `PulumiOutputReference` | Deferred reference to a stack output (the Pulumi analogue of `BicepOutputReference`), resolved after deploy. |
| `PulumiRunner` | Thin wrapper over the Automation API for `up`, `preview`, and `destroy`. |

## Pipeline lifecycle

The environment registers its steps through a `PipelineStepAnnotation`, and each deployment target's steps are expanded into the pipeline during step collection. Build and push steps are created automatically by Aspire for project and container resources, so the integration does not create them.

- **prepare** (`DependsOn ValidateComputeEnvironments`, `RequiredBy BeforeStart`) — publish-only. Creates a `PulumiDeploymentTargetResource` per targeted compute resource and attaches a `DeploymentTargetAnnotation`.
- **publish** (`DependsOn PublishPrereq`, `RequiredBy Publish`) — writes a reviewable `pulumi preview` artifact to the environment output directory without deploying.
- **deploy** (`DependsOn Push`, `RequiredBy Deploy`) — runs `pulumi up`, then back-propagates stack outputs into the output references.
- **destroy** (`DependsOn DestroyPrereq`, `RequiredBy Destroy`) — runs `pulumi destroy`. The registry stack has its own destroy step so it is not orphaned.

## Mode behavior

| Mode | Behavior |
| --- | --- |
| Run | The environment and registry are **not** added to the model, so they never appear in the dashboard. |
| Publish | The environment and registry are added to the model. The publish step emits a reviewable preview artifact. |
| Deploy | The deploy step provisions the registry, lets the framework push images, then runs the inline Pulumi program to provision cloud resources. |

## Value and secret resolution

`PulumiComputeResourceContext` resolves Aspire structured values the same way the Azure Container Apps translator does, handling `string`, `EndpointReference`, `EndpointReferenceExpression`, `ParameterResource`, `ConnectionStringReference`, `IResourceWithConnectionString`, `ReferenceExpression`, `PulumiOutputReference`, and `IValueProvider`. Secret parameters and connection strings are wrapped with `Output.CreateSecret` so they are encrypted in Pulumi state rather than written as plaintext.

## Container registry

The Azure provider provisions an Azure Container Registry as a **separate Pulumi stack** before the environment deploys, so images can be built and pushed first. A user-assigned managed identity is granted `AcrPull`, and the Container Apps reference the registry through that identity. The registry is environment-owned and is destroyed alongside the environment.

## Adding a provider

A new cloud provider implementation typically includes:

1. A `PulumiEnvironmentResource` subclass whose `CreateStackResourcesAsync` provisions the provider's infrastructure and translates each targeted compute resource.
2. A `PulumiComputeResourceContext` subclass that builds the provider's workload resource and resolves endpoints.
3. A `PulumiContainerRegistryResource` subclass if the provider needs a registry.
4. An `Add{Provider}Environment` extension method (in the `Aspire.Hosting` namespace) that applies the run/publish split and calls `AddPulumiInfrastructureCore`.

The Azure package is the only provider implemented in this repository today.

## References

- [Aspire](https://aspire.dev/)
- [Pulumi Automation API](https://www.pulumi.com/docs/guides/automation-api/)

