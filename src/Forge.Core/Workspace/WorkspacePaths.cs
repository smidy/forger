namespace Forge.Core.Workspace;

public static class WorkspacePaths
{
  public static string RunRoot(string forgeHome, string runId) =>
    Path.Combine(forgeHome, "runs", runId);

  public static string StageDir(string runRoot, string stageId) =>
    Path.Combine(runRoot, "stages", stageId);

  public static string IterationDir(string stageDir, int index) =>
    Path.Combine(stageDir, "iterations", index.ToString("D3"));

  public static string ToolOutputsDir(string stageDir) =>
    Path.Combine(stageDir, "tool-outputs");

  public static string TracePath(string runRoot) =>
    Path.Combine(runRoot, "trace.jsonl");

  public static string InputPath(string runRoot) =>
    Path.Combine(runRoot, "input.json");

  public static string PlanPath(string runRoot) =>
    Path.Combine(runRoot, "plan.json");

  public static string ResultPath(string runRoot) =>
    Path.Combine(runRoot, "result.json");

  public static string StatusPath(string runRoot) =>
    Path.Combine(runRoot, "status.json");

  public static string StageOutputPath(string stageDir) =>
    Path.Combine(stageDir, "output.json");

  /// <summary>
  /// Path to the pending-question artifact written when a headless caller defers.
  /// </summary>
  public static string PendingQuestionPath(string stageDir) =>
    Path.Combine(stageDir, "pending_question.json");
}
