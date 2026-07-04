using Raizen.Server.Core.Services;

namespace Raizen.Tests;

public sealed class ApprovalToastNotifierTests
{
    [Fact]
    public void NotifyNewRequest_TriggersSubscriber()
    {
        var notifier = new ApprovalToastNotifier();
        Guid? received = null;
        notifier.OnNewRequest += id => received = id;

        var expected = Guid.NewGuid();
        notifier.NotifyNewRequest(expected);

        Assert.Equal(expected, received);
    }

    [Fact]
    public void NotifyNewRequest_MultipleSubscribers_AllReceive()
    {
        var notifier = new ApprovalToastNotifier();
        var results = new List<Guid>();

        notifier.OnNewRequest += id => results.Add(id);
        notifier.OnNewRequest += id => results.Add(id);

        var requestId = Guid.NewGuid();
        notifier.NotifyNewRequest(requestId);

        Assert.Equal(2, results.Count);
        Assert.All(results, id => Assert.Equal(requestId, id));
    }

    [Fact]
    public void UnsubscribedHandler_NotCalled()
    {
        var notifier = new ApprovalToastNotifier();
        var called = false;
        Action<Guid> handler = _ => called = true;

        notifier.OnNewRequest += handler;
        notifier.OnNewRequest -= handler;

        notifier.NotifyNewRequest(Guid.NewGuid());

        Assert.False(called);
    }

    [Fact]
    public void NotifyNewRequest_NoSubscribers_DoesNotThrow()
    {
        var notifier = new ApprovalToastNotifier();
        var ex = Record.Exception(() => notifier.NotifyNewRequest(Guid.NewGuid()));
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyNewRequest_ConcurrentSubscribers_AllReceive()
    {
        var notifier = new ApprovalToastNotifier();
        var count = 0;

        for (var i = 0; i < 10; i++)
            notifier.OnNewRequest += _ => Interlocked.Increment(ref count);

        notifier.NotifyNewRequest(Guid.NewGuid());

        Assert.Equal(10, count);
    }
}
