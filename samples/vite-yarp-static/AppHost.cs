// Vite + YARP static sample with Pulumi Azure deployment

using EmmittJ.Aspire.Hosting.Pulumi.Azure;

var builder = DistributedApplication.CreateBuilder(args);

// Add an Azure Container Apps environment and hand its deployment to Pulumi — one line.
// Everything is inferred from the environment: the suppression selector, the translation program, and the
// registry-first phase (Aspire's own ACR model, provisioned into its own "vite-yarp-static-pulumi-registry"
// stack before images are pushed). "vite-yarp-static-pulumi" = Pulumi project; the stack is the deploy-time
// environment (e.g. `aspire deploy --environment dev` → Pulumi stack "dev").
builder.AddAzureContainerAppEnvironment("vite-yarp-static")
    .PublishAsPulumi(new AzureAdoptionOptions { Location = "eastus" });

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
