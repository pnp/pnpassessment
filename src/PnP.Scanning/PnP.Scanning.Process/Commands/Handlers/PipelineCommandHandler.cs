using Google.Protobuf;
using Grpc.Core;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Process.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands;

internal static class PipelineCommandHandler
{
    internal static Command Analyze(ScannerManager manager)
    {
        var command = new Command("analyze", "Analyzes one explicitly selected sealed snapshot without authentication");
        var id = new Option<Guid>("--id", "Assessment ID") { IsRequired = true };
        var snapshot = new Option<Guid>("--snapshot-id", "Sealed source snapshot ID") { IsRequired = true };
        var rule = new Option<string>("--rule-version", "Analysis rule version; defaults to the registered current version");
        var parameters = new Option<string>("--parameters", () => VersionedJson.Empty.Json, "Versioned analysis parameters as JSON");
        var threads = new Option<int>("--threads", "Optional execution concurrency; defaults to the assessment's stored value");
        command.AddOption(id);
        command.AddOption(snapshot);
        command.AddOption(rule);
        command.AddOption(parameters);
        command.AddOption(threads);
        command.SetHandler(async context =>
        {
            var parse = context.ParseResult;
            context.ExitCode = await ExecuteAsync(manager, client => client.AnalyzeAsync(new AnalyzeRequest
            {
                Id = parse.GetValueForOption(id).ToString(), SnapshotId = parse.GetValueForOption(snapshot).ToString(),
                RuleVersion = parse.GetValueForOption(rule) ?? "", ParametersJson = parse.GetValueForOption(parameters),
                Threads = parse.GetValueForOption(threads),
            }));
        });
        return command;
    }

    internal static Task<int> RunCollectionAsync(ScannerManager manager, CollectRequest request, bool collectOnly,
        string ruleVersion, string analysisParameters) => ExecuteAsync(manager, client => collectOnly
            ? client.CollectAsync(request)
            : client.StartPipelineAsync(new StartPipelineRequest
            {
                Collection = request, RuleVersion = ruleVersion ?? "",
                AnalysisParametersJson = analysisParameters ?? VersionedJson.Empty.Json,
            }));

    private static async Task<int> ExecuteAsync(ScannerManager manager,
        Func<PnPScanner.PnPScannerClient, AsyncUnaryCall<PhaseReply>> call)
    {
        try
        {
            var client = await manager.GetScannerClientAsync();
            using var operation = call(client);
            var reply = await operation.ResponseAsync;
            Console.WriteLine(JsonFormatter.Default.Format(reply));
            return 0;
        }
        catch (RpcException ex)
        {
            Console.Error.WriteLine(ex.StatusCode == StatusCode.Unimplemented
                ? "The running assessment service does not support collection/analysis pipeline RPCs. Restart it using this version of the tool."
                : ex.Status.Detail);
            return 1;
        }
    }
}
