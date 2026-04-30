using System.Text.Json;
using System.Threading.Channels;
using Forge.Core.Json;

namespace Forge.Core.Trace;

/// <summary>Append-only JSONL trace. Bounded channel: when full, <see cref="Trace"/> blocks until drained (backpressure).</summary>
public sealed class TraceSink : ITraceSink
{
  private readonly Channel<TraceEvent> _channel;
  private readonly ChannelWriter<TraceEvent> _writer;
  private readonly Task _writerTask;
  private readonly string _path;
  private readonly CancellationTokenSource _cts = new();

  public TraceSink(string tracePath, int capacity = 4096)
  {
    _path = tracePath;
    var dir = Path.GetDirectoryName(tracePath);
    if (!string.IsNullOrEmpty(dir))
    {
      Directory.CreateDirectory(dir);
    }

    var opts = new BoundedChannelOptions(capacity)
    {
      FullMode = BoundedChannelFullMode.Wait,
      SingleReader = true,
      SingleWriter = false
    };
    _channel = Channel.CreateBounded<TraceEvent>(opts);
    _writer = _channel.Writer;
    _writerTask = Task.Run(() => WriteLoopAsync(_cts.Token), CancellationToken.None);
  }

  public void Trace(TraceEvent e)
  {
    _writer.WriteAsync(e).GetAwaiter().GetResult();
  }

  private async Task WriteLoopAsync(CancellationToken ct)
  {
    await using var sw = new StreamWriter(_path, append: true, System.Text.Encoding.UTF8);
    await foreach (var ev in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
    {
      var line = JsonSerializer.Serialize(ev, ev.GetType(), JsonSerializationDefaults.Trace);
      await sw.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
      // Flush each line so a non-graceful shutdown (Ctrl-C, kill, OOM, power loss)
      // leaves trace.jsonl as a sequence of complete-or-absent lines, not torn ones.
      // The trace runs on a dedicated background channel so the per-event fsync cost
      // does not block the agent's hot path. Worst residual: a hard kill mid-syscall
      // can still tear bytes; readers already swallow that case via try/catch.
      await sw.FlushAsync(ct).ConfigureAwait(false);
    }
  }

  public async ValueTask DisposeAsync()
  {
    _writer.Complete();
    try
    {
      await _writerTask.ConfigureAwait(false);
    }
    catch
    {
      // best-effort
    }

    _cts.Dispose();
  }
}
