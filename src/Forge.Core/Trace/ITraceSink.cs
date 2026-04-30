namespace Forge.Core.Trace;

public interface ITraceSink : IAsyncDisposable
{
  void Trace(TraceEvent e);
}
