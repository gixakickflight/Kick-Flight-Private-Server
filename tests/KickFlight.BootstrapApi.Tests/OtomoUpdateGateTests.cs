using KickFlight.BootstrapApi.Otomo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The update gate as otomo's ops snapshot states it: the same rule as gatepolicy.Evaluate's update step.
public sealed class OtomoUpdateGateTests
{
    private static OtomoUpdateGate Gate(string snapshot, string mode = "enforce")
    {
        var gate = new OtomoUpdateGate(new OtomoUpdateGateOptions
        {
            SnapshotUrl = "http://ops.invalid/internal/ops/snapshot", Channel = "live", Mode = mode
        }, NullLogger<OtomoUpdateGate>.Instance, new HttpClientHandler());
        gate.Use(snapshot);
        return gate;
    }

    private const string ReleaseBlock = """
        {"schema":1,"revision":7,"update":{"channels":{"live":{"release_id":45,"min_client_version":"2.11.0",
         "client_update":{"latest_version":"2.11.804","min_version":{"default":"2.11.800","ios":"2.11.700"},
         "message":{"default":"Update please","ja":"更新してください"}}}},"gates":[]}}
        """;

    [Fact]
    public void The_release_block_sets_the_minimum_per_platform_and_the_message_per_language()
    {
        var gate = Gate(ReleaseBlock);

        var old = gate.Check("2.11.706", "android", "ja");
        Assert.True(old.Outdated);
        Assert.Equal("2.11.800", old.Minimum);
        Assert.Equal("更新してください", old.Message);

        Assert.False(gate.Check("2.11.804", "android", "en").Outdated);
        Assert.False(gate.Check("2.11.706", "ios", "en").Outdated);   // iOS has its own, lower minimum
        Assert.True(gate.Check("", "android", "en").Outdated);        // no version is refused
        Assert.Equal("Update please", gate.Check("2.10.0", "android", "pt").Message);
    }

    [Fact]
    public void An_enforcing_operator_gate_raises_the_minimum_and_a_lower_one_does_not()
    {
        var gate = Gate("""
            {"schema":1,"revision":8,"update":{"channels":{"live":{"min_client_version":"2.11.0","client_update":null}},
             "gates":[{"channel":"live","platform":"android","min_version":"2.11.900","message":{"default":"New build"},"enforce":true},
                      {"channel":"live","platform":"default","min_version":"2.10.0","enforce":true},
                      {"channel":"staging","platform":"android","min_version":"9.0.0","enforce":true}]}}
            """);

        var android = gate.Check("2.11.804", "android", "en");
        Assert.True(android.Outdated);
        Assert.Equal("2.11.900", android.Minimum);
        Assert.Equal("New build", android.Message);

        // iOS falls to the channel's default gate, which asks less than the release's own minimum.
        var ios = gate.Check("2.11.0", "ios", "en");
        Assert.False(ios.Outdated);
        Assert.Equal("2.11.0", ios.Minimum);
    }

    [Fact]
    public void Warn_and_a_cold_snapshot_refuse_nothing()
    {
        Assert.True(Gate(ReleaseBlock, "warn").Warns);
        Assert.False(Gate(ReleaseBlock, "off").Check("1.0.0", "android", "en").Outdated);

        var cold = new OtomoUpdateGate(new OtomoUpdateGateOptions { SnapshotUrl = "http://ops.invalid/s" },
            NullLogger<OtomoUpdateGate>.Instance, new HttpClientHandler());
        Assert.False(cold.Check("1.0.0", "android", "en").Outdated);
    }
}
