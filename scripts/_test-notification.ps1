$PsqlExe = 'D:\PostgreSQL\18\bin\psql.exe'
$Db      = 'host=localhost port=5432 dbname=raizen user=postgres password=admin'

# Use a temp file for the SELECT so double-quote column names are preserved
$selectSql = 'SELECT "Id" FROM elevation_requests ORDER BY "SubmittedAt" DESC LIMIT 1'
$tmpSelect = "$env:TEMP\rz_select.sql"
[System.IO.File]::WriteAllText($tmpSelect, $selectSql)

Write-Host 'Finding most recent request...' -ForegroundColor Cyan
$reqId = (& $PsqlExe -t -A -f $tmpSelect $Db).Trim()
Remove-Item $tmpSelect -ErrorAction SilentlyContinue

if (!$reqId -or $reqId -match 'ERROR' -or $reqId.Length -lt 10) {
    Write-Host "No requests found (got: '$reqId'). Submit a request from the tray first." -ForegroundColor Red
    exit 1
}

Write-Host "Using request: $reqId" -ForegroundColor Gray

$insertSql = @"
INSERT INTO request_comments ("Id","RequestId","AuthorUpn","AuthorDisplayName","IsAdmin","Body","CreatedAt")
VALUES (gen_random_uuid(),'$reqId','admin@raizen.local','Raizen Admin',true,'Balloon test - can you see this popup?',now());
"@

$tmpInsert = "$env:TEMP\rz_insert.sql"
[System.IO.File]::WriteAllText($tmpInsert, $insertSql)

Write-Host 'Inserting admin comment...' -ForegroundColor Cyan
& $PsqlExe -f $tmpInsert $Db
Remove-Item $tmpInsert -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Done. Watch for a balloon tip from the tray within 30 seconds.' -ForegroundColor Green
