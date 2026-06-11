using OpenCodex.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder
    .AddOpenCodexConfiguration();

builder.Services.AddOpenCodexApi(builder.Configuration);

var app = builder.Build();

app.UseOpenCodexApi();
app.Run();

public partial class Program;
