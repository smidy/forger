using System.Text.Json.Nodes;
using Forge.Core.Json;
using Forge.Pipeline;
using Spectre.Console;

namespace Forge.Cli;

internal static class HumanRenderer
{
  public static void RenderResumeResult(PipelineResult result, string agentName, string runId, TimeSpan elapsed)
  {
    AnsiConsole.Write(new Rule($"[bold green]Resumed {Markup.Escape(agentName)} ({Markup.Escape(runId)})[/] [grey]{elapsed.TotalSeconds:F1}s[/]").LeftJustified());
    WriteJsonPanel("result", result.Result);
  }

  public static void RenderAgentResult(JsonNode result, string agentName, string runId, TimeSpan elapsed)
  {
    AnsiConsole.Write(new Rule($"[bold green]Agent {Markup.Escape(agentName)} completed[/] [grey]{Markup.Escape(runId)} · {elapsed.TotalSeconds:F1}s[/]").LeftJustified());
    WriteJsonPanel("output", result);
  }

  public static void RenderRunsList(JsonArray rows)
  {
    if (rows.Count == 0)
    {
      AnsiConsole.MarkupLine("[grey]No runs.[/]");
      return;
    }

    var table = new Table().RoundedBorder();
    table.AddColumn("Run ID");
    table.AddColumn("Status");
    table.AddColumn("Name");
    table.AddColumn("Path");

    foreach (var node in rows)
    {
      if (node is not JsonObject row) continue;
      var runId = row["run_id"]?.GetValue<string>() ?? "";
      var path = row["path"]?.GetValue<string>() ?? "";
      var status = "-";
      var name = "-";
      if (row["status"] is JsonObject so)
      {
        status = so["status"]?.GetValue<string>() ?? "-";
        name = so["agent"]?.GetValue<string>() ?? "-";
      }
      else if (row["status"]?.GetValue<string>() == "unreadable")
      {
        status = "unreadable";
      }

      table.AddRow(Markup.Escape(runId), FormatStatus(status), Markup.Escape(name), Markup.Escape(path));
    }

    AnsiConsole.Write(table);
  }

  public static void RenderRunDetail(JsonObject doc)
  {
    var runId = doc["run_id"]?.GetValue<string>() ?? "";
    AnsiConsole.Write(new Rule($"[bold]Run {Markup.Escape(runId)}[/]").LeftJustified());

    if (doc["path"] is JsonNode p)
    {
      AnsiConsole.MarkupLine($"[grey]path:[/] {Markup.Escape(p.GetValue<string>())}");
    }

    if (doc["status"] is JsonNode status) WriteJsonPanel("status", status);
    if (doc["input"] is JsonNode input) WriteJsonPanel("input", input);
    if (doc["result"] is JsonNode result) WriteJsonPanel("result", result);

    if (doc["trace_tail"] is JsonNode trace)
    {
      var text = trace.GetValue<string>();
      AnsiConsole.Write(new Panel(Markup.Escape(text)).Header("trace (tail)").RoundedBorder());
    }
    else if (doc["trace_path"] is JsonNode tracePath)
    {
      AnsiConsole.MarkupLine($"[grey]trace:[/] {Markup.Escape(tracePath.GetValue<string>())}");
    }
  }

  private static void WriteJsonPanel(string header, JsonNode node)
  {
    var text = node.ToJsonString(JsonSerializationDefaults.Indented);
    AnsiConsole.Write(new Panel(Markup.Escape(text)).Header(header).RoundedBorder());
  }

  private static string FormatStatus(string status) => status switch
  {
    "completed" => "[green]completed[/]",
    "running" => "[yellow]running[/]",
    "failed" => "[red]failed[/]",
    "partial" => "[yellow]partial[/]",
    "cancelled" => "[grey]cancelled[/]",
    _ => Markup.Escape(status),
  };
}
