$hklm = [Microsoft.Win32.Registry]::LocalMachine
$k = $hklm.OpenSubKey('SOFTWARE\Classes\exefile\shell\Raizen')
if ($k) {
    Write-Output ("Parent MUIVerb: " + $k.GetValue('MUIVerb'))
    Write-Output ("SubCommands:   " + $k.GetValue('SubCommands'))
    $shell = $hklm.OpenSubKey('SOFTWARE\Classes\exefile\shell\Raizen\shell')
    if ($shell) {
        Write-Output ("Verb subkeys:  " + ($shell.GetSubKeyNames() -join ', '))
        foreach ($name in $shell.GetSubKeyNames()) {
            $vk = $shell.OpenSubKey($name)
            Write-Output ("  [$name] MUIVerb = " + $vk.GetValue('MUIVerb'))
            $ck = $shell.OpenSubKey("$name\command")
            if ($ck) { Write-Output ("  [$name] command = " + $ck.GetValue('')) }
            $vk.Close()
        }
        $shell.Close()
    } else { Write-Output 'ERROR: No shell\ subkey found!' }
    $k.Close()
} else { Write-Output 'ERROR: Raizen key missing from HKLM\SOFTWARE\Classes\exefile\shell' }
