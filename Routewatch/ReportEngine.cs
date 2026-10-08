using System.Text;
using System.Text.Json;
using RouteWatch.Analysis;
using RouteWatch.Models;

namespace RouteWatch.Reporting;

/// <summary>Generates HTML and JSON diagnostic reports.</summary>
public static class ReportEngine
{
    // ── JSON ──────────────────────────────────────────────────────────────
    public static string ToJson(
        string target, string targetIp, ProbeProtocol protocol, int port,
        IReadOnlyList<HopModel> hops, double stabilityScore)
    {
        var doc = new
        {
            meta = new
            {
                tool      = "RouteWatch",
                version   = "1.0.0",
                target,
                targetIp,
                protocol  = protocol.ToString(),
                port,
                generated = DateTime.UtcNow.ToString("o"),
                stabilityScore
            },
            diagnosis = HopAnalyzer.Diagnose(hops),
            hops = hops.Where(h => h.Sent > 0).Select(h => new
            {
                ttl     = h.Ttl,
                ip      = h.IpAddress,
                host    = h.HostName,
                country = h.Country,
                city    = h.City,
                sent    = h.Sent,
                recv    = h.Received,
                lossPct = Math.Round(h.PathLoss, 2),
                replyLossPct = Math.Round(h.Loss, 2),
                lastMs  = Math.Round(h.Last,  2),
                avgMs   = Math.Round(h.Avg,   2),
                bestMs  = h.Best < double.MaxValue ? Math.Round(h.Best, 2) : (double?)null,
                worstMs = h.Worst > 0 ? Math.Round(h.Worst, 2) : (double?)null,
                jitterMs= Math.Round(h.Jitter, 2),
                stdDevMs= Math.Round(h.StdDev, 2),
                flag    = h.Flag.ToString()
            })
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── HTML ──────────────────────────────────────────────────────────────
    public static string ToHtml(
        string target, string targetIp, ProbeProtocol protocol, int port,
        IReadOnlyList<HopModel> hops, double stabilityScore)
    {
        var liveHops = hops.Where(h => h.Sent > 0).ToList();
        var diagnosis = HopAnalyzer.Diagnose(hops);
        var worstHop  = liveHops.OrderByDescending(h => h.PathLoss)
                                 .ThenByDescending(h => h.Avg)
                                 .FirstOrDefault();

        var sb = new StringBuilder();
        sb.Append(HtmlHeader(target, targetIp, protocol, port, stabilityScore));

        // Diagnosis summary
        sb.Append("<div class=\"section\"><h2>Diagnosis</h2><ul class=\"diag\">");
        foreach (var d in diagnosis)
            sb.Append($"<li>{System.Net.WebUtility.HtmlEncode(d)}</li>");
        sb.Append("</ul></div>");

        // Summary cards
        double totalLoss = liveHops.Count > 0 ? liveHops.Average(h => h.PathLoss) : 0;
        double maxLatency = liveHops.Count > 0 ? liveHops.Max(h => h.Avg) : 0;
        sb.Append($@"
<div class=""cards"">
  <div class=""card""><div class=""card-val {ScoreClass(stabilityScore)}"">{stabilityScore:F0}</div><div class=""card-lbl"">Stability Score</div></div>
  <div class=""card""><div class=""card-val {LossClass(totalLoss)}"">{totalLoss:F1}%</div><div class=""card-lbl"">Avg Path Loss</div></div>
  <div class=""card""><div class=""card-val"">{maxLatency:F1} ms</div><div class=""card-lbl"">Peak Latency</div></div>
  <div class=""card""><div class=""card-val"">{liveHops.Count}</div><div class=""card-lbl"">Hops Discovered</div></div>
</div>");

        // Hop table
        sb.Append(@"
<div class=""section"">
<h2>Hop-by-Hop Analysis</h2>
<table>
<thead><tr>
  <th>Hop</th><th>Host</th><th>IP</th><th>Location</th>
  <th>Path loss%</th><th>Reply loss%</th><th>Sent</th><th>Recv</th>
  <th>Last ms</th><th>Avg ms</th><th>Best ms</th><th>Worst ms</th><th>Jitter ms</th><th>StdDev</th><th>Flag</th>
</tr></thead><tbody>");

        foreach (var h in liveHops)
        {
            string rowCls  = RowClass(h);
            string flagStr = h.Flag == HopFlag.None ? "" : h.Flag.ToString();
            string geo     = string.IsNullOrEmpty(h.Country) ? "" : $"{h.City}, {h.Country}".Trim(',', ' ');
            sb.Append($@"
<tr class=""{rowCls}"">
  <td>{h.Ttl}</td>
  <td>{HE(h.HostName)}</td>
  <td><code>{HE(h.IpAddress)}</code></td>
  <td>{HE(geo)}</td>
  <td class=""{LossClass(h.PathLoss)}"">{h.PathLoss:F1}%</td>
  <td class=""{LossClass(h.Loss)}"">{h.Loss:F1}%</td>
  <td>{h.Sent}</td><td>{h.Received}</td>
  <td>{Fmt(h.Last)}</td><td>{Fmt(h.Avg)}</td>
  <td>{(h.Best < double.MaxValue ? Fmt(h.Best) : "—")}</td>
  <td>{(h.Worst > 0 ? Fmt(h.Worst) : "—")}</td>
  <td>{Fmt(h.Jitter)}</td>
  <td>{Fmt(h.StdDev)}</td>
  <td class=""flag"">{HE(flagStr)}</td>
</tr>");
        }

        sb.Append("</tbody></table></div>");

        // Latency sparklines via inline SVG chart
        sb.Append(LatencyChart(liveHops));

        sb.Append(HtmlFooter());
        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static string Fmt(double v) => v >= 0 ? $"{v:F1}" : "—";
    private static string HE(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string LossClass(double loss) =>
        loss == 0 ? "ok" : loss < 10 ? "warn" : loss < 50 ? "bad" : "critical";

    private static string ScoreClass(double score) =>
        score >= 90 ? "ok" : score >= 60 ? "warn" : "bad";

    private static string RowClass(HopModel h) =>
        h.Flag == HopFlag.SeverePacketLoss ? "row-critical" :
        h.Flag == HopFlag.PacketLoss ? "row-warn" :
        h.Flag == HopFlag.HighLatency ? "row-warn" :
        h.Flag == HopFlag.Unreachable ? "row-muted" :
        h.Flag == HopFlag.Destination ? "row-dest" : "";

    private static string LatencyChart(List<HopModel> hops)
    {
        if (hops.Count == 0) return string.Empty;

        const int chartW = 700, chartH = 80, padL = 40, padR = 10, padT = 5, padB = 20;
        double maxRtt = hops.Where(h => h.Worst > 0).Select(h => h.Worst).DefaultIfEmpty(1).Max();
        double xStep  = (double)(chartW - padL - padR) / Math.Max(hops.Count - 1, 1);

        var sbSvg = new StringBuilder();
        sbSvg.Append($@"<div class=""section""><h2>Latency Overview</h2>
<svg viewBox=""0 0 {chartW} {chartH + padT + padB}"" xmlns=""http://www.w3.org/2000/svg"" 
     style=""background:#161b22;border-radius:8px;width:100%;max-width:{chartW}px"">
<defs>
  <linearGradient id=""grad"" x1=""0"" y1=""0"" x2=""0"" y2=""1"">
    <stop offset=""0%"" stop-color=""#58a6ff"" stop-opacity=""0.4""/>
    <stop offset=""100%"" stop-color=""#58a6ff"" stop-opacity=""0""/>
  </linearGradient>
</defs>");

        // Area + line
        var avgPts = new List<string>();
        var areaPath = new StringBuilder("M ");

        for (int i = 0; i < hops.Count; i++)
        {
            double x = padL + i * xStep;
            double y = hops[i].Avg > 0
                       ? chartH + padT - (hops[i].Avg / maxRtt * (chartH - padT))
                       : chartH + padT;
            avgPts.Add($"{x:F1},{y:F1}");
            areaPath.Append(i == 0 ? $"{x:F1},{y:F1}" : $" L{x:F1},{y:F1}");
        }

        double lastX = padL + (hops.Count - 1) * xStep;
        sbSvg.Append($"<path d=\"{areaPath} L{lastX:F1},{chartH + padT} L{padL},{chartH + padT} Z\" fill=\"url(#grad)\"/>");
        sbSvg.Append($"<polyline points=\"{string.Join(" ", avgPts)}\" fill=\"none\" stroke=\"#58a6ff\" stroke-width=\"2\"/>");

        // Dot + label per hop
        for (int i = 0; i < hops.Count; i++)
        {
            double x = padL + i * xStep;
            double y = hops[i].Avg > 0
                       ? chartH + padT - (hops[i].Avg / maxRtt * (chartH - padT))
                       : chartH + padT;
            sbSvg.Append($"<circle cx=\"{x:F1}\" cy=\"{y:F1}\" r=\"3\" fill=\"#58a6ff\"/>");
            sbSvg.Append($"<text x=\"{x:F1}\" y=\"{chartH + padT + padB - 2}\" text-anchor=\"middle\" font-size=\"9\" fill=\"#8b949e\">{hops[i].Ttl}</text>");
        }

        // Y axis label
        sbSvg.Append($"<text x=\"{padL - 4}\" y=\"{padT + 10}\" text-anchor=\"end\" font-size=\"9\" fill=\"#8b949e\">{maxRtt:F0}ms</text>");
        sbSvg.Append($"<text x=\"{padL - 4}\" y=\"{chartH + padT}\" text-anchor=\"end\" font-size=\"9\" fill=\"#8b949e\">0</text>");

        sbSvg.Append("</svg></div>");
        return sbSvg.ToString();
    }

    // ── HTML scaffolding ──────────────────────────────────────────────────
    private static string HtmlHeader(string target, string targetIp, ProbeProtocol proto, int port, double score) => $@"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""utf-8"">
<title>RouteWatch Report — {HE(target)}</title>
<style>
  :root{{--bg:#0d1117;--panel:#161b22;--border:#21262d;--acc:#58a6ff;--green:#3fb950;--yellow:#d29922;--red:#f85149;--text:#c9d1d9;--muted:#484f58}}
  *{{box-sizing:border-box;margin:0;padding:0}}
  body{{background:var(--bg);color:var(--text);font-family:'Segoe UI',sans-serif;font-size:14px;line-height:1.6;padding:32px}}
  h1{{font-size:28px;color:#fff;margin-bottom:4px}}
  h2{{font-size:16px;color:var(--acc);margin:24px 0 12px;border-bottom:1px solid var(--border);padding-bottom:6px}}
  .meta{{color:var(--muted);font-size:12px;margin-bottom:24px}}
  .section{{margin-bottom:32px}}
  .cards{{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:24px}}
  .card{{background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:16px 24px;min-width:140px}}
  .card-val{{font-size:28px;font-weight:700;color:#fff}}
  .card-lbl{{font-size:11px;color:var(--muted);text-transform:uppercase;letter-spacing:1px}}
  table{{width:100%;border-collapse:collapse;font-size:12px;font-family:'Consolas',monospace}}
  th{{background:var(--border);color:var(--acc);padding:8px 10px;text-align:left;white-space:nowrap}}
  td{{padding:7px 10px;border-bottom:1px solid var(--border)}}
  tr:hover td{{background:rgba(88,166,255,0.04)}}
  .row-warn td{{border-left:3px solid var(--yellow)}}
  .row-critical td{{border-left:3px solid var(--red)}}
  .row-muted{{opacity:.5}}
  .row-dest td{{border-left:3px solid var(--green)}}
  .ok{{color:var(--green)}} .warn{{color:var(--yellow)}} .bad{{color:var(--red)}} .critical{{color:#ff0040}}
  .flag{{font-size:10px;color:var(--yellow)}}
  .diag{{padding-left:20px;color:var(--muted);font-size:13px}} .diag li{{margin-bottom:6px}}
  code{{background:var(--panel);border:1px solid var(--border);border-radius:3px;padding:1px 5px;font-size:11px;color:var(--acc)}}
  footer{{margin-top:48px;padding-top:16px;border-top:1px solid var(--border);color:var(--muted);font-size:11px;display:flex;justify-content:space-between}}
</style></head><body>
<h1>RouteWatch — Diagnostic Report</h1>
<div class=""meta"">Target: <strong style=""color:#fff"">{HE(target)}</strong> ({HE(targetIp)}) &nbsp;|&nbsp;
Protocol: <strong>{proto}{(proto != ProbeProtocol.ICMP ? $":{port}" : "")}</strong> &nbsp;|&nbsp;
Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} &nbsp;|&nbsp; Stability: <span class=""{ScoreClass(score)}"">{score:F0}/100</span></div>
";

    private static string HtmlFooter() => @"
<footer><span>RouteWatch v1.0</span><span>Generated by RouteWatch Reporting Engine</span></footer>
</body></html>";
}
