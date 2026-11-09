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
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage.Pipeline;
using PnP.Scanning.Process;
using PnP.Scanning.Process.Commands;
using PnP.Scanning.Process.Services;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Hosts the actual CLI and loopback RPC service with recorded modules and forbidden-online counters.
/// </summary>
internal sealed class NativePipelineHost : IAsyncDisposable
{
    private readonly IHost host;
    private readonly GrpcChannel channel;

    /// <summary>
    /// Initializes Native Pipeline Host with the supplied dependencies and fixed scope.
    /// </summary>
    private NativePipelineHost(
        IHost host,
        string address,
        ForbiddenOnlineEnvironment environment,
        PipelineCoordinator? coordinator,
        LegacyOnlyScanner? legacy,
        ResolutionCounter counter)
    {
        this.host = host;
        channel = GrpcChannel.ForAddress(address);
        Client = new PnPScanner.PnPScannerClient(channel);
        Coordinator = coordinator;
        Legacy = legacy;
        Counter = counter;
        ConfigurationOptions? config = new ConfigurationOptions { Port = new Uri(address).Port };
        ScannerManager? manager = new ScannerManager(config);
        Parser = new CommandLineBuilder(
                new RootCommandHandler(manager, environment, config, _ => Task.CompletedTask).Create()).UseDefaults()
            .Build();
    }

    internal PnPScanner.PnPScannerClient Client { get; }
    internal PipelineCoordinator? Coordinator { get; }
    internal LegacyOnlyScanner? Legacy { get; }
    internal Parser Parser { get; }
    internal ResolutionCounter Counter { get; }

    /// <summary>
    /// Starts the real loopback RPC service with recorded modules and dependency-rejection counters.
    /// </summary>
    internal static async Task<NativePipelineHost> StartAsync(
        PipelineStore store,
        ModuleRegistry registry,
        ForbiddenOnlineEnvironment environment,
        bool legacyOnly = false)
    {
        PipelineCoordinator? coordinator = legacyOnly ? null : new PipelineCoordinator(store, registry, environment);
        LegacyOnlyScanner? legacy = legacyOnly ? new LegacyOnlyScanner() : null;
        ResolutionCounter? counter = new ResolutionCounter();
        IHost? host = Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                if (legacyOnly)
                    web.UseStartup<Startup<LegacyOnlyScanner>>();
                else
                    web.UseStartup<Startup<Scanner>>();
                web.ConfigureKestrel(options => options.Listen(
                    IPAddress.Loopback,
                    0,
                    listen => listen.Protocols = HttpProtocols.Http2));
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IDataProtectionProvider>(environment);
                    if (legacyOnly)
                        services.AddSingleton(legacy!);
                    else
                    {
                        services.AddSingleton(store);
                        services.AddSingleton(registry);
                        services.AddSingleton<ICollectionEnvironment>(environment);
                        services.AddSingleton(coordinator!);
                        services.AddSingleton<IHostedService>(coordinator!);
                        services.AddSingleton<Scanner>();
                        services.AddSingleton<ReportManager>();
                        services.AddSingleton<global::PnP.Core.Services.IPnPContextFactory>(_ =>
                            counter.Reject<global::PnP.Core.Services.IPnPContextFactory>());
                        services.AddSingleton<AuthenticationManager>(_ => counter.Reject<AuthenticationManager>());
                        services.AddSingleton<ScanManager>(_ => counter.Reject<ScanManager>());
                        services.AddSingleton<SiteEnumerationManager>(_ => counter.Reject<SiteEnumerationManager>());
                    }
                });
            })
            .Build();
        await host.StartAsync();
        string? address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Single();
        return new NativePipelineHost(host, address, environment, coordinator, legacy, counter);
    }

    /// <summary>
    /// Executes the actual CLI parser and captures its exit code, output, and errors.
    /// </summary>
    internal async Task<(int ExitCode, PhaseReply? Ticket, string Output, string Error)> InvokeAsync(
        params string[] arguments)
    {
        // Initialize Spectre against the stable test-runner writer before temporarily
        // redirecting Console. Its singleton must not retain a disposed capture writer.
        _ = Spectre.Console.AnsiConsole.Console;
        TextWriter? previousOut = Console.Out;
        TextWriter? previousError = Console.Error;
        using StringWriter? output = new StringWriter();
        using StringWriter? error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int exitCode = await Parser.InvokeAsync(arguments, new CaptureConsole(output, error));
            PhaseReply? ticket = null;
            return (exitCode, ticket, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    /// <summary>
    /// Captures CLI output and error streams without invoking external processes.
    /// </summary>
    private sealed class CaptureConsole(TextWriter output, TextWriter error) : System.CommandLine.IConsole
    {
        public System.CommandLine.IO.IStandardStreamWriter Out { get; } = new CaptureWriter(output);
        public System.CommandLine.IO.IStandardStreamWriter Error { get; } = new CaptureWriter(error);
        public bool IsOutputRedirected => true;
        public bool IsErrorRedirected => true;
        public bool IsInputRedirected => true;

        /// <summary>
        /// Adapts recorded output writers to the command-line console interface.
        /// </summary>
        private sealed class CaptureWriter(TextWriter writer) : System.CommandLine.IO.IStandardStreamWriter
        {
            /// <summary>
            /// Appends CLI text to the fixture's recorded output stream.
            /// </summary>
            public void Write(string? value) => writer.Write(value);
        }
    }

    /// <summary>
    /// Stops and disposes the owned loopback host and client channel.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        channel.Dispose();
        await host.StopAsync();
        host.Dispose();
    }

    /// <summary>
    /// Counts and rejects unexpected construction of online or legacy services.
    /// </summary>
    internal sealed class ResolutionCounter
    {
        internal int Calls;

        /// <summary>
        /// Counts and rejects forbidden online or legacy dependency resolution.
        /// </summary>
        internal T Reject<T>()
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException($"Forbidden legacy/online dependency resolution: {typeof(T).Name}");
        }
    }
}

/// <summary>
/// Provides an existing-RPC fixture without internal pipeline-specific entry points.
/// </summary>
internal sealed class LegacyOnlyScanner : PnPScanner.PnPScannerBase
{
    internal int StartCalls;
    internal int StopCalls;

    /// <summary>
    /// Reports the recorded fixture's process availability.
    /// </summary>
    public override Task<PingReply> Ping(Empty request, ServerCallContext context) =>
        Task.FromResult(new PingReply { UpAndRunning = true, ProcessId = Environment.ProcessId });

    /// <summary>
    /// Records the existing Start RPC call.
    /// </summary>
    public override Task Start(
        StartRequest request,
        IServerStreamWriter<StartStatus> responseStream,
        ServerCallContext context)
    {
        Interlocked.Increment(ref StartCalls);
        return responseStream.WriteAsync(new StartStatus { Status = "legacy start was called" });
    }

    /// <summary>
    /// Records the existing Stop RPC call without changing a real host.
    /// </summary>
    public override Task<Empty> Stop(StopRequest request, ServerCallContext context)
    {
        Interlocked.Increment(ref StopCalls);
        return Task.FromResult(new Empty());
    }
}