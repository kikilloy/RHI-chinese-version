using RenoDXCommander.Services.Net;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// Two consecutive failures skip a mirror for 10 minutes. The clock is injected so the cooldown
/// is verified without sleeping.
/// </summary>
public class MirrorHealthTrackerTests
{
    private const string Mirror = "https://m1.example/";

    [Fact]
    public void FreshTracker_IsClosed()
    {
        var tracker = new MirrorHealthTracker();
        Assert.False(tracker.IsCircuitOpen(Mirror));
        Assert.Equal(0, tracker.ConsecutiveFailures(Mirror));
    }

    [Fact]
    public void OneFailure_DoesNotOpen()
    {
        var tracker = new MirrorHealthTracker();
        tracker.RecordFailure(Mirror);

        Assert.False(tracker.IsCircuitOpen(Mirror));
        Assert.Equal(1, tracker.ConsecutiveFailures(Mirror));
    }

    [Fact]
    public void TwoFailures_OpenTheCircuit()
    {
        var tracker = new MirrorHealthTracker();
        tracker.RecordFailure(Mirror);
        tracker.RecordFailure(Mirror);

        Assert.True(tracker.IsCircuitOpen(Mirror));
    }

    [Fact]
    public void Success_ResetsTheCount()
    {
        var tracker = new MirrorHealthTracker();
        tracker.RecordFailure(Mirror);
        tracker.RecordSuccess(Mirror);

        Assert.Equal(0, tracker.ConsecutiveFailures(Mirror));

        tracker.RecordFailure(Mirror);
        Assert.False(tracker.IsCircuitOpen(Mirror));   // only one failure since the success
    }

    [Fact]
    public void CircuitClosesAgain_AfterCooldown()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var tracker = new MirrorHealthTracker(() => now);

        tracker.RecordFailure(Mirror);
        tracker.RecordFailure(Mirror);
        Assert.True(tracker.IsCircuitOpen(Mirror));

        now = now + MirrorHealthTracker.Cooldown + TimeSpan.FromSeconds(1);
        Assert.False(tracker.IsCircuitOpen(Mirror));
        Assert.Equal(0, tracker.ConsecutiveFailures(Mirror));
    }

    [Fact]
    public void Mirrors_AreTrackedIndependently()
    {
        var tracker = new MirrorHealthTracker();
        tracker.RecordFailure("https://a.example/");
        tracker.RecordFailure("https://a.example/");

        Assert.True(tracker.IsCircuitOpen("https://a.example/"));
        Assert.False(tracker.IsCircuitOpen("https://b.example/"));
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var tracker = new MirrorHealthTracker();
        tracker.RecordFailure(Mirror);
        tracker.RecordFailure(Mirror);

        tracker.Reset();

        Assert.False(tracker.IsCircuitOpen(Mirror));
    }

    [Fact]
    public void EmptyKey_IsIgnored()
    {
        var tracker = new MirrorHealthTracker();
        tracker.RecordFailure("");
        Assert.False(tracker.IsCircuitOpen(""));
        Assert.Equal(0, tracker.ConsecutiveFailures(""));
    }
}
