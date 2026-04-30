using System.Text.Json;
using Forge.Cli.Doctor;
using Forge.Core.Json;
using Spectre.Console;

namespace Forge.Cli.Commands;

internal static class DoctorCommand
{
  public static async Task<int> ExecuteAsync(DoctorSettings settings, ForgeAppState appState)
  {
    var report = await DoctorRunner.RunAsync(
      appState.ForgeHome,
      settings.Probe,
      appState.CancellationToken).ConfigureAwait(false);

    if (settings.Human)
    {
      RenderHumanTable(report);
    }
    else
    {
      var json = JsonSerializer.Serialize(report, JsonSerializationDefaults.Indented);
      Console.WriteLine(json);
    }

    return report.HardFailures > 0 ? 1 : 0;
  }

  private static void RenderHumanTable(DoctorReport report)
  {
    var table = new Table();
    table.AddColumn("Check");
    table.AddColumn("Status");
    table.AddColumn("Detail");

    foreach (var check in report.Checks)
    {
      var statusColor = check.Status switch
      {
        "ok" => "green",
        "warn" => "yellow",
        "fail" => "red",
        "skip" => "grey",
        _ => "white"
      };

      var statusLabel = check.Status switch
      {
        "ok" => $"[{statusColor}]\u2713 {check.Status}[/]",
        "warn" => $"[{statusColor}]\u26A0 {check.Status}[/]",
        "fail" => $"[{statusColor}]\u2717 {check.Status}[/]",
        "skip" => $"[{statusColor}]- {check.Status}[/]",
        _ => $"[{statusColor}]{check.Status}[/]"
      };

      var detail = check.Detail ?? "";
      if (check.FixHint is not null && check.Status is "fail" or "warn")
      {
        detail += $"\n  [italic]Fix: {check.FixHint}[/]";
      }

      table.AddRow(
        $"{check.Title} [grey]({check.Id})[/]{(check.Required ? " [red]*[/]" : "")}",
        statusLabel,
        detail);
    }

    AnsiConsole.Write(table);

    var summary = report.HardFailures > 0
      ? $"[red]{report.HardFailures} hard failure(s), {report.Warnings} warning(s)[/]"
      : report.Warnings > 0
        ? $"[yellow]{report.Warnings} warning(s), no hard failures[/]"
        : "[green]All checks passed[/]";

    AnsiConsole.MarkupLine($"\n{summary}");
  }
}
