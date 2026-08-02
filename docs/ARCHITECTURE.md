# Architecture

This document describes the internal structure of the Pulumi integration for maintainers. It complements the README by focusing on lifecycle and extension points rather than setup or end-user usage.

Pulumi Aspire deploys Aspire applications to the cloud using Pulumi's [Automation API](https://www.pulumi.com/docs/guides/automation-api/) through **native environment adoption** (adopt-and-traverse), validated by the [native-environment step-adoption spike](spikes/native-environment-step-adoption.md): users keep configuring Aspire's **native** compute environments (`AddAzureContainerAppEnvironment`, `AddAzureAppServiceEnvironment`), and Pulumi adopts them — the native environment's modeling steps still materialize the full provisioning model (Bicep templates, deployment targets), while its execution steps (Azure provisioning, ACR login) are neutralized and Pulumi-owned steps spliced into the same pipeline slots become the execution engine over that model. The spike also proved the seam for Kubernetes and Docker Compose, but those adoption frontends are deferred for now — the current focus is the Azure Native providers.

## Packages

| Package | Responsibility |
| --- | --- |
| `EmmittJ.Aspire.Hosting.Pulumi` | Provider-agnostic core: the `PublishAsPulumi` decorator, step suppression primitives, the Pulumi environment resource and publishing context, the value/secret resolver, and the Automation API runner. |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure` | Azure adoption frontend: translates the adopted environment's Bicep templates into Pulumi azure-native resources and ships the ready-made registry-first phase. |

## Core types

| Type | Role |
| --- | --- |
| `PulumiEnvironmentResource` | The Pulumi execution engine for one adopted environment, named `{environment}-pulumi`. Splices the Pulumi publish/deploy/destroy steps into the pipeline and runs the user-supplied program through the Automation API. |
| `PulumiPublishingContext` | The context handed to the program: exposes the model, the adopted environment, the operation (`Preview`/`Up`/`Destroy`), and traversal helpers (`GetDeploymentTargets()`, `GetContainerRegistries()`), plus `AddOutput` for exporting stack outputs. |
| `NativePipelineStepAdoption` | Builder-time suppression primitive that re-wraps `PipelineStepAnnotation`s to substitute selected steps with same-name no-op clones. |
| `PulumiStepSuppressionSelector` | Structural-first step classifier (public well-known steps/tags + graph position), with data lists to widen and `Keep*` lists to narrow the match; `Structural`, `AzureContainerApps`, and `AzureAppService` ship as well-known selectors. |
| `PulumiAdoptionGuard` | Internal configuration-time invariant guard wired by `PublishAsPulumi`: fails the pipeline with an actionable error when an execution step of an adopted resource escapes suppression. |
| `PulumiRegistryPhase` | Optional registry-first phase: provisions the adopted environment's container registry into its own stack before Aspire's push step, plus a login callback for Docker credentials. |
| `PulumiValueResolver` | Resolves Aspire structured values to Pulumi `Output<string>`, tracking secret-ness. |
| `PulumiRunner` | Thin wrapper over the Automation API for `up`, `preview`, and `destroy`. |

## Suppression mechanism

Two Aspire API facts (verified against 13.4.6) fix the seam: `PipelineStep.Action` and `PipelineConfigurationContext.Steps` are init-only, so a pipeline-configuration callback can rewire edges but cannot remove or replace steps. Suppression therefore happens **at builder time, before build**, using only public APIs:

- `NativePipelineStepAdoption.SuppressExecutionSteps` re-wraps each adopted resource's `PipelineStepAnnotation`s so steps matching a suppression selector are substituted with **same-name no-op clones**. Because a clone keeps the original name, tags, and `DependsOn`/`RequiredBy` edges, the step graph stays identical: every other step schedules exactly as before and graph validation is untouched.
- `PulumiStepSuppressionSelector` classifies execution steps **structurally by default**, using only public Aspire pipeline contracts — no per-version step-name maintenance: a step is an execution step when it carries the `WellKnownPipelineTags.ProvisionInfrastructure` tag, depends on `deploy-prereq` (deploy-slot execution work such as login validation and provisioning-context creation), touches the destroy slot (`destroy-prereq`/`destroy` edges), or is a registry-owned `push-prereq` step (the login seam the registry phase replaces). Data lists (names, prefixes, tags) widen the match and `Keep*` lists always win and narrow it, so provider quirks stay expressible. The shipped `AzureContainerApps`/`AzureAppService` selectors keep their pinned names/tags as belt-and-braces on top of the structural rules.
- The catalogue tests in `EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests` pin the invariant that matters: on the real ACA/App Service pipelines, the purely `Structural` selector classifies **exactly** the same execution steps as the legacy pinned data — an Aspire version bump that moves either signal fails CI with the exact diff instead of silently deploying twice.
- Suppression happens at builder time, so `PublishAsPulumi` also wires a `PipelineConfigurationAnnotation` guard (`PulumiAdoptionGuard`) that re-validates the fully materialized step graph at pipeline-configuration time: any adopted-resource step that matches the selector but was registered after adoption, or that sits inside the deploy/destroy phase without being cosmetic (summary printers) or explicitly kept, fails the pipeline with an actionable error.
- Spliced Pulumi steps must include `BeforeStart` in their dependencies (in addition to `Push`) so they never observe a half-materialized model while native prepare steps are still attaching `DeploymentTargetAnnotation`s.

## Decorator and environment resource

`PublishAsPulumi(selector, program)` (on the native environment's resource builder) is the user-facing entry point: in publish mode it applies the suppression (all resources by default, since native environments spread execution steps across implicitly added resources; a filter parameter narrows this when multiple environments coexist) and registers a `PulumiEnvironmentResource` named `{environment}-pulumi`. In run mode it is a no-op so `aspire run` stays untouched — the Pulumi environment resource never appears in the dashboard. An optional `configureEnvironment` callback customizes the resource (for example `WithStackName`).

For the native Azure environments, the Azure package layers a one-line `PublishAsPulumi(options?)` overload (constrained to `IAzureComputeEnvironmentResource`) on top: the suppression selector is inferred from the adopted environment's type (the pinned Azure Container Apps / Azure App Service selectors for the known types, falling back to `PulumiStepSuppressionSelector.Structural` for any other Azure compute environment — so new native environments adopt without a library update), the program defaults to `TranslateAzureEnvironmentAsync`, and the registry-first phase defaults to `CreateAzureRegistryPhase`. An optional `program` parameter swaps in a custom program while keeping the inferred selector and default registry phase; the general overload remains the full-control escape hatch.

The environment resource splices `pulumi-publish/deploy/destroy-{name}` steps into the standard slots and runs the supplied program through the Automation API; the program receives a `PulumiPublishingContext` and walks the materialized model with `GetDeploymentTargets()` (Bicep-backed targets for the Azure environments), exporting stack outputs via `AddOutput`.

### Pipeline steps

- **publish** (`DependsOn PublishPrereq`, `RequiredBy Publish`) — writes a reviewable `pulumi preview` artifact to the environment output directory without deploying.
- **deploy** (`DependsOn Push + BeforeStart`, `RequiredBy Deploy`) — runs `pulumi up` over the program. The `BeforeStart` dependency is deliberate: the pipeline scheduler runs independent steps concurrently, and without it the deploy step could start while native prepare steps (which attach `DeploymentTargetAnnotation`s) are still running, observing a half-materialized model.
- **destroy** (`DependsOn DestroyPrereq`, `RequiredBy Destroy`) — runs `pulumi destroy`.
- **deploy-registry / destroy-registry** — only when a registry phase is configured (see below).

### Stack naming

The Pulumi project is `{environment}-pulumi` (override via the resource's `PulumiProjectName`). The stack is the Aspire deployment environment (precedence: `--environment` > `DOTNET_ENVIRONMENT` > `ASPIRE_ENVIRONMENT`), lower-cased and validated against Pulumi's naming rules; `WithStackName` pins it explicitly. The registry phase uses a sibling project named `{project}-registry` with the same stack name.

## Value and secret resolution

`PulumiValueResolver` resolves Aspire structured values the same way the Azure Container Apps translator does, handling `string`, `EndpointReference`, `EndpointReferenceExpression`, `ParameterResource`, `ConnectionStringReference`, `IResourceWithConnectionString`, `ReferenceExpression`, and `IValueProvider`. Secret parameters and connection strings are wrapped with `Output.CreateSecret` so they are encrypted in Pulumi state rather than written as plaintext. Endpoint resolution is platform-specific and supplied via the `EndpointResolver`/`EndpointExpressionResolver` hooks.

## Registry-first phase (the ACR-login/push-credential seam)

Suppressing the ACA `login-to-acr-*` step (`RequiredBy push-prereq`) means the Pulumi environment must supply a provisioned registry and Docker credentials before Aspire's push step runs — the one native behavior that is *replaced* rather than merely skipped. `PulumiRegistryPhase` (passed to `PublishAsPulumi(..., registryPhase: ...)`; the Azure one-line overload sets it by default) resolves this: the environment resource splices a `pulumi-deploy-registry-{name}` step (`dependsOn before-start`, `requiredBy push-prereq`) that runs the phase's program as a Pulumi `up` against a dedicated `{project}-registry` stack, then invokes the phase's login callback for each registry the adopted environment attached to its deployment targets. A matching `pulumi-destroy-registry-{name}` step (`dependsOn` the main destroy step) tears the registry stack down after the main stack so workloads referencing registry images are gone before the registry is.

For Azure, `CreateAzureRegistryPhase(options)` is the ready-made phase: its program (`TranslateAzureRegistriesAsync`) translates the registry-template closure the adopted environment attached to its deployment targets into the registry stack (defaulting to its own `{environment}-registry-rg` resource group so destroying either stack never deletes the other's resources), and its login callback runs `az acr login`. Exporting the registry outputs is load-bearing: it roots the applies that back-propagate the deployed values into the registry resource's Aspire outputs, which is what lets Aspire's push step, the login callback, and the main deploy resolve the registry name/endpoint. When a registry phase is set, `TranslateAzureEnvironmentAsync` excludes the registry templates from the main stack so the same ARM resources are never managed by two stacks — their `BicepOutputReference` parameters resolve from the back-propagated outputs instead.

## Azure adoption frontend

`TranslateAzureEnvironmentAsync(options)` (on `PulumiPublishingContext`, in the Azure package) is the ready-made program body for adopted Azure Container Apps environments. It discovers every Bicep template the adopted environment materialized — the environment resource itself plus each deployment target, expanded to the closure of templates referenced through `BicepOutputReference` parameters (e.g. the standalone container registry template) — orders them by parameter dependencies, creates or references the target resource group, and translates each template with the [azure-provisioning translation core](spikes/azure-provisioning-translation-source.md) (`AzureProvisioningTemplateTranslator`). Every template output is exported as a `{template}_{output}` stack output, which both surfaces the values and roots the translator's back-propagation applies. `AzureAdoptionOptions` controls the resource group (name, location, use-existing) and exposes a `ConfigureResource` hook for per-resource input/option customization. The `PulumiOperation` carried by the context selects the translation mode: `Preview` substitutes deterministic placeholders for ambient invokes so `pulumi preview` needs no Azure credentials, while `Up` resolves them through real invokes.

## Adding an adoption frontend

A new provider frontend typically includes:

1. A suppression selector for the provider's native execution steps. Start with `PulumiStepSuppressionSelector.Structural` — providers whose execution steps carry the well-known provisioning tag and deploy/destroy edges need no data at all — and add data/`Keep*` overrides only for steps the structural rules cannot see (for example Kubernetes' `check-helm-prereqs`, which declares no edges). Pin the resulting suppression set with catalogue tests so Aspire version bumps surface drift.
2. A `Translate{Provider}EnvironmentAsync` extension on `PulumiPublishingContext` that traverses the adopted environment's deployment targets and translates them into Pulumi resources.
3. Optionally, a ready-made `PulumiRegistryPhase` factory if the provider needs a registry provisioned before image push.

The Azure package is the only frontend implemented in this repository today.

## Roadmap

1. ✅ Suppression primitives: `NativePipelineStepAdoption` + `PulumiStepSuppressionSelector` with structural-first classification (proven equivalent to the pinned per-provider selectors by catalogue invariant tests) and a configuration-time adoption guard.
2. ✅ The Pulumi environment resource (`PulumiEnvironmentResource`) and the `PublishAsPulumi(...)` decorator that applies the suppression, splices the Pulumi publish/deploy/destroy steps, and walks the materialized model through `PulumiPublishingContext`.
3. ✅ The Azure adoption frontend: `TranslateAzureEnvironmentAsync` wires the Bicep→azure-native translation core into the adoption flow, pinned by mock-engine fixture tests over the real ACA provisioning model.
4. ✅ The ACR-login/push-credential seam: `PulumiRegistryPhase` + `CreateAzureRegistryPhase` provision the registry into its own stack before push and re-implement the login against Pulumi outputs.
5. ⏸️ Kubernetes and Docker Compose adoption frontends — deferred; the current focus is the Azure Native providers.

## References

- [Aspire](https://aspire.dev/)
- [Pulumi Automation API](https://www.pulumi.com/docs/guides/automation-api/)
