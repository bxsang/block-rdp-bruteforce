using System.Runtime.Versioning;
using BlockRdpBruteForce;
using BlockRdpBruteForce.Cli;
using BlockRdpBruteForce.Configuration;
using BlockRdpBruteForce.Firewall;
using BlockRdpBruteForce.Ipc;
using BlockRdpBruteForce.Logging;
using BlockRdpBruteForce.State;
using BlockRdpBruteForce.Unblocking;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("BlockRdpBruteForce requires Windows.");
    return 1;
}

if (CliDispatcher.IsCliInvocation(args))
    return CliDispatcher.Run(args);

return RunService(args);

[SupportedOSPlatform("windows")]
static int RunService(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    var programData = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
    var overridePath = Path.Combine(programData, "BlockRdpBruteForce", "appsettings.json");
    builder.Configuration.AddJsonFile(overridePath, optional: true, reloadOnChange: true);

    builder.Services.Configure<AppOptions>(
        builder.Configuration.GetSection(AppOptions.SectionName));

    ConfigureSerilog(builder);

    builder.Services.AddSingleton<StateStore>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<AppOptions>>().Value;
        return new StateStore(opts.StateFilePath);
    });
    builder.Services.AddSingleton<IFirewallManager, FirewallManager>();
    builder.Services.AddSingleton<SemaphoreSlim>(_ => new SemaphoreSlim(1, 1));
    builder.Services.AddSingleton<FirewallRuleSync>();
    builder.Services.AddSingleton<UnblockScheduler>();

#pragma warning disable CA1416 // RunService is gated on OperatingSystem.IsWindows() at the call site
    builder.Services.AddSingleton<Worker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());
    builder.Services.AddSingleton<IPipeOps>(sp => sp.GetRequiredService<Worker>());
    builder.Services.AddHostedService<PipeServer>();
#pragma warning restore CA1416

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "BlockRdpBruteForce";
    });

    try
    {
        var host = builder.Build();
        host.Run();
        return 0;
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Host terminated unexpectedly");
        return 1;
    }
    finally
    {
        Log.CloseAndFlush();
    }
}

[SupportedOSPlatform("windows")]
static void ConfigureSerilog(HostApplicationBuilder builder)
{
    var section = builder.Configuration.GetSection(AppOptions.SectionName);
    var rawLogPath = section["LogPath"] ?? @"%ProgramData%\BlockRdpBruteForce\logs\service-.log";
    var logPath = Environment.ExpandEnvironmentVariables(rawLogPath);
    var logDir = Path.GetDirectoryName(logPath);
    if (!string.IsNullOrEmpty(logDir))
    {
        try { Directory.CreateDirectory(logDir); }
        catch { }
    }

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(logPath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true,
            outputTemplate:
                "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .WriteTo.WindowsEventLog("BlockRdpBruteForce",
            logName: "Application",
            restrictedToMinimumLevel: LogEventLevel.Warning)
        .CreateLogger();

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();
}
