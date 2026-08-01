# Architecture

This document describes the internal structure of the Pulumi integration for maintainers. It complements the README by focusing on lifecycle and extension points rather than setup or end-user usage.

Pulumi Aspire deploys Aspire compute resources to the cloud using Pulumi's [Automation API](https://www.pulumi.com/docs/guides/automation-api/). It implements Aspire's deployment-target/publisher pattern so it participates in `aspire run`, `aspire publish`, and `aspire deploy` alongside other compute environments.

## Packages

| Package | Responsibility |
| --- | --- |
| `EmmittJ.Aspire.Hosting.Pulumi` | Provider-agnostic core: the compute-environment base class, deployment-target resource, value/secret resolver, container-registry base, output references, and the Automation API runner. |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure.AppContainers` | Azure Container Apps provider: translates compute resources to `ContainerApp` resources and provisions the supporting Azure infrastructure. |

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
- **deploy** (`DependsOn Push + BeforeStart`, `RequiredBy Deploy`) — runs `pulumi up`, then back-propagates stack outputs into the output references. The `BeforeStart` dependency is deliberate: the pipeline scheduler runs independent steps concurrently, and without it the deploy step could start while a prepare step (which attaches `DeploymentTargetAnnotation`s) is still running, observing a half-materialized model.
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

## Native environment adoption (adopt-and-traverse)

Alongside the Pulumi-owned environments above, the integration is building a second mode validated by the [native-environment step-adoption spike](spikes/native-environment-step-adoption.md) (**Go** for Azure Container Apps, Kubernetes, and Docker Compose): users keep configuring Aspire's **native** compute environments (`AddAzureContainerAppEnvironment`, `AddKubernetesEnvironment`, `AddDockerComposeEnvironment`), and Pulumi adopts them — the native environment's modeling steps still materialize the full provisioning model (Bicep templates, deployment targets, manifests), while its execution steps (Azure provisioning, Helm, `docker compose up`) are neutralized and Pulumi-owned steps spliced into the same pipeline slots become the execution engine over that model.

### Mechanism

Two Aspire API facts (verified against 13.4.6) fix the seam: `PipelineStep.Action` and `PipelineConfigurationContext.Steps` are init-only, so a pipeline-configuration callback can rewire edges but cannot remove or replace steps. Suppression therefore happens **at builder time, before build**, using only public APIs:

- `NativePipelineStepAdoption.SuppressExecutionSteps` re-wraps each adopted resource's `PipelineStepAnnotation`s so steps matching a suppression selector are substituted with **same-name no-op clones**. Because a clone keeps the original name, tags, and `DependsOn`/`RequiredBy` edges, the step graph stays identical: every other step schedules exactly as before and graph validation is untouched.
- `PulumiStepSuppressionSelector` keeps the per-provider selectors **data-driven** (exact names, name prefixes, tags): `AzureContainerApps`, `Kubernetes`, and `DockerCompose` are shipped as well-known selectors. The native step names/tags are undocumented strings, so the catalogue tests in `EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests` pin them — an Aspire version bump that changes them fails CI with the exact diff instead of silently deploying twice.
- Spliced Pulumi steps must include `BeforeStart` in their dependencies (in addition to `Push`) so they never observe a half-materialized model while native prepare steps are still attaching `DeploymentTargetAnnotation`s.

### Decorator and backend

`PublishAsPulumi(selector, program)` (on the native environment's resource builder) is the user-facing entry point: in publish mode it applies the suppression (all resources by default, since native environments spread execution steps across implicitly added resources; a filter parameter narrows this when multiple environments coexist) and registers a `PulumiBackendResource` named `{environment}-pulumi`. In run mode it is a no-op so `aspire run` stays untouched. The backend splices `pulumi-publish/deploy/destroy-{name}` steps into the standard slots (deploy: `dependsOn push + before-start`, `requiredBy deploy`) and runs the supplied program through the Automation API; the program receives a `PulumiAdoptionContext` and walks the materialized model with `GetDeploymentTargets()` (Bicep-backed targets for ACA, service resources for Kubernetes/Compose), exporting stack outputs via `AddOutput`.

### Registry-first phase (the ACR-login/push-credential seam)

Suppressing the ACA `login-to-acr-*` step (`RequiredBy push-prereq`) means the Pulumi backend must supply a provisioned registry and Docker credentials before Aspire's push step runs — the one native behavior that is *replaced* rather than merely skipped. `PulumiRegistryPhase` (passed to `PublishAsPulumi(..., registryPhase: ...)`) resolves this: the backend splices a `pulumi-deploy-registry-{name}` step (`dependsOn before-start`, `requiredBy push-prereq`) that runs the phase's program as a Pulumi `up` against a dedicated `{project}-registry` stack, then invokes the phase's login callback for each registry the adopted environment attached to its deployment targets. A matching `pulumi-destroy-registry-{name}` step (`dependsOn` the main destroy step) tears the registry stack down after the main stack so workloads referencing registry images are gone before the registry is.

For Azure, `CreateAzureRegistryPhase(options)` is the ready-made phase: its program (`TranslateAzureRegistriesAsync`) translates the registry-template closure the adopted environment attached to its deployment targets into the registry stack (defaulting to its own `{environment}-registry-rg` resource group so destroying either stack never deletes the other's resources), and its login callback runs `az acr login`. Exporting the registry outputs is load-bearing: it roots the applies that back-propagate the deployed values into the registry resource's Aspire outputs, which is what lets Aspire's push step, the login callback, and the main deploy resolve the registry name/endpoint. When a registry phase is set, `TranslateAzureEnvironmentAsync` excludes the registry templates from the main stack so the same ARM resources are never managed by two stacks — their `BicepOutputReference` parameters resolve from the back-propagated outputs instead.

### Azure adoption frontend

`TranslateAzureEnvironmentAsync(options)` (on `PulumiAdoptionContext`, in the Azure package) is the ready-made program body for adopted Azure Container Apps environments. It discovers every Bicep template the adopted environment materialized — the environment resource itself plus each deployment target, expanded to the closure of templates referenced through `BicepOutputReference` parameters (e.g. the standalone container registry template) — orders them by parameter dependencies, creates or references the target resource group, and translates each template with the [azure-provisioning translation core](spikes/azure-provisioning-translation-source.md) (`AzureProvisioningTemplateTranslator`). Every template output is exported as a `{template}_{output}` stack output, which both surfaces the values and roots the translator's back-propagation applies. `AzureAdoptionOptions` controls the resource group (name, location, use-existing) and exposes a `ConfigureResource` hook for per-resource input/option customization. The `PulumiOperation` carried by the context selects the translation mode: `Preview` substitutes deterministic placeholders for ambient invokes so `pulumi preview` needs no Azure credentials, while `Up` resolves them through real invokes.

### Roadmap

1. ✅ Suppression primitives: `NativePipelineStepAdoption` + `PulumiStepSuppressionSelector` with pinned per-provider selectors.
2. ✅ A generic Pulumi backend resource (`PulumiBackendResource`) and the `PublishAsPulumi(...)` decorator that applies the suppression, splices the Pulumi publish/deploy/destroy steps, and walks the materialized model through `PulumiAdoptionContext` (Bicep for ACA, deployment targets for Kubernetes/Compose).
3. ✅ The Azure adoption frontend: `TranslateAzureEnvironmentAsync` wires the Bicep→azure-native translation core into the adoption flow, pinned by mock-engine fixture tests over the real ACA provisioning model.
4. ✅ The ACR-login/push-credential seam: `PulumiRegistryPhase` + `CreateAzureRegistryPhase` provision the registry into its own stack before push and re-implement the login against Pulumi outputs.
5. 🔨 Kubernetes and Docker Compose adoption frontends over the materialized deployment targets.


## References

- [Aspire](https://aspire.dev/)
- [Pulumi Automation API](https://www.pulumi.com/docs/guides/automation-api/)

