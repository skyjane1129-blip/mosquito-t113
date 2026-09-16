# One-shot health check before (and during) a customer demo. Prints PASS / WARN / FAIL per item and exits 1
# when anything FAILs. Run:  pwsh -File scripts\Check-DemoReadiness.ps1
# Reads the operator password from MOSQUITO_DEMO_PASSWORD or appsettings.json; prints no secrets.
param([string]$DeviceId = 'MQ-SH-001')
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$api = Join-Path $root 'src\Mosquito.Cloud.Api'
$fail = 0; $warn = 0
function Report($state, $item, $detail) {
    $script:fail += [int]($state -eq 'FAIL'); $script:warn += [int]($state -eq 'WARN')
    Write-Output ("[{0,-4}] {1,-28} {2}" -f $state, $item, $detail)
}

$cfg = Get-Content -Raw (Join-Path $api 'appsettings.json') | ConvertFrom-Json
$public = $cfg.Storage.PublicBaseUrl.TrimEnd('/')
$password = $env:MOSQUITO_DEMO_PASSWORD; if (-not $password) { $password = $cfg.Auth.Password }

# 1. processes
$apiProc = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like '*Mosquito.Cloud.Api.dll*' }
if ($apiProc) { Report PASS 'API 进程' "PID $($apiProc.ProcessId) 自 $($apiProc.CreationDate.ToString('MM-dd HH:mm'))" } else { Report FAIL 'API 进程' '未运行（Start-DemoServer.ps1）' }
$frpc = Get-Process frpc -ErrorAction SilentlyContinue
if ($frpc) { Report PASS 'frpc 隧道进程' "PID $($frpc.Id)" } else { Report FAIL 'frpc 隧道进程' '未运行' }

# 2. health local + public
foreach ($pair in @(@('本地健康检查', 'http://127.0.0.1:5080/health'), @('公网健康检查', "$public/health"))) {
    try { $h = Invoke-RestMethod -Proxy $null -TimeoutSec 8 -Uri $pair[1]; Report PASS $pair[0] "$($h.status) repo=$($h.repository) storage=$($h.storage)" }
    catch { Report FAIL $pair[0] $_.Exception.Message }
}

# 3. login + device + analysis runtime
$token = $null
try {
    $login = Invoke-RestMethod -Proxy $null -TimeoutSec 10 -Method Post -Uri "$public/api/auth/login" -ContentType 'application/json' -Body (@{username=$cfg.Auth.Username;password=$password}|ConvertTo-Json)
    $token = $login.accessToken; Report PASS '操作员登录' "账号 $($cfg.Auth.Username)，令牌有效至 $($login.expiresAt)"
} catch { Report FAIL '操作员登录' $_.Exception.Message }
if ($token) {
    $h = @{Authorization="Bearer $token"}
    try {
        $devices = Invoke-RestMethod -Proxy $null -TimeoutSec 10 -Headers $h -Uri "$public/api/v2/devices"
        $d = $devices.items | Where-Object deviceId -eq $DeviceId
        if (-not $d) { Report FAIL "设备 $DeviceId" '服务端未登记' }
        elseif ($d.online) { Report PASS "设备 $DeviceId" "在线，心跳 $($d.lastSeenAtUtc)，电池 $($d.lastPower.batteryMv) mV $($d.lastPower.chargeState)，镜像 $($d.imageVersion)" }
        else { Report WARN "设备 $DeviceId" "离线，最近心跳 $($d.lastSeenAtUtc)（上电后约 2.5 分钟恢复）" }
        if ($d -and $d.lastPower.batteryMv -lt 3700) { Report WARN '板子电池' "$($d.lastPower.batteryMv) mV 偏低，演示前充满" }
    } catch { Report FAIL '设备目录' $_.Exception.Message }
}
$py = Join-Path $root 'ai\python311\python.exe'
$model = Join-Path $root $cfg.Analysis.ModelPath
if ((Test-Path $py) -and (Test-Path $model)) {
    $v = & $py -c "import ultralytics, torch; print(ultralytics.__version__, torch.__version__)" 2>&1
    if ($LASTEXITCODE -eq 0) { Report PASS '蚊卵识别运行时' "python311 + ultralytics/torch $v" } else { Report FAIL '蚊卵识别运行时' "$v" }
} else { Report FAIL '蚊卵识别运行时' "缺 $py 或 $model" }

# 4. machine
$os = Get-CimInstance Win32_OperatingSystem
$freeGb = [math]::Round($os.FreePhysicalMemory/1MB, 1)
if ($freeGb -ge 3) { Report PASS '空闲内存' "$freeGb GB" } elseif ($freeGb -ge 1.5) { Report WARN '空闲内存' "$freeGb GB（识别需约 1 GB，关掉 EDA/浏览器/聊天软件）" } else { Report FAIL '空闲内存' "$freeGb GB" }
$disk = Get-PSDrive C; $diskGb = [math]::Round($disk.Free/1GB, 1)
if ($diskGb -ge 5) { Report PASS 'C 盘空闲' "$diskGb GB" } else { Report FAIL 'C 盘空闲' "$diskGb GB" }
$sleep = (powercfg /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE | Select-String 'AC.*0x|交流.*0x').Line -replace '.*0x', '0x'
if ($sleep -eq '0x00000000') { Report PASS '接电源时睡眠' '永不' } else { Report FAIL '接电源时睡眠' "$sleep（powercfg /change standby-timeout-ac 0）" }
$hib = (powercfg /query SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE | Select-String 'AC.*0x|交流.*0x').Line -replace '.*0x', '0x'
if ($hib -eq '0x00000000') { Report PASS '接电源时休眠' '永不' } else { Report FAIL '接电源时休眠' "$hib（powercfg /change hibernate-timeout-ac 0）" }
foreach ($t in 'Mosquito Demo Server', 'Mosquito Demo Backup') {
    $task = Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
    if ($task) { Report PASS "计划任务 $t" "$($task.State)" } else { Report FAIL "计划任务 $t" '未注册（Register-DemoServerAutostart.ps1）' }
}
$wu = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' -ErrorAction SilentlyContinue
if ($wu.PauseUpdatesExpiryTime -and ([datetime]$wu.PauseUpdatesExpiryTime) -gt (Get-Date)) { Report PASS 'Windows 更新' "已暂停到 $($wu.PauseUpdatesExpiryTime)" } else { Report WARN 'Windows 更新' '未暂停：设置 → Windows 更新 → 暂停更新 1 周（防自动重启）' }
$backup = Get-ChildItem (Join-Path $root 'artifacts\backups') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
if ($backup -and $backup.LastWriteTime -gt (Get-Date).AddHours(-26)) { Report PASS '数据库备份' "最近 $($backup.Name)" } elseif ($backup) { Report WARN '数据库备份' "最近 $($backup.Name) 已超过 26 小时" } else { Report WARN '数据库备份' '还没有备份（Backup-DemoData.ps1）' }
$installer = Get-Item (Join-Path (Split-Path $root) 'mosquito-windows-client\artifacts\installer\MosquitoCapture-Setup-1.0.0.exe') -ErrorAction SilentlyContinue
if ($installer) { Report PASS '客户端安装包' "$($installer.LastWriteTime.ToString('MM-dd HH:mm')) $([math]::Round($installer.Length/1MB,1)) MB" } else { Report WARN '客户端安装包' '未找到' }

Write-Output ""
Write-Output ("结果：{0} 项 FAIL，{1} 项 WARN" -f $fail, $warn)
exit ([int]($fail -gt 0))
