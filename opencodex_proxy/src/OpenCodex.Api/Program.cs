using OpenCodex.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder
    .AddOpenCodexConfiguration()
    .UseOpenCodexUrls();

builder.Services.AddOpenCodexApi();

var app = builder.Build();

app.UseOpenCodexApi();
app.Run();

public partial class Program;
