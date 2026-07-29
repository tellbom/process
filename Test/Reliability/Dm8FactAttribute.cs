using Xunit;

namespace FlowableWrapper.Test.Reliability;

public sealed class Dm8FactAttribute : FactAttribute
{
    public Dm8FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FLOW_DM8_TEST_CONNECTION_STRING")))
        {
            Skip = "FLOW_DM8_TEST_CONNECTION_STRING is not configured.";
        }
    }
}
