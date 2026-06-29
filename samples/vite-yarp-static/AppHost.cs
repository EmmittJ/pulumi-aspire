// Vite + YARP static sample with Pulumi Azure deployment

var builder = DistributedApplication.CreateBuilder(args);

// Add Pulumi Azure Container Apps environment for cloud deployment
// The name "dev" becomes the Pulumi stack name
var azure = builder.AddPulumiAzureContainerAppEnvironment("dev", "vite-yarp-static")
    .WithLocation("eastus");

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
