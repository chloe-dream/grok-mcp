using System.Collections.Concurrent;
using GrokMcp.Config;
using Microsoft.Extensions.Options;

namespace GrokMcp.Services;

public record ChatTurn(string Role, string Content);

public class ChatSessionStore
{
    private readonly ConcurrentDictionary<string, List<ChatTurn>> _sessions = new();
    private readonly int _cap;

    public ChatSessionStore(IOptions<GrokOptions> opts)
    {
        _cap = opts.Value.SessionTurnCap;
    }

    public List<ChatTurn> Get(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, _ => new List<ChatTurn>());
    }

    public void Append(string sessionId, ChatTurn turn)
    {
        var list = Get(sessionId);
        lock (list)
        {
            list.Add(turn);
            // FIFO trim — keep last _cap turns. Drop pairs to preserve user/assistant boundary roughly.
            while (list.Count > _cap)
                list.RemoveAt(0);
        }
    }

    public void Reset(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    public IReadOnlyList<ChatTurn> Snapshot(string sessionId)
    {
        var list = Get(sessionId);
        lock (list) { return list.ToArray(); }
    }
}
