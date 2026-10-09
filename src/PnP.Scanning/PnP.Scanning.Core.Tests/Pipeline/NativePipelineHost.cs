#nullable enable
using System.Net;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AuthenticationManager = PnP.Scanning.Core.Authentication.AuthenticationManager;
using PnP.Scanning.Core.Pipeline.Collection;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage.Pipeline;
using PnP.Scanning.Process;
using PnP.Scanning.Process.Commands;
using PnP.Scanning.Process.Services;

namespace PnP.Scanning.Core.Tests.Pipeline;

internal sealed class NativePipelineHost : IAsyncDisposable
{
    private readonly IHost host;
    private readonly GrpcChannel channel;
    private NativePipelineHost(IHost host, string address, ForbiddenOnlineEnvironment environment,
        PipelineCoordinator? coordinator, LegacyOnlyScanner? legacy, ResolutionCounter counter)
    {
        this.host = host;
        channel = GrpcChannel.ForAddress(address);
        Client = new PnPScanner.PnPScannerClient(channel);
        Coordinator = coordinator;
        Legacy = legacy;
        Counter = counter;
        var config = new ConfigurationOptions { Port = new Uri(address).Port };
        var manager = new ScannerManager(config);
        Parser = new CommandLineBuilder(new RootCommandHandler(manager, environment, config).Create()).UseDefaults().Build();
    }

    internal PnPScanner.PnPScannerClient Client { get; }
    internal PipelineCoordinator? Coordinator { get; }
    internal LegacyOnlyScanner? Legacy { get; }
    internal Parser Parser { get; }
    internal ResolutionCounter Counter { get; }

    internal static async Task<NativePipelineHost> StartAsync(PipelineStore store, ModuleRegistry registry,
        ForbiddenOnlineEnvironment environment, bool legacyOnly = false)
    {
        var coordinator = legacyOnly ? null : new PipelineCoordinator(store, registry, environment);
        var legacy = legacyOnly ? new LegacyOnlyScanner() : null;
        var counter = new ResolutionCounter();
        var host = Host.CreateDefaultBuilder().ConfigureLogging(x => x.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                if (legacyOnly) web.UseStartup<Startup<LegacyOnlyScanner>>();
                else web.UseStartup<Startup<Scanner>>();
                web.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IDataProtectionProvider>(environment);
                    if (legacyOnly) services.AddSingleton(legacy!);
                    else
                    {
                        services.AddSingleton(store);
                        services.AddSingleton(registry);
                        services.AddSingleton<ICollectionEnvironment>(environment);
                        services.AddSingleton(coordinator!);
                        services.AddSingleton<IHostedService>(coordinator!);
                        services.AddSingleton<Scanner>();
                        services.AddSingleton<global::PnP.Core.Services.IPnPContextFactory>(_ => counter.Reject<global::PnP.Core.Services.IPnPContextFactory>());
                        services.AddSingleton<AuthenticationManager>(_ => counter.Reject<AuthenticationManager>());
                        services.AddSingleton<ScanManager>(_ => counter.Reject<ScanManager>());
                        services.AddSingleton<SiteEnumerationManager>(_ => counter.Reject<SiteEnumerationManager>());
                    }
                });
            }).Build();
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new NativePipelineHost(host, address, environment, coordinator, legacy, counter);
    }

    internal async Task<(int ExitCode, PhaseReply? Ticket, string Output, string Error)> InvokeAsync(params string[] arguments)
    {
        // Initialize Spectre against the stable test-runner writer before temporarily
        // redirecting Console. Its singleton must not retain a disposed capture writer.
        _ = Spectre.Console.AnsiConsole.Console;
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Parser.InvokeAsync(arguments, new CaptureConsole(output, error));
            var line = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(x => x.TrimStart().StartsWith("{", StringComparison.Ordinal));
            var ticket = line == null ? null : JsonParser.Default.Parse<PhaseReply>(line);
            return (exitCode, ticket, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private sealed class CaptureConsole(TextWriter output, TextWriter error) : System.CommandLine.IConsole
    {
        public System.CommandLine.IO.IStandardStreamWriter Out { get; } = new CaptureWriter(output);
        public System.CommandLine.IO.IStandardStreamWriter Error { get; } = new CaptureWriter(error);
        public bool IsOutputRedirected => true;
        public bool IsErrorRedirected => true;
        public bool IsInputRedirected => true;
        private sealed class CaptureWriter(TextWriter writer) : System.CommandLine.IO.IStandardStreamWriter
        {
            public void Write(string? value) => writer.Write(value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        channel.Dispose();
        await host.StopAsync();
        host.Dispose();
    }

    internal sealed class ResolutionCounter
    {
        internal int Calls;
        internal T Reject<T>()
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException($"Forbidden legacy/online dependency resolution: {typeof(T).Name}");
        }
    }
}

internal sealed class LegacyOnlyScanner : PnPScanner.PnPScannerBase
{
    internal int StartCalls;
    internal int StopCalls;
    public override Task<PingReply> Ping(Empty request, ServerCallContext context) =>
        Task.FromResult(new PingReply { UpAndRunning = true, ProcessId = Environment.ProcessId });
    public override Task Start(StartRequest request, IServerStreamWriter<StartStatus> responseStream, ServerCallContext context)
    {
        Interlocked.Increment(ref StartCalls);
        return responseStream.WriteAsync(new StartStatus { Status = "legacy start was called" });
    }
    public override Task<Empty> Stop(StopRequest request, ServerCallContext context)
    {
        Interlocked.Increment(ref StopCalls);
        return Task.FromResult(new Empty());
    }
}
