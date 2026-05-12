using GrokMcp.Config;
using GrokMcp.Services;
using Microsoft.Extensions.Options;

namespace GrokMcp.Tests;

public class ChatSessionStoreTests
{
    private static ChatSessionStore NewStore(int cap)
    {
        var opts = Options.Create(new GrokOptions { SessionTurnCap = cap });
        return new ChatSessionStore(opts);
    }

    [Fact]
    public void Append_below_cap_keeps_all_turns()
    {
        var store = NewStore(cap: 4);
        store.Append("s", new ChatTurn("user", "1"));
        store.Append("s", new ChatTurn("assistant", "2"));
        store.Append("s", new ChatTurn("user", "3"));

        var snap = store.Snapshot("s");
        Assert.Equal(3, snap.Count);
        Assert.Equal("1", snap[0].Content);
        Assert.Equal("3", snap[2].Content);
    }

    [Fact]
    public void Append_over_cap_fifo_trims_oldest_first()
    {
        var store = NewStore(cap: 2);
        store.Append("s", new ChatTurn("user", "1"));
        store.Append("s", new ChatTurn("user", "2"));
        store.Append("s", new ChatTurn("user", "3"));

        var snap = store.Snapshot("s");
        Assert.Equal(2, snap.Count);
        Assert.Equal("2", snap[0].Content);
        Assert.Equal("3", snap[1].Content);
    }

    [Fact]
    public void Reset_removes_session_history()
    {
        var store = NewStore(cap: 4);
        store.Append("s", new ChatTurn("user", "1"));
        store.Reset("s");
        Assert.Empty(store.Snapshot("s"));
    }

    [Fact]
    public void Snapshot_returns_copy_not_live_view()
    {
        var store = NewStore(cap: 4);
        store.Append("s", new ChatTurn("user", "1"));
        var snap = store.Snapshot("s");
        store.Append("s", new ChatTurn("user", "2"));
        Assert.Single(snap);
    }

    [Fact]
    public void Sessions_are_isolated_by_id()
    {
        var store = NewStore(cap: 4);
        store.Append("a", new ChatTurn("user", "A1"));
        store.Append("b", new ChatTurn("user", "B1"));

        var a = store.Snapshot("a");
        var b = store.Snapshot("b");
        Assert.Equal("A1", Assert.Single(a).Content);
        Assert.Equal("B1", Assert.Single(b).Content);
    }
}
