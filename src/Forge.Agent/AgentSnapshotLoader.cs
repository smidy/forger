using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Json;
using Forge.Core.Types;
using Forge.Core.Workspace;

namespace Forge.Agent;

/// <summary>
/// Scans a stage directory for agent state snapshots (<c>iterations/NNN/state.json</c>)
/// and loads the latest valid snapshot for resuming.
/// </summary>
public static class AgentSnapshotLoader
{
    /// <summary>
    /// Attempts to load the latest agent snapshot for a non-fan-out stage.
    /// Scans <c>stageDir/iterations/*/state.json</c>, picks the highest iteration index
    /// that parses cleanly, and returns an <see cref="AgentResumeState"/> with
    /// <see cref="AgentResumeState.StartingIter"/> set to <c>snapshotIter + 1</c>.
    /// Returns null if no valid snapshot exists.
    /// </summary>
    public static AgentResumeState? TryLoadLatest(string stageDir)
    {
        var iterationsDir = Path.Combine(stageDir, "iterations");
        if (!Directory.Exists(iterationsDir))
        {
            return null;
        }

        var bestIter = -1;
        string? bestPath = null;
        foreach (var subdir in Directory.EnumerateDirectories(iterationsDir))
        {
            var dirName = Path.GetFileName(subdir);
            if (dirName.Length != 3 || !int.TryParse(dirName, out var iter))
            {
                continue;
            }

            var statePath = Path.Combine(subdir, "state.json");
            if (!File.Exists(statePath))
            {
                continue;
            }

            if (iter > bestIter)
            {
                bestIter = iter;
                bestPath = statePath;
            }
        }

        if (bestPath is null)
        {
            return null;
        }

        return TryLoadFromFile(bestPath, bestIter);
    }

    /// <summary>
    /// Loads a snapshot from a specific file and adjusts its starting iteration.
    /// </summary>
    private static AgentResumeState? TryLoadFromFile(string path, int snapshotIter)
    {
        try
        {
            var json = File.ReadAllText(path);
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj)
            {
                return null;
            }

            var messagesArr = obj["messages"] as JsonArray ?? new JsonArray();
            var messages = messagesArr
                .Where(m => m is not null)
                .Select(m => m!.DeepClone())
                .ToList();

            var nudged = obj["nudged"] is JsonValue nv && nv.TryGetValue(out bool b) && b;

            var ledgerArr = obj["ledger"] as JsonArray ?? new JsonArray();
            var ledger = new List<AgentWriteRecord>();
            foreach (var entry in ledgerArr)
            {
                if (entry is null) continue;
                var rec = entry.Deserialize<AgentWriteRecord>(JsonSerializationDefaults.General);
                if (rec is not null) ledger.Add(rec);
            }

            return new AgentResumeState
            {
                StartingIter = snapshotIter + 1,
                Messages = messages,
                Nudged = nudged,
                LedgerEntries = ledger
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}