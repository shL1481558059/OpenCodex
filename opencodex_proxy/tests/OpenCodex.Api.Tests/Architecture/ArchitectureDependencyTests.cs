namespace OpenCodex.Api.Tests.Architecture;

public sealed class ArchitectureDependencyTests
{
    [Fact]
    public void ExternalIntegrationsDoNotReferenceServices()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/ExternalIntegrations",
            "OpenCodex.Api.Services");
    }

    [Fact]
    public void AbstractionsDoNotReferenceConcreteLayers()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Abstractions",
            "OpenCodex.Api.Services",
            "OpenCodex.Api.Persistence",
            "OpenCodex.Api.ExternalIntegrations",
            "OpenCodex.Api.Infrastructure",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions");
    }

    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Domain",
            "OpenCodex.Api.Services",
            "OpenCodex.Api.Persistence",
            "OpenCodex.Api.ExternalIntegrations",
            "OpenCodex.Api.Infrastructure",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions");
    }

    [Fact]
    public void PersistenceDoesNotReferenceOuterLayers()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Persistence",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs",
            "OpenCodex.Api.Services",
            "OpenCodex.Api.Infrastructure",
            "OpenCodex.Api.ExternalIntegrations",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions");
    }

    [Fact]
    public void InfrastructureDoesNotReferenceUpperOrConcreteOuterLayers()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Infrastructure",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs",
            "OpenCodex.Api.Services",
            "OpenCodex.Api.Persistence",
            "OpenCodex.Api.ExternalIntegrations");
    }

    [Fact]
    public void LowerLayersDoNotReferenceHosting()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Abstractions",
            "OpenCodex.Api.Hosting");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Domain",
            "OpenCodex.Api.Hosting");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Persistence",
            "OpenCodex.Api.Hosting");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Infrastructure",
            "OpenCodex.Api.Hosting");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/ExternalIntegrations",
            "OpenCodex.Api.Hosting");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Services",
            "OpenCodex.Api.Hosting");
    }

    [Fact]
    public void LowerLayersDoNotReferenceApiSurface()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Abstractions",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Domain",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Persistence",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Infrastructure",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/ExternalIntegrations",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Services",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs");
    }

    [Fact]
    public void DtosDoNotReferenceAspNetCore()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/DTOs",
            "Microsoft.AspNetCore",
            "HttpContext",
            "HttpRequest",
            "HttpResponse",
            "IQueryCollection",
            "IActionResult",
            "ActionResult");
    }

    [Fact]
    public void DtosDoNotReferenceServices()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/DTOs",
            "OpenCodex.Api.Services");
    }

    [Fact]
    public void AdminAuthDtosDoNotReferenceServices()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/DTOs/AdminAuth",
            "OpenCodex.Api.Services");
    }

    [Fact]
    public void AdminChannelDiagnosticsDtosDoNotReferenceServices()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/DTOs/AdminChannelDiagnostics",
            "OpenCodex.Api.Services");
    }

    [Fact]
    public void AdminConfigDtosDoNotReferenceServices()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/DTOs/AdminConfig",
            "OpenCodex.Api.Services");
    }

    [Fact]
    public void AdminWebSearchDtosDoNotReferenceServices()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/DTOs/AdminWebSearch",
            "OpenCodex.Api.Services");
    }

    [Fact]
    public void AdminServicesDoNotReferenceAspNetCore()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Services/Admin",
            "Microsoft.AspNetCore",
            "HttpContext",
            "HttpRequest",
            "HttpResponse",
            "IActionResult",
            "ActionResult");
    }

    [Fact]
    public void ServicesAndExternalIntegrationsDoNotReferenceAspNetCoreStatusCodes()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Services",
            "Microsoft.AspNetCore",
            "StatusCodes");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/ExternalIntegrations",
            "Microsoft.AspNetCore",
            "StatusCodes");
    }

    [Fact]
    public void ServicesDoNotReadHostConfigurationDirectly()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Services",
            "IConfiguration",
            "IHostEnvironment",
            "IWebHostEnvironment",
            "IOptions",
            "Configuration[",
            ".GetSection(",
            "ContentRootPath",
            "EnvironmentName");
    }

    [Fact]
    public void ServicesAndPersistenceDoNotReferenceConfigurationLayer()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Services",
            "OpenCodex.Api.Configuration");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Persistence",
            "OpenCodex.Api.Configuration");
    }

    [Fact]
    public void ConfigurationDoesNotOwnServiceFacingContracts()
    {
        AssertNoSourceFilesNamed(
            "src/OpenCodex.Api/Configuration",
            "IAdminStaticDirectoryProvider.cs",
            "IOpenCodexRuntimeSettingsProvider.cs",
            "OpenCodexRuntimeSettings.cs");
    }

    [Fact]
    public void SessionUsesGenericUserRepository()
    {
        AssertNoSourceFilesNamed(
            "src/OpenCodex.Api/Persistence",
            "IAdminSessionRepository.cs",
            "AdminSessionRepository.cs");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Services/Admin",
            new[] { "AdminSessionService.cs" },
            "IAdminSessionRepository",
            "AdminSessionRepository");
    }

    [Fact]
    public void CoreConfigProtocolAndRoutingDoNotReferenceOuterLayers()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Config",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs",
            "OpenCodex.Api.Persistence",
            "OpenCodex.Api.Infrastructure",
            "OpenCodex.Api.ExternalIntegrations",
            "OpenCodex.Api.Hosting",
            "OpenCodex.Api.Configuration",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Protocols",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs",
            "OpenCodex.Api.Persistence",
            "OpenCodex.Api.Infrastructure",
            "OpenCodex.Api.ExternalIntegrations",
            "OpenCodex.Api.Hosting",
            "OpenCodex.Api.Configuration",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions");

        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Routing",
            "OpenCodex.Api.Controllers",
            "OpenCodex.Api.DTOs",
            "OpenCodex.Api.Persistence",
            "OpenCodex.Api.Infrastructure",
            "OpenCodex.Api.ExternalIntegrations",
            "OpenCodex.Api.Hosting",
            "OpenCodex.Api.Configuration",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions");
    }

    [Fact]
    public void ProxyExceptionTypesDoNotReferenceAspNetCore()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Errors",
            new[]
            {
                "BadRequestException.cs",
                "ProxyException.cs",
                "ProxyHttpStatus.cs",
                "RoutingException.cs",
                "UpstreamException.cs"
            },
            "Microsoft.AspNetCore",
            "StatusCodes",
            "HttpContext",
            "HttpRequest",
            "HttpResponse",
            "RequestDelegate",
            "ILogger",
            "IActionResult",
            "ActionResult");
    }

    [Fact]
    public void ProxyEndpointAndStreamServicesDoNotReferenceHttpTypes()
    {
        AssertNoForbiddenReferences(
            "src/OpenCodex.Api/Services/Proxy",
            new[]
            {
                "ProxyEndpointService.cs",
                "ProxyEndpointContext.cs",
                "ProxyStreamService.cs",
                "ProxyStreamContext.cs"
            },
            "HttpRequest",
            "HttpResponse",
            "Microsoft.AspNetCore",
            "StatusCodes",
            "IRequestBodyReader",
            "ProxyRequestMetadataFactory");
    }

    [Fact]
    public void InfrastructureDoesNotOwnJsonDictionaryValue()
    {
        AssertNoSourceFilesNamed(
            "src/OpenCodex.Api/Infrastructure",
            "JsonDictionaryValue.cs");
    }

    [Fact]
    public void InfrastructureDoesNotOwnWebSearchPayload()
    {
        AssertNoSourceFilesNamed(
            "src/OpenCodex.Api/Infrastructure",
            "WebSearchPayload.cs");
    }

    private static void AssertNoForbiddenReferences(
        string relativeDirectory,
        params string[] forbiddenReferences)
    {
        var sourceRoot = SourceRoot();
        var directory = Path.Combine(sourceRoot, relativeDirectory);
        Assert.True(Directory.Exists(directory), $"Directory not found: {directory}");

        var violations = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => ForbiddenReferencesInFile(sourceRoot, file, forbiddenReferences))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Forbidden architecture references found:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static void AssertNoForbiddenReferences(
        string relativeDirectory,
        IReadOnlyCollection<string> fileNames,
        params string[] forbiddenReferences)
    {
        var sourceRoot = SourceRoot();
        var directory = Path.Combine(sourceRoot, relativeDirectory);
        Assert.True(Directory.Exists(directory), $"Directory not found: {directory}");

        var violations = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => fileNames.Contains(Path.GetFileName(file)))
            .SelectMany(file => ForbiddenReferencesInFile(sourceRoot, file, forbiddenReferences))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Forbidden architecture references found:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static void AssertNoSourceFilesNamed(
        string relativeDirectory,
        params string[] fileNames)
    {
        var sourceRoot = SourceRoot();
        var directory = Path.Combine(sourceRoot, relativeDirectory);
        Assert.True(Directory.Exists(directory), $"Directory not found: {directory}");

        var forbiddenNames = fileNames.ToHashSet(StringComparer.Ordinal);
        var violations = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => forbiddenNames.Contains(Path.GetFileName(file)))
            .Select(file => Path.GetRelativePath(sourceRoot, file))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Forbidden source files found:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> ForbiddenReferencesInFile(
        string sourceRoot,
        string file,
        IReadOnlyList<string> forbiddenReferences)
    {
        var text = File.ReadAllText(file);
        var relativePath = Path.GetRelativePath(sourceRoot, file);
        foreach (var forbidden in forbiddenReferences)
        {
            if (text.Contains(forbidden, StringComparison.Ordinal))
            {
                yield return $"{relativePath}: {forbidden}";
            }
        }
    }

    private static string SourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "OpenCodex.Api");
            if (Directory.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository root from {AppContext.BaseDirectory}");
    }
}
