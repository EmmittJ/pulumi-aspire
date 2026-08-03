// Vite + YARP static sample with Pulumi Azure deployment

using EmmittJ.Aspire.Hosting.Pulumi;

var builder = DistributedApplication.CreateBuilder(args);

// A completely standard Azure Container Apps environment...
builder.AddAzureContainerAppEnvironment("vite-yarp-static");

// ...deployed by Pulumi instead of ARM — one line. `aspire deploy` runs its normal flow (subscription,
// resource group, and location prompts included); Pulumi replaces only the execution engine, translating
// every template to azure-native resources in a real stack (project = the AppHost name, stack = the
// deploy-time environment, e.g. `aspire deploy --environment dev` → Pulumi stack "dev").
builder.UsePulumiProvisioning();

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
