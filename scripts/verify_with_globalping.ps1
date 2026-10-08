$ErrorActionPreference = "Stop"

function Run-GlobalpingMeasurement {
    param(
        [string]$Target,
        [string]$Protocol,
        [int]$Port = 0
    )

    $options = @{
        protocol = $Protocol
    }
    if ($Port -gt 0) {
        $options.port = $Port
    }

    $body = @{
        type = "traceroute"
        target = $Target
        measurementOptions = $options
        limit = 1
    } | ConvertTo-Json -Depth 5

    try {
        $postResp = Invoke-RestMethod -Uri "https://api.globalping.io/v1/measurements" -Method Post -Body $body -ContentType "application/json"
        $measId = $postResp.id

        # Poll for completion
        $attempts = 0
        $result = $null
        while ($attempts -lt 8) {
            Start-Sleep -Seconds 3
            $result = Invoke-RestMethod -Uri "https://api.globalping.io/v1/measurements/$measId"
            if ($result.status -eq "finished") {
                break
            }
            $attempts++
        }

        if ($result -and $result.results -and $result.results.Count -gt 0) {
            $probeResult = $result.results[0]
            $probe = $probeResult.probe
            $res = $probeResult.result

            $hopCount = if ($res.hops) { $res.hops.Count } else { 0 }
            $lastHop = if ($res.hops -and $res.hops.Count -gt 0) { $res.hops[-1] } else { $null }
            $lastRtt = if ($lastHop -and $lastHop.timings -and $lastHop.timings.Count -gt 0) {
                [math]::Round($lastHop.timings[0].rtt, 2)
            } else { "N/A" }

            return [PSCustomObject]@{
                Target = $Target
                Protocol = $Protocol
                Port = if ($Port -gt 0) { $Port } else { "default" }
                Family = if ($Target -match ":") { "IPv6" } else { "IPv4" }
                ProbeLocation = "$($probe.city), $($probe.country)"
                ASN = $probe.asn
                TotalHops = $hopCount
                FinalHopIP = if ($lastHop) { $lastHop.resolvedAddress } else { "Unknown" }
                FinalRTT_ms = $lastRtt
                Status = $res.status
            }
        }
    } catch {
        Write-Warning "Failed measurement for $Target ($Protocol): $_"
        return [PSCustomObject]@{
            Target = $Target
            Protocol = $Protocol
            Port = $Port
            Family = if ($Target -match ":") { "IPv6" } else { "IPv4" }
            ProbeLocation = "Error"
            ASN = "N/A"
            TotalHops = 0
            FinalHopIP = "N/A"
            FinalRTT_ms = "N/A"
            Status = "Failed: $_"
        }
    }
}

Write-Host "Running Globalping Matrix Verification (ICMP, TCP, UDP on IPv4 and IPv6)..." -ForegroundColor Cyan

$tests = @(
    @{ Target = "1.1.1.1"; Protocol = "ICMP"; Port = 0 },
    @{ Target = "2606:4700:4700::1111"; Protocol = "ICMP"; Port = 0 },
    @{ Target = "1.1.1.1"; Protocol = "TCP"; Port = 80 },
    @{ Target = "2606:4700:4700::1111"; Protocol = "TCP"; Port = 443 },
    @{ Target = "1.1.1.1"; Protocol = "UDP"; Port = 53 },
    @{ Target = "2606:4700:4700::1111"; Protocol = "UDP"; Port = 53 }
)

$results = @()
foreach ($t in $tests) {
    Write-Host "Testing $($t.Protocol) -> $($t.Target) (Port: $($t.Port))..." -ForegroundColor Yellow
    $res = Run-GlobalpingMeasurement -Target $t.Target -Protocol $t.Protocol -Port $t.Port
    $results += $res
}

Write-Host "`n=== GLOBALPING VERIFICATION RESULTS ===" -ForegroundColor Green
$results | Format-Table -AutoSize

$results | Export-Clixml -Path "C:\mtr\artifacts\globalping_verification_results.xml"
$results | ConvertTo-Json -Depth 4 | Set-Content "C:\mtr\artifacts\globalping_verification_results.json"
Write-Host "Verification results saved to C:\mtr\artifacts\globalping_verification_results.json" -ForegroundColor Green
