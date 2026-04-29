using PortMTR.Enterprise.Models;

namespace PortMTR.Enterprise.Analysis;

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

            hop.Flag = Classify(hop, i > 0 ? hops[i - 1] : null);
        }
    }

    private static HopFlag Classify(HopModel hop, HopModel? prev)
    {
        // Completely dark hop
        if (hop.Sent > 0 && hop.Received == 0)
            return HopFlag.Unreachable;

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

            // Loss penalty: up to 60 points
            totalPenalty += Math.Min(hop.Loss / 100.0 * 60, 60);

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

        foreach (var hop in hops)
        {
            if (hop.Sent < MinSamplesNeeded) continue;

            string id = $"Hop {hop.Ttl} ({hop.IpAddress})";

            switch (hop.Flag)
            {
                case HopFlag.Unreachable:
                    msgs.Add($"{id}: No response — ICMP may be filtered or node is down.");
                    break;
                case HopFlag.SeverePacketLoss:
                    msgs.Add($"{id}: SEVERE loss {hop.Loss:F1}% — likely congestion or faulty link.");
                    break;
                case HopFlag.PacketLoss:
                    msgs.Add($"{id}: Packet loss {hop.Loss:F1}% — possible congestion or rate-limiting.");
                    break;
                case HopFlag.HighLatency:
                    msgs.Add($"{id}: High latency {hop.Avg:F1} ms — possible bottleneck or long-haul segment.");
                    break;
                case HopFlag.AsymmetricRoute:
                    msgs.Add($"{id}: Asymmetric route detected — ICMP replies taking shorter return path.");
                    break;
            }
        }

        if (msgs.Count == 0)
            msgs.Add("Path appears healthy — no significant issues detected.");

        return msgs;
    }
}
