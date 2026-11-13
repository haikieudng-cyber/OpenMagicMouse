<#
.SYNOPSIS
    Gỡ bỏ OpenMagicMouseService, Scheduled Task và tùy chọn gỡ luôn driver Apple Boot Camp.
#>
param(
    [switch]$IncludeAppleDriver
)

$ErrorActionPreference = 'SilentlyContinue'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Vui lòng chạy PowerShell bằng quyền Administrator (Run as Administrator)."
    exit 1
}

Stop-Process -Name 'OpenMagicMouse' -Force
Unregister-ScheduledTask -TaskName 'OpenMagicMouseUI' -Confirm:$false
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'OpenMagicMouse'
Remove-Item -Path (Join-Path $HOME 'Desktop\OpenMagicMouse.lnk') -Force

if (Get-Service -Name 'OpenMagicMouseService') {
    sc.exe stop OpenMagicMouseService | Out-Null
    Start-Sleep -Milliseconds 600
    logman stop OpenMagicMouseBthSession -ets | Out-Null
    sc.exe delete OpenMagicMouseService | Out-Null
}
Remove-Item -Path 'C:\Windows\System32\OpenMagicMouseBthSession' -Force

if ($IncludeAppleDriver) {
    $mmHidDevices = Get-PnpDevice -Class HIDClass | Where-Object {
        $_.InstanceId -match 'VID&0001004C_PID&0269|VID_004C&PID_0269'
    }
    foreach ($dev in $mmHidDevices) {
        $regPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($dev.InstanceId)"
        $curFilters = @((Get-ItemProperty -Path $regPath -Name 'LowerFilters').LowerFilters) | Where-Object { $_ }
        if ($curFilters -contains 'applewirelessmouse') {
            $newFilters = @($curFilters | Where-Object { $_ -ne 'applewirelessmouse' })
            if ($newFilters.Count -eq 0) {
                Remove-ItemProperty -Path $regPath -Name 'LowerFilters'
            } else {
                Set-ItemProperty -Path $regPath -Name 'LowerFilters' -Value $newFilters -Type MultiString
            }
            pnputil /restart-device "$($dev.InstanceId)" | Out-Null
        }
    }

    if (Get-Service -Name 'applewirelessmouse') {
        sc.exe stop applewirelessmouse | Out-Null
        sc.exe delete applewirelessmouse | Out-Null
    }
    Remove-Item -Path "$env:SystemRoot\System32\drivers\applewirelessmouse.sys" -Force
    Write-Output "Đã gỡ hoàn toàn OpenMagicMouseService và driver Apple Wireless Mouse."
}
else {
    Write-Output "Đã gỡ OpenMagicMouseService và mục tự khởi động (giữ nguyên driver Apple WHQL)."
}
