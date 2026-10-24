using System;
using System.Collections.Generic;

namespace FrameCacheSpike;

internal sealed class FrameCache
{
    private sealed record Entry(string Key, NativeFrame Frame);

    private readonly int capacity;
    private readonly LinkedList<Entry> lru = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> byKey = new(StringComparer.Ordinal);

    internal FrameCache(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
    }

    internal int Count => byKey.Count;

    internal void Add(NativeFrame frame)
    {
        string key = MakeKey(frame.SessionId, frame.RenderIdentity, frame.TimeSeconds);
        if (byKey.TryGetValue(key, out LinkedListNode<Entry>? existing))
        {
            lru.Remove(existing);
            byKey.Remove(key);
        }

        LinkedListNode<Entry> node = lru.AddFirst(new Entry(key, frame));
        byKey[key] = node;

        while (byKey.Count > capacity)
        {
            LinkedListNode<Entry>? tail = lru.Last;
            if (tail is null)
            {
                break;
            }

            lru.RemoveLast();
            byKey.Remove(tail.Value.Key);
        }
    }

    internal bool TryGet(
        string sessionId,
        string renderIdentity,
        double timeSeconds,
        double toleranceSeconds,
        out NativeFrame frame)
    {
        LinkedListNode<Entry>? best = null;
        double bestDistance = double.MaxValue;

        for (LinkedListNode<Entry>? node = lru.First; node is not null; node = node.Next)
        {
            NativeFrame candidate = node.Value.Frame;
            if (!string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(candidate.RenderIdentity, renderIdentity, StringComparison.Ordinal))
            {
                continue;
            }

            double distance = Math.Abs(candidate.TimeSeconds - timeSeconds);
            if (distance <= toleranceSeconds && distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }

        if (best is null)
        {
            frame = null!;
            return false;
        }

        lru.Remove(best);
        lru.AddFirst(best);
        frame = best.Value.Frame;
        return true;
    }

    internal void InvalidateIdentity(string renderIdentity)
    {
        List<LinkedListNode<Entry>> removed = new();
        for (LinkedListNode<Entry>? node = lru.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value.Frame.RenderIdentity, renderIdentity, StringComparison.Ordinal))
            {
                removed.Add(node);
            }
        }

        foreach (LinkedListNode<Entry> node in removed)
        {
            lru.Remove(node);
            byKey.Remove(node.Value.Key);
        }
    }

    internal void Clear()
    {
        lru.Clear();
        byKey.Clear();
    }

    private static string MakeKey(string sessionId, string renderIdentity, double timeSeconds)
    {
        long micros = checked((long)Math.Round(timeSeconds * 1_000_000d, MidpointRounding.AwayFromZero));
        return sessionId
            + "|"
            + renderIdentity
            + "|"
            + micros.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
