using Forge.Cli.Commands;
using Forge.Llm;
using Forge.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Forge.Cli;

internal static class Program
{
  public static async Task<int> Main(string[] args)
  {
    Forge.Core.Filesystem.RuntimePaths.ProcessStartedDirectory = Path.GetFullPath(Environment.CurrentDirectory);
    var ver = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    if (args.Length > 0 && args[0] is "--version" or "-v" && args.Length == 1)
    {
      Console.WriteLine($"forge {ver}");
      return 0;
    }

    var forgeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".forge");
    Directory.CreateDirectory(forgeHome);

    using var cts = new CancellationTokenSource();
    var cancelCount = 0;
    Console.CancelKeyPress += (_, e) =>
    {
      if (Interlocked.Increment(ref cancelCount) == 1)
      {
        e.Cancel = true;
        TryCancel(cts);
      }
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => TryCancel(cts);

    var appState = new ForgeAppState { ForgeHome = forgeHome, CancellationToken = cts.Token };

    var services = new ServiceCollection();
    services.AddSingleton(appState);
    services.AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));
    var llmCfg = LiteLlmConfig.Load(forgeHome);
    services.AddSingleton(llmCfg);
    services.AddLiteLlm(llmCfg);
    services.AddForgeBuiltInTools();

    await using var sp = services.BuildServiceProvider();

    var app = new CommandApp();
    app.Configure(config =>
    {
      config.SetApplicationName("forge");
      config.Settings.ApplicationVersion = ver;
      config.AddAsyncDelegate<AgentSettings>("agent", async (ctx, s) => await AgentCommand.ExecuteAsync(sp, s).ConfigureAwait(false))
        .WithDescription("Run a configured agent (YAML under .forge/agents).");
      config.AddDelegate<ListSettings>("list", (ctx, s) => ListCommand.Execute(sp, s))
        .WithDescription("List agents or built-in tools.");
      config.AddDelegate<ValidateSettings>("validate", (ctx, s) => ValidateCommand.Execute(s))
        .WithDescription("Validate an agent YAML file.");
      config.AddDelegate<DescribeSettings>("describe", (ctx, s) => DescribeCommand.Execute(sp, s))
        .WithDescription("Print JSON metadata for an agent or tool.");
      config.AddAsyncDelegate<ResumeSettings>("resume", async (ctx, s) => await ResumeCommand.ExecuteAsync(sp, s).ConfigureAwait(false))
        .WithDescription("Resume a previously started agent run from the last completed iteration.");
      config.AddAsyncDelegate<InitSettings>("init", async (ctx, s) => await InitCommand.ExecuteAsync(s, forgeHome).ConfigureAwait(false))
        .WithDescription("Scaffold ~/.forge/llm.json and optional example files.");
      config.AddAsyncDelegate<DoctorSettings>("doctor", async (ctx, s) => await DoctorCommand.ExecuteAsync(s, appState).ConfigureAwait(false))
        .WithDescription("Diagnose a Forge install (config, plugins, endpoint).");

      config.AddBranch<RunsBranchSettings>("runs", runs =>
      {
        runs.SetDescription("List and inspect run directories under ~/.forge/runs/.");
        runs.AddAsyncDelegate<RunsListSettings>("list", async (ctx, s) => await RunsSubcommands.ListAsync(sp, s).ConfigureAwait(false))
          .WithDescription("List recent runs (metadata from status.json when present).");
        runs.AddAsyncDelegate<RunsShowSettings>("show", async (ctx, s) => await RunsSubcommands.ShowAsync(sp, s).ConfigureAwait(false))
          .WithDescription("Show input/status/result for a run; optional trace tail.");
      });
    });

    try
    {
      return await app.RunAsync(args).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      var rendered = ExitCodeMapper.RenderStderr(ex);
      if (rendered is not null) Console.Error.WriteLine(rendered);
      return ExitCodeMapper.ExitCodeFor(ex);
    }
  }

  private static void TryCancel(CancellationTokenSource cts)
  {
    try
    {
      if (!cts.IsCancellationRequested) cts.Cancel();
    }
    catch (ObjectDisposedException)
    {
      // CTS already disposed after main returned — nothing to cancel.
    }
  }
}
