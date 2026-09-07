$ErrorActionPreference = 'SilentlyContinue'
# CPU 增量采样 5s
$a = @{}
foreach ($p in Get-Process) { if (-not $a.ContainsKey($p.Id)) { $a[$p.Id] = $p.TotalProcessorTime.TotalSeconds } }
$t0 = Get-Date
Start-Sleep -Seconds 5
$t1 = Get-Date
$span = ($t1 - $t0).TotalSeconds
$cores = [Environment]::ProcessorCount
$rows = @()
foreach ($p in Get-Process) {
    if ($a.ContainsKey($p.Id)) {
        $d = $p.TotalProcessorTime.TotalSeconds - $a[$p.Id]
        if ($d -gt 0.1) {
            $rows += [PSCustomObject]@{ Name = $p.ProcessName; PID = $p.Id; Cores = [math]::Round($d / $span, 2); Pct = [math]::Round($d / $span / $cores * 100, 1); WS_MB = [math]::Round($p.WorkingSet64 / 1MB) }
        }
    }
}
"===== CPU TOP (5s采样, $cores 逻辑核) ====="
$rows | Sort-Object Cores -Descending | Select-Object -First 15 | Format-Table -AutoSize

"===== 内存 TOP (按进程名聚合 WorkingSet) ====="
Get-Process | Group-Object ProcessName | ForEach-Object {
    [PSCustomObject]@{ Name = $_.Name; Count = $_.Count; WS_MB = [math]::Round(($_.Group | Measure-Object WorkingSet64 -Sum).Sum / 1MB); Private_MB = [math]::Round(($_.Group | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB) }
} | Sort-Object WS_MB -Descending | Select-Object -First 15 | Format-Table -AutoSize

$os = Get-CimInstance Win32_OperatingSystem
$commit = (Get-Counter '\Memory\Committed Bytes').CounterSamples[0].CookedValue / 1MB
$commitLimit = (Get-Counter '\Memory\Commit Limit (KB)').CounterSamples[0].CookedValue / 1KB
"===== 系统 ====="
"物理内存: 总 {0:N1} GB / 可用 {1:N1} GB" -f ($os.TotalVisibleMemorySize / 1MB), ($os.FreePhysicalMemory / 1MB)
"页面文件提交: {0:N0} MB / 上限 {1:N0} MB ({2:P0})" -f $commit, $commitLimit, ($commit / $commitLimit)
$pf = Get-CimInstance Win32_PageFileUsage
foreach ($f in $pf) { "页面文件: $($f.Name) 当前 {0} MB / 峰值 {1} MB" -f $f.CurrentUsage, $f.PeakUsage }
