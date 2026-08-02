// Vite + YARP static sample with Pulumi Azure deployment

using EmmittJ.Aspire.Hosting.Pulumi;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;

var builder = DistributedApplication.CreateBuilder(args);

// Add an Azure Container Apps environment and hand its deployment to Pulumi.
// "vite-yarp-static-pulumi" = Pulumi project; the stack is the deploy-time environment
// (e.g. `aspire deploy --environment dev` → Pulumi stack "dev").
var azureOptions = new AzureAdoptionOptions { Location = "eastus" };
builder.AddAzureContainerAppEnvironment("vite-yarp-static")
    .PublishAsPulumi(
        PulumiStepSuppressionSelector.AzureContainerApps,
        context => context.TranslateAzureEnvironmentAsync(azureOptions),
        registryPhase: PulumiAzureAdoptionExtensions.CreateAzureRegistryPhase(azureOptions));

// Add Vite frontend
var frontend = builder.AddViteApp("frontend", "./frontend");

// Add YARP reverse proxy
builder.AddYarp("app")
       .WithConfiguration(c =>
       {
           if (builder.ExecutionContext.IsRunMode)
           {
               // In run mode, forward all requests to vite dev server
               c.AddRoute("{**catch-all}", frontend);
           }
       })
       .WithExternalHttpEndpoints()
       .PublishWithStaticFiles(frontend);

builder.Build().Run();
