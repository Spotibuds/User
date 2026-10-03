namespace User.Services;
public sealed class PresenceStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, HashSet<string>> connections = new();
    public bool Add(string user, string connection) { lock (gate) { if (!connections.TryGetValue(user, out var set)) connections[user] = set = []; var first = set.Count == 0; set.Add(connection); return first; } }
    public bool Remove(string user, string connection) { lock (gate) { if (!connections.TryGetValue(user, out var set)) return false; set.Remove(connection); if (set.Count != 0) return false; connections.Remove(user); return true; } }
    public bool Online(string user) { lock (gate) return connections.ContainsKey(user); }
}
