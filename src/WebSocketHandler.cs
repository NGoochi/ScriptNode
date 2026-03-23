using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScriptNodePlugin
{
    /// <summary>Tracks WebSocket connections per Alien node GUID.</summary>
    public sealed class AlienNodeWebSocketHub
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<int, WebSocket>> _byNode =
            new ConcurrentDictionary<Guid, ConcurrentDictionary<int, WebSocket>>();

        private int _socketId;

        public IDisposable Register(Guid nodeGuid, WebSocket socket)
        {
            int id = Interlocked.Increment(ref _socketId);
            var bag = _byNode.GetOrAdd(nodeGuid, _ => new ConcurrentDictionary<int, WebSocket>());
            bag[id] = socket;
            return new Registration(this, nodeGuid, id);
        }

        private void Remove(Guid nodeGuid, int id)
        {
            if (_byNode.TryGetValue(nodeGuid, out var bag)
                && bag.TryRemove(id, out var ws))
            {
                try { ws.Dispose(); } catch { }
            }
            if (bag != null && bag.IsEmpty)
                _byNode.TryRemove(nodeGuid, out _);
        }

        public void BroadcastText(Guid nodeGuid, string text)
        {
            if (!_byNode.TryGetValue(nodeGuid, out var bag)) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            foreach (var kv in bag)
            {
                var ws = kv.Value;
                if (ws.State != WebSocketState.Open) continue;
                try
                {
                    _ = ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch { }
            }
        }

        private sealed class Registration : IDisposable
        {
            private readonly AlienNodeWebSocketHub _hub;
            private readonly Guid _guid;
            private readonly int _id;

            public Registration(AlienNodeWebSocketHub hub, Guid guid, int id)
            {
                _hub = hub;
                _guid = guid;
                _id = id;
            }

            public void Dispose() => _hub.Remove(_guid, _id);
        }
    }
}
