using OpenCodex.Api.Hosting;

var contentRoot = OpenCodexContentRootResolver.ResolveContentRoot();
var webRoot = OpenCodexContentRootResolver.ResolveWebRoot(contentRoot);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = webRoot
});

builder
    .AddOpenCodexConfiguration();

builder.Services.AddOpenCodexApi(builder.Configuration);

var app = builder.Build();

app.UseOpenCodexApi();
app.Run();

public partial class Program;
