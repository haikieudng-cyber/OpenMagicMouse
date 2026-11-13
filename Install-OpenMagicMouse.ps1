<#
.SYNOPSIS
    Cài đặt hoặc cập nhật OpenMagicMouse 2.2 (Driver Apple Boot Camp WHQL + Dịch vụ đa điểm 125Hz + Tự động chạy khi đăng nhập).
#>
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Vui lòng chạy PowerShell bằng quyền Administrator (Run as Administrator)."
    exit 1
}

$kitDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $kitDir 'OpenMagicMouse\OpenMagicMouse.csproj'
$exe = Join-Path $kitDir 'OpenMagicMouse.exe'
$infSrc = Join-Path $kitDir 'AppleBootCampDriver\AppleWirelessMouse.inf'
$sysSrc = Join-Path $kitDir 'AppleBootCampDriver\AppleWirelessMouse.sys'
$sysDst = 'C:\Windows\System32\drivers\applewirelessmouse.sys'

# 1. Cài/khởi động driver Apple Boot Camp WHQL & gắn LowerFilters cho Magic Mouse 2 (PID_0269)
if ((Test-Path $infSrc) -and (-not (Test-Path $sysDst))) {
    pnputil /add-driver "$infSrc" /install 2>&1 | Out-Null
}
if ((Test-Path $sysSrc) -and (-not (Test-Path $sysDst))) {
    Copy-Item -Path $sysSrc -Destination $sysDst -Force
}
if (-not (Get-Service -Name 'applewirelessmouse' -ErrorAction SilentlyContinue)) {
    sc.exe create applewirelessmouse type= kernel start= demand error= normal binPath= "System32\drivers\applewirelessmouse.sys" DisplayName= "Apple Wireless Mouse Filter Driver" | Out-Null
}
sc.exe start applewirelessmouse 2>&1 | Out-Null

$mmHidDevices = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object {
    $_.InstanceId -match 'VID&0001004C_PID&0269|VID_004C&PID_0269'
}
foreach ($dev in $mmHidDevices) {
    $regPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($dev.InstanceId)"
    if (Test-Path $regPath) {
        $curFilters = @((Get-ItemProperty -Path $regPath -Name 'LowerFilters' -ErrorAction SilentlyContinue).LowerFilters) | Where-Object { $_ }
        if ($curFilters -notcontains 'applewirelessmouse') {
            $newFilters = @($curFilters + 'applewirelessmouse')
            Set-ItemProperty -Path $regPath -Name 'LowerFilters' -Value $newFilters -Type MultiString
            pnputil /restart-device "$($dev.InstanceId)" 2>&1 | Out-Null
        }
    }
}

# 2. Bật quyền ghi nhận gói HID đầy đủ cho BTHPORT ETW Event 402 (chống Windows 11 cắt xén tọa độ cảm ứng)
$bthParamKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters'
if (-not (Test-Path $bthParamKey)) {
    New-Item -Path $bthParamKey -Force | Out-Null
}
$prevSensitive = (Get-ItemProperty -Path $bthParamKey -Name 'EtwLogSensitiveData' -ErrorAction SilentlyContinue).EtwLogSensitiveData
$prevDrop = (Get-ItemProperty -Path $bthParamKey -Name 'EtwDropLargeEvents' -ErrorAction SilentlyContinue).EtwDropLargeEvents
$needRadioRestart = ($prevSensitive -ne 1 -or $prevDrop -ne 0)

New-ItemProperty -Path $bthParamKey -Name 'EtwLogSensitiveData' -PropertyType DWord -Value 1 -Force | Out-Null
New-ItemProperty -Path $bthParamKey -Name 'EtwDropLargeEvents' -PropertyType DWord -Value 0 -Force | Out-Null
New-ItemProperty -Path $bthParamKey -Name 'MaxEtwBytes' -PropertyType DWord -Value 4096 -Force | Out-Null

# 3. Dừng bản đang chạy (nếu có), build từ mã nguồn nếu có .NET SDK, và dọn file ETW rò rỉ cũ
$uiRunning = Get-Process -Name 'OpenMagicMouse' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }
if ($uiRunning) {
    $uiRunning | Stop-Process -Force -ErrorAction SilentlyContinue
}
if (Get-Service -Name 'OpenMagicMouseService' -ErrorAction SilentlyContinue) {
    sc.exe stop OpenMagicMouseService 2>&1 | Out-Null
    for ($i = 0; $i -lt 25; $i++) {
        if (-not (Get-Process -Name 'OpenMagicMouse' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 200
    }
    logman stop OpenMagicMouseBthSession -ets 2>&1 | Out-Null
    sc.exe delete OpenMagicMouseService | Out-Null
    Start-Sleep -Milliseconds 400
}

if ((Test-Path $proj) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    dotnet build $proj -c Release | Out-Null
    $bin = Join-Path $kitDir 'OpenMagicMouse\bin\Release\net10.0-windows'
    foreach ($extFile in @('OpenMagicMouse.exe', 'OpenMagicMouse.dll', 'OpenMagicMouse.deps.json', 'OpenMagicMouse.runtimeconfig.json')) {
        $srcFile = Join-Path $bin $extFile
        if (Test-Path $srcFile) {
            Copy-Item -Path $srcFile -Destination $kitDir -Force
        }
    }
}
elseif (-not (Test-Path $exe)) {
    Write-Error "Chưa có OpenMagicMouse.exe và máy chưa cài .NET 10 SDK. Hãy tải bản dựng sẵn (OpenMagicMouse-win-x64.zip) ở GitHub Releases hoặc cài .NET 10 SDK bằng lệnh: winget install Microsoft.DotNet.SDK.10"
    exit 1
}

Remove-Item 'C:\Windows\System32\OpenMagicMouseBthSession' -Force -ErrorAction SilentlyContinue

# 4. Cài đặt & khởi động OpenMagicMouseService (LocalSystem)
$binPath = "`"$exe`" --service"
sc.exe create OpenMagicMouseService binPath= $binPath start= auto DisplayName= "OpenMagicMouse Multi-Touch Service" | Out-Null
sc.exe description OpenMagicMouseService "Open-source real-time Bluetooth HID multi-touch stream service for Apple Magic Mouse 2" | Out-Null
sc.exe start OpenMagicMouseService | Out-Null

# 5. Khởi động lại USB Bluetooth Adapter nếu vừa mới bật EtwLogSensitiveData=1
if ($needRadioRestart) {
    $radios = Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like 'USB\*' -and $_.Status -eq 'OK' }
    foreach ($radio in $radios) {
        pnputil /restart-device "$($radio.InstanceId)" | Out-Null
    }
    Start-Sleep -Seconds 3
}

# 6. Đăng ký Scheduled Task High-Integrity (RunLevel Highest, chạy ngầm ở khay hệ thống, không hỏi UAC)
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'OpenMagicMouse' -ErrorAction SilentlyContinue

$taskName = 'OpenMagicMouseUI'
$action = New-ScheduledTaskAction -Execute $exe -Argument '--tray' -WorkingDirectory $kitDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

Start-ScheduledTask -TaskName $taskName
Start-Sleep -Milliseconds 1000

# 7. Tạo shortcut ngoài Desktop nếu chưa có
$desktopExe = Join-Path $HOME 'Desktop\OpenMagicMouse.exe'
$desktopLnk = Join-Path $HOME 'Desktop\OpenMagicMouse.lnk'
if ((-not (Test-Path $desktopExe)) -and (-not (Test-Path $desktopLnk))) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $sc = $shell.CreateShortcut($desktopLnk)
        $sc.TargetPath = $exe
        $sc.WorkingDirectory = $kitDir
        $sc.Description = 'OpenMagicMouse Control Center'
        $sc.Save()
    }
    catch {
        # Non-fatal
    }
}

Write-Output "Đã cài đặt và khởi động thành công OpenMagicMouse 2.2 (Cuộn quán tính 125Hz + Đa điểm chuẩn Mac)!"
Write-Output "Biểu tượng OpenMagicMouse đang chạy ngầm ở khay hệ thống (góc phải dưới)."
