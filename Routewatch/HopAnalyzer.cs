using RouteWatch.Models;

namespace RouteWatch.Analysis;

/// <summary>
/// Analyses a snapshot of HopModel statistics and assigns diagnostic flags.
/// Rules are evaluated in priority order; highest-severity flag wins.
/// </summary>
public static class HopAnalyzer
{
    // Thresholds — tunable
    public const double LossWarnPct      = 5.0;
    public const double LossSeverePct    = 25.0;
    public const double LatencyJumpRatio = 2.0;   // hop avg > prev avg × this
    public const double HighLatencyMs    = 150.0;
    public const int    MinSamplesNeeded = 3;

    /// <summary>
    /// Analyse all hops in order and assign flags.
    /// Call after each sweep completes.
    /// </summary>
    public static void AnalyseAll(IReadOnlyList<HopModel> hops)
    {
        for (int i = 0; i < hops.Count; i++)
        {
            var hop = hops[i];
            if (hop.Sent < MinSamplesNeeded) continue;
            if (hop.Flag == HopFlag.Destination) continue;

            hop.Flag = Classify(hop, i > 0 ? hops[i - 1] : null, hops, i);
        }
    }

    private static HopFlag Classify(HopModel hop, HopModel? prev, IReadOnlyList<HopModel> allHops, int index)
    {
        // Completely dark hop
        if (hop.Sent > 0 && hop.Received == 0)
            return HopFlag.Unreachable;

        // Router ICMP rate-limiting: apparent loss on this intermediate hop,
        // but subsequent downstream hops are healthy (no true end-to-end packet loss)
        if (hop.Loss >= LossWarnPct && IsDownstreamHealthy(allHops, index))
            return HopFlag.RateLimiting;

        // Severe packet loss
        if (hop.Loss >= LossSeverePct)
            return HopFlag.SeverePacketLoss;

        // Any packet loss
        if (hop.Loss >= LossWarnPct)
            return HopFlag.PacketLoss;

        // High absolute latency
        if (hop.Avg >= HighLatencyMs)
            return HopFlag.HighLatency;

        // Sudden latency jump vs previous hop
        if (prev != null && prev.Avg > 0 && hop.Avg > prev.Avg * LatencyJumpRatio)
            return HopFlag.HighLatency;

        // Asymmetric route: RTT suddenly drops (ICMP replies taking different path)
        if (prev != null && prev.Avg > 0 && hop.Avg < prev.Avg * 0.4 && hop.Avg > 0)
            return HopFlag.AsymmetricRoute;

        return HopFlag.None;
    }

    private static bool IsDownstreamHealthy(IReadOnlyList<HopModel> hops, int index)
    {
        bool foundRespondingDownstream = false;
        for (int i = index + 1; i < hops.Count; i++)
        {
            var next = hops[i];
            if (next.Sent >= MinSamplesNeeded && next.Received > 0)
            {
                foundRespondingDownstream = true;
                if (next.Loss >= LossWarnPct)
                    return false; // Downstream also has loss, so true path loss
            }
        }
        return foundRespondingDownstream;
    }

    /// <summary>
    /// Compute overall path stability score 0–100.
    /// 100 = perfect; penalises for loss and high jitter.
    /// </summary>
    public static double ComputeStabilityScore(IReadOnlyList<HopModel> hops)
    {
        if (hops.Count == 0) return 100;

        double totalPenalty = 0;
        int counted = 0;

        foreach (var hop in hops)
        {
            if (hop.Sent < MinSamplesNeeded) continue;
            counted++;

            // Loss penalty: up to 60 points (rate-limited control-plane responses are discounted)
            double effectiveLoss = hop.Flag == HopFlag.RateLimiting ? 0 : hop.Loss;
            totalPenalty += Math.Min(effectiveLoss / 100.0 * 60, 60);

            // Jitter penalty: up to 40 points (normalised at 100 ms jitter = max)
            totalPenalty += Math.Min(hop.Jitter / 100.0 * 40, 40);
        }

        if (counted == 0) return 100;
        double avgPenalty = totalPenalty / counted;
        return Math.Max(0, Math.Round(100 - avgPenalty, 1));
    }

    /// <summary>Returns a human-readable summary of problems found.</summary>
    public static IReadOnlyList<string> Diagnose(IReadOnlyList<HopModel> hops)
    {
        var msgs = new List<string>();

        // 1. Destination status
        var dest = hops.FirstOrDefault(h => h.Flag == HopFlag.Destination);
        if (dest != null && dest.Received > 0)
        {
            if (dest.Loss == 0)
                msgs.Add($"✔ Target Destination reached (Hop {dest.Ttl} · {dest.IpAddress}): 0.0% loss, {dest.Last:F1}ms latency. Path is healthy.");
            else
                msgs.Add($"⚠ Target Destination reached (Hop {dest.Ttl} · {dest.IpAddress}): {dest.Loss:F1}% end-to-end packet loss detected!");
        }

        // 2. Identify true packet loss on intermediate hops
        var lossHops = hops.Where(h => h.Sent >= MinSamplesNeeded && (h.Flag == HopFlag.SeverePacketLoss || h.Flag == HopFlag.PacketLoss)).ToList();
        foreach (var hop in lossHops)
        {
            string severity = hop.Flag == HopFlag.SeverePacketLoss ? "SEVERE" : "Moderate";
            msgs.Add($"⚠ Hop {hop.Ttl} ({hop.IpAddress}): {severity} loss {hop.Loss:F1}% — possible link congestion or carrier drop.");
        }

        // 3. Identify ICMP Rate Limiting
        var rateLimitedHops = hops.Where(h => h.Sent >= MinSamplesNeeded && h.Flag == HopFlag.RateLimiting).ToList();
        if (rateLimitedHops.Count > 0)
        {
            string hopsList = string.Join(", ", rateLimitedHops.Select(h => $"Hop {h.Ttl} ({h.IpAddress})"));
            msgs.Add($"ℹ {hopsList}: likely ICMP response limiting. Downstream probes show no corresponding loss, so these missed router replies are excluded from path-loss summaries.");
        }

        // 4. Group consecutive unreachable/silent hops
        int startSilent = -1;
        int endSilent = -1;
        for (int i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            if (h.Sent >= MinSamplesNeeded && h.Flag == HopFlag.Unreachable)
            {
                if (startSilent == -1) startSilent = h.Ttl;
                endSilent = h.Ttl;
            }
            else
            {
                if (startSilent != -1)
                {
                    AddSilentRangeMsg(msgs, startSilent, endSilent);
                    startSilent = -1;
                }
            }
        }
        if (startSilent != -1)
        {
            AddSilentRangeMsg(msgs, startSilent, endSilent);
        }

        // 5. High latency jumps
        var highLatencyHops = hops.Where(h => h.Sent >= MinSamplesNeeded && h.Flag == HopFlag.HighLatency).ToList();
        foreach (var hop in highLatencyHops)
        {
            msgs.Add($"⚡ Hop {hop.Ttl} ({hop.IpAddress}): Latency jump to {hop.Avg:F1}ms (trans-continental or long-haul segment).");
        }

        if (msgs.Count == 0)
            msgs.Add("✔ Path appears completely healthy — 100% packet delivery across all responding nodes.");

        return msgs;
    }

    private static void AddSilentRangeMsg(List<string> msgs, int start, int end)
    {
        if (start == end)
            msgs.Add($"🛡 Hop {start} (???): Silent router — ICMP echo/TTL-expired filtered by firewall.");
        else
            msgs.Add($"🛡 Hops {start}–{end} ({end - start + 1} hops): Silent transit segment — ISP core routers filter ICMP generation.");
    }
}
