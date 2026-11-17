namespace FlowableWrapper.Infrastructure.Readiness;

public sealed class ApplicationReadinessState
{
    private readonly object _gate = new();
    private bool _ready;
    private DateTime? _readySince;
    private IReadOnlyDictionary<string, string> _dependencies =
        new Dictionary<string, string>();

    public ReadinessSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ReadinessSnapshot(
                _ready,
                _readySince,
                new Dictionary<string, string>(_dependencies));
        }
    }

    public void SetReady(IReadOnlyDictionary<string, string> dependencies)
    {
        lock (_gate)
        {
            _ready = true;
            _readySince ??= DateTime.UtcNow;
            _dependencies = new Dictionary<string, string>(dependencies);
        }
    }

    public void SetNotReady(IReadOnlyDictionary<string, string> dependencies)
    {
        lock (_gate)
        {
            _ready = false;
            _readySince = null;
            _dependencies = new Dictionary<string, string>(dependencies);
        }
    }
}

public sealed record ReadinessSnapshot(
    bool Ready,
    DateTime? ReadySince,
    IReadOnlyDictionary<string, string> Dependencies);
