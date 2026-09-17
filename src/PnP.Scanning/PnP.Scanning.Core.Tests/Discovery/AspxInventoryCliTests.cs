using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Process.Commands;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class AspxInventoryCliTests
{
    [Fact]
    public async Task Cli_binds_mode_input_output_and_resume()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.Write("input.json", "{}");
        var manifest = directory.Write("manifest.json", "{}");
        AspxInventoryCliOptions captured = null;
        var command = AspxInventoryCommandDefinition.Create((options, _) =>
        {
            captured = options;
            return Task.FromResult(0);
        });
        var parser = new CommandLineBuilder(command).UseDefaults().Build();
        var resume = Guid.NewGuid();

        var exit = await parser.InvokeAsync(new[]
        {
            "--input", input,
            "--manifest", manifest,
            "--database", directory.File("inventory.sqlite"),
            "--output", directory.File("output.json"),
            "--resume-run-id", resume.ToString("D"),
        });

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured.Mode.Should().Be(Mode.AspxInventory);
        captured.ResumeRunId.Should().Be(resume);
        captured.Input.FullName.Should().EndWith("input.json");
        captured.Database.FullName.Should().EndWith("inventory.sqlite");
        captured.Output.FullName.Should().EndWith("output.json");
    }

    [Fact]
    public async Task Real_cli_handler_runs_offline_source_and_writes_sqlite_and_versioned_output()
    {
        using var directory = new TemporaryDirectory();
        var inputJson = JsonSerializer.Serialize(
            AspxDiscoveryOrchestrationTests.CompleteInput(), AspxInventoryRuntime.JsonOptions(indented: true));
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputJson))).ToLowerInvariant();
        var input = directory.Write("input.json", inputJson);
        var manifest = directory.Write("manifest.json", JsonSerializer.Serialize(
            AspxDiscoveryOrchestrationTests.Manifest() with { InputManifestHash = inputHash, FixtureHash = inputHash },
            AspxInventoryRuntime.JsonOptions(indented: true)));
        var database = directory.File("inventory.sqlite");
        var output = directory.File("output.json");
        var parser = new CommandLineBuilder(new AspxInventoryCommandHandler().Create()).UseDefaults().Build();

        var exit = await parser.InvokeAsync(new[]
        {
            "--input", input,
            "--manifest", manifest,
            "--database", database,
            "--output", output,
        });

        exit.Should().Be(0);
        File.Exists(database).Should().BeTrue();
        File.Exists(output).Should().BeTrue();
        var gapCsv = output + ".gaps.csv";
        File.Exists(gapCsv).Should().BeTrue();
        (await File.ReadAllLinesAsync(gapCsv)).Should().StartWith(
            "ScopeKey,SourceKind,GapCode,Detail,Resolved");
        var json = await File.ReadAllTextAsync(output);
        json.Should().Contain("\"outputVersion\": \"classic-page-discovery-output/v3\"");
        json.Should().Contain("\"coverageVerdict\": \"CompleteAuthorizedSurface\"");
        using var document = JsonDocument.Parse(json);
        var inventoryNames = document.RootElement.GetProperty("inventory").EnumerateArray()
            .Select(row => row.GetProperty("fileName").GetString()).ToArray();
        inventoryNames.Should().Contain("Upper.ASPX");
        inventoryNames.Should().NotContain("not-a-page.aspx.bak");
    }

    [Fact]
    public async Task Cli_rejects_existing_database_without_resume_before_execution()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.Write("input.json", "{}");
        var manifest = directory.Write("manifest.json", "{}");
        var database = directory.Write("inventory.sqlite", "not-a-ledger");
        var executed = false;
        var command = AspxInventoryCommandDefinition.Create((_, _) =>
        {
            executed = true;
            return Task.FromResult(0);
        });
        var parser = new CommandLineBuilder(command).UseDefaults().Build();

        var exit = await parser.InvokeAsync(new[]
        {
            "--input", input,
            "--manifest", manifest,
            "--database", database,
            "--output", directory.File("output.json"),
        });

        exit.Should().NotBe(0);
        executed.Should().BeFalse();
    }

    [Fact]
    public async Task Runtime_failure_returns_exit_one_without_claiming_success()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.Write("input.json", "{}");
        var manifest = directory.Write("manifest.json", "{}");
        var parser = new CommandLineBuilder(new AspxInventoryCommandHandler().Create()).UseDefaults().Build();

        var exit = await parser.InvokeAsync(new[]
        {
            "--input", input,
            "--manifest", manifest,
            "--database", directory.File("inventory.sqlite"),
            "--output", directory.File("output.json"),
        });

        exit.Should().Be(1);
        File.Exists(directory.File("output.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Cli_rejects_input_bytes_that_do_not_match_immutable_manifest_hash()
    {
        using var directory = new TemporaryDirectory();
        var inputJson = JsonSerializer.Serialize(
            AspxDiscoveryOrchestrationTests.CompleteInput(), AspxInventoryRuntime.JsonOptions(indented: true));
        var input = directory.Write("input.json", inputJson);
        var manifest = directory.Write("manifest.json", JsonSerializer.Serialize(
            AspxDiscoveryOrchestrationTests.Manifest(), AspxInventoryRuntime.JsonOptions(indented: true)));
        var parser = new CommandLineBuilder(new AspxInventoryCommandHandler().Create()).UseDefaults().Build();

        var exit = await parser.InvokeAsync(new[]
        {
            "--input", input,
            "--manifest", manifest,
            "--database", directory.File("inventory.sqlite"),
            "--output", directory.File("output.json"),
        });

        exit.Should().Be(1);
        File.Exists(directory.File("inventory.sqlite")).Should().BeFalse();
        File.Exists(directory.File("output.json")).Should().BeFalse();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aspx-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }
        internal string File(string name) => System.IO.Path.Combine(Path, name);
        internal string Write(string name, string content)
        {
            var path = File(name);
            System.IO.File.WriteAllText(path, content);
            return path;
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
