using FlowableWrapper.Infrastructure.Readiness;
using Xunit;

namespace FlowableWrapper.Test.Reliability;

public class ReadinessStateTests
{
    [Fact]
    public void New_state_is_not_ready_and_can_transition_both_ways()
    {
        var state = new ApplicationReadinessState();

        Assert.False(state.Snapshot().Ready);

        state.SetReady(new Dictionary<string, string>
        {
            ["flowable"] = "ready"
        });
        var ready = state.Snapshot();
        Assert.True(ready.Ready);
        Assert.NotNull(ready.ReadySince);
        Assert.Equal("ready", ready.Dependencies["flowable"]);

        state.SetNotReady(new Dictionary<string, string>
        {
            ["failure"] = "flowable unavailable"
        });
        var notReady = state.Snapshot();
        Assert.False(notReady.Ready);
        Assert.Null(notReady.ReadySince);
        Assert.Equal(
            "flowable unavailable",
            notReady.Dependencies["failure"]);
    }
}
