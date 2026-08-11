namespace FlowableWrapper.Configuration;

public sealed class RuntimeTuningOptions
{
    public const string SectionName = "RuntimeTuning";

    public int MinWorkerThreads { get; set; } = 200;
    public int MinIoCompletionThreads { get; set; } = 200;
}
