using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OpenCodex.ProtocolConversion;
using Xunit;

namespace OpenCodex.Api.Tests;

[CollectionDefinition("Root request capacity", DisableParallelization = true)]
public sealed class RootRequestCapacityCollection;

/// <summary>Only a private output copy receives the small root P; the parent output is immutable.</summary>
internal static class RootRequestCapacityTestProcess
{
    private const string ManifestFileName = "root-request-capacity-child.json";
    private const string ChildRole = "root-request-capacity-child";

    public static async Task RunAsync(Type testClass, string testMethod, Func<int, CancellationToken, Task> childCase)
    {
        var sourceDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var entryPoint = testClass.FullName + "." + testMethod;
        var timeoutMilliseconds = ReadCaseTimeout(sourceDirectory);
        var manifestPath = Path.Combine(sourceDirectory, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            Assert.Equal(ChildRole, manifest.RootElement.GetProperty("role").GetString());
            Assert.Equal(entryPoint, manifest.RootElement.GetProperty("entryPoint").GetString());
            Assert.Equal(timeoutMilliseconds, manifest.RootElement.GetProperty("caseTimeoutMilliseconds").GetInt32());
            var expectedBytes = manifest.RootElement.GetProperty("expectedRequestBytes").GetInt32();
            Assert.True(expectedBytes > 0);
            Assert.Equal(expectedBytes, ProtocolConversionTestEnv.Read(sourceDirectory, "FlushBytes"));
            Assert.Equal((long)expectedBytes, ProtocolConversionTestEnv.Read(sourceDirectory, "MaxRequestBodyBytes"));
            Assert.Equal((long)expectedBytes, ProtocolConversionParameters.Current.MaxRequestBodyBytes);
            Assert.Equal(expectedBytes, ProtocolConversionParameters.Current.FlushBytes);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));
            await childCase(expectedBytes, cancellation.Token);
            return;
        }

        var originalEnv = File.ReadAllBytes(ProtocolConversionTestEnv.PathIn(sourceDirectory));
        var originalRequestBytes = ProtocolConversionTestEnv.Read(sourceDirectory, "MaxRequestBodyBytes");
        var isolatedRequestBytes = ProtocolConversionTestEnv.Read(sourceDirectory, "FlushBytes");
        Assert.True(isolatedRequestBytes > 0);
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ocxp-root-request-capacity-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            CopyOutput(sourceDirectory, directory);
            ProtocolConversionTestEnv.Write(directory, "MaxRequestBodyBytes", isolatedRequestBytes);
            // An explicit parent-created role/entry manifest is the only child-mode flag.
            // It is never an environment setting or a production capacity fallback.
            File.WriteAllText(Path.Combine(directory, ManifestFileName), JsonSerializer.Serialize(new
            {
                role = ChildRole, entryPoint, expectedRequestBytes = isolatedRequestBytes,
                originalRequestBytes, caseTimeoutMilliseconds = timeoutMilliseconds
            }));
            var assemblyName = Path.GetFileName(testClass.Assembly.Location);
            var results = Path.Combine(directory, "root-request-results");
            const string reportName = "root-request-capacity.trx";
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(Path.Combine(directory, assemblyName));
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + entryPoint);
            start.ArgumentList.Add("--Logger:trx;LogFileName=" + reportName);
            start.ArgumentList.Add("--ResultsDirectory:" + results);
            using var process = new Process { StartInfo = start };
            Assert.True(process.Start());
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("The isolated root request-capacity child exceeded the public CaseTimeoutMilliseconds.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                await Task.WhenAll(stdout, stderr);
            }
            var output = await stdout + Environment.NewLine + await stderr;
            Assert.True(process.ExitCode == 0, output);
            AssertExecutedChildFact(Path.Combine(results, reportName), testClass, testMethod);
            Assert.Equal((long)isolatedRequestBytes, ProtocolConversionTestEnv.Read(directory, "MaxRequestBodyBytes"));
        }
        finally
        {
            try
            {
                // The parent output is immutable; the isolated child only ever used its copy.
                Assert.Equal(originalEnv, File.ReadAllBytes(ProtocolConversionTestEnv.PathIn(sourceDirectory)));
            }
            finally
            {
                // Only this parent created/populated directory is removed, after child exit.
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
    }

    public static byte[] JsonBodyAtBytes(int bytes, string model)
    {
        var original = JsonSerializer.SerializeToUtf8Bytes(new { model, input = "hello" });
        Assert.True(bytes >= original.Length, "The explicit root FlushBytes must fit the JSON fixture.");
        var result = new byte[bytes];
        original.CopyTo(result, 0);
        result.AsSpan(original.Length).Fill((byte)' ');
        Assert.Equal(bytes, result.Length);
        return result;
    }

    private static int ReadCaseTimeout(string directory)
    {
        _ = directory;
        var value = WsHistoryTestParameters.CaseTimeoutMilliseconds;
        if (value <= 0) throw new InvalidOperationException("CaseTimeoutMilliseconds must be positive.");
        return value;
    }

    private static void AssertExecutedChildFact(string reportPath, Type testClass, string testMethod)
    {
        var report = XDocument.Load(reportPath);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var counters = Assert.Single(report.Descendants(ns + "Counters"));
        Assert.Equal("1", counters.Attribute("executed")!.Value);
        Assert.Equal("1", counters.Attribute("passed")!.Value);
        Assert.Equal("0", counters.Attribute("failed")!.Value);
        var result = Assert.Single(report.Descendants(ns + "UnitTestResult"));
        Assert.Equal("Passed", result.Attribute("outcome")!.Value);
        var definition = Assert.Single(report.Descendants(ns + "UnitTest"));
        Assert.Equal(definition.Attribute("id")!.Value, result.Attribute("testId")!.Value);
        var method = Assert.Single(definition.Descendants(ns + "TestMethod"));
        Assert.Equal(testMethod, method.Attribute("name")!.Value);
        var className = method.Attribute("className")!.Value.Split(',', 2)[0];
        Assert.Equal(testClass.FullName, className);
    }

    private static void CopyOutput(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var child in Directory.EnumerateDirectories(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(child));
            Directory.CreateDirectory(target);
            CopyOutput(child, target);
        }
    }
}
