<#
.SYNOPSIS
    Data Migration module for the Migration Validation Pipeline.

.DESCRIPTION
    Invokes the DataMigrator CLI as a subprocess to replicate table data from
    SQL Server to PostgreSQL. Parses stdout for migration results and handles
    timeout, early-skip (no tables), and stderr capture on failure.

.NOTES
    Requirements: 3.1, 3.2, 3.3, 3.4, 3.5
#>

function Invoke-DataMigration {
    <#
    .SYNOPSIS
        Invokes the DataMigrator CLI to replicate data from SQL Server to PostgreSQL.

    .PARAMETER SourceConnectionString
        SQL Server connection string for the source database.

    .PARAMETER TargetConnectionString
        PostgreSQL connection string for the destination database.

    .PARAMETER SessionPath
        Path to the session directory containing extracted objects metadata.

    .PARAMETER DataMigratorProjectPath
        Path to the DataMigrator .NET project directory.

    .PARAMETER TimeoutSeconds
        Maximum time in seconds to wait for the DataMigrator process. Default: 120.

    .OUTPUTS
        Hashtable with: tablesSucceeded, tablesFailed, totalRows, elapsed, rawOutput, status
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SourceConnectionString,

        [Parameter(Mandatory)]
        [string]$TargetConnectionString,

        [Parameter(Mandatory)]
        [string]$SessionPath,

        [Parameter(Mandatory)]
        [string]$DataMigratorProjectPath,

        [int]$TimeoutSeconds = 120
    )

    # Check if session has tables — if not, return skipped status early
    $hasTables = Test-SessionHasTables -SessionPath $SessionPath
    if (-not $hasTables) {
        Write-PipelineLog -Level "INFO" -Step "data-migration" -Message "No tables found in session '$SessionPath'. Skipping data migration."
        return @{
            tablesSucceeded = 0
            tablesFailed    = 0
            totalRows       = 0
            elapsed         = 0
            rawOutput       = ""
            status          = "skipped"
        }
    }

    Write-PipelineLog -Level "INFO" -Step "data-migration" -Message "Starting data migration from session '$SessionPath'"

    # Build arguments for dotnet run
    $migratorArgs = @(
        "run", "--project", $DataMigratorProjectPath, "--",
        "--source", $SourceConnectionString,
        "--target", $TargetConnectionString,
        "--session", $SessionPath,
        "--truncate", "--disable-fk", "--reseed"
    )

    # Configure the process
    $processInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processInfo.FileName = "dotnet"
    $processInfo.Arguments = ($migratorArgs | ForEach-Object {
        if ($_ -match '\s') { "`"$_`"" } else { $_ }
    }) -join ' '
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $process = [System.Diagnostics.Process]::Start($processInfo)

        # Read stdout and stderr asynchronously to avoid deadlocks
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        # Wait for process with timeout
        $timeoutMs = $TimeoutSeconds * 1000
        $exited = $process.WaitForExit($timeoutMs)

        if (-not $exited) {
            # Process exceeded timeout — kill it
            try { $process.Kill() } catch { }
            $stopwatch.Stop()

            $partialStdout = if ($stdoutTask.IsCompleted) { $stdoutTask.Result } else { "" }
            $partialStderr = if ($stderrTask.IsCompleted) { $stderrTask.Result } else { "" }

            $errMsg = "DataMigrator process timed out after $TimeoutSeconds seconds"
            if ($partialStderr) { $errMsg += ": $($partialStderr.Trim())" }

            Write-PipelineLog -Level "ERROR" -Step "data-migration" -Message $errMsg

            return @{
                tablesSucceeded = 0
                tablesFailed    = 0
                totalRows       = 0
                elapsed         = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
                rawOutput       = $partialStdout
                status          = "timeout"
                errorMessage    = $errMsg
            }
        }

        # Process completed — read output
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $exitCode = $process.ExitCode
        $stopwatch.Stop()

        if ($exitCode -ne 0) {
            $errMsg = if ($stderr) { $stderr.Trim() } else { "DataMigrator exited with code $exitCode" }
            Write-PipelineLog -Level "ERROR" -Step "data-migration" -Message "DataMigrator failed: $errMsg"

            # Still attempt to parse partial results from stdout
            $parsed = ConvertFrom-DataMigratorOutput -Output $stdout
            $parsed.elapsed = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
            $parsed.rawOutput = $stdout
            $parsed.status = "failed"
            $parsed.errorMessage = $errMsg
            return $parsed
        }

        # Parse successful output
        $parsed = ConvertFrom-DataMigratorOutput -Output $stdout
        $parsed.elapsed = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
        $parsed.rawOutput = $stdout
        $parsed.status = "completed"

        Write-PipelineLog -Level "INFO" -Step "data-migration" -Message "Data migration completed: $($parsed.tablesSucceeded) tables succeeded, $($parsed.tablesFailed) failed, $($parsed.totalRows) total rows in $($parsed.elapsed)s"

        return $parsed
    }
    catch {
        $stopwatch.Stop()
        $errMsg = "Exception invoking DataMigrator: $($_.Exception.Message)"
        Write-PipelineLog -Level "ERROR" -Step "data-migration" -Message $errMsg

        return @{
            tablesSucceeded = 0
            tablesFailed    = 0
            totalRows       = 0
            elapsed         = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
            rawOutput       = ""
            status          = "failed"
            errorMessage    = $errMsg
        }
    }
}

function Test-SessionHasTables {
    <#
    .SYNOPSIS
        Checks whether the session directory contains any table objects.
    .OUTPUTS
        Boolean indicating if tables exist in the session.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$SessionPath
    )

    $objectsPath = Join-Path $SessionPath "objects"
    if (-not (Test-Path $objectsPath)) {
        return $false
    }

    # Look for .Table.json files in the objects directory
    $tableFiles = Get-ChildItem -Path $objectsPath -Filter "*.Table.json" -ErrorAction SilentlyContinue
    return ($null -ne $tableFiles -and $tableFiles.Count -gt 0)
}

function ConvertFrom-DataMigratorOutput {
    <#
    .SYNOPSIS
        Parses DataMigrator stdout to extract migration statistics.
    .DESCRIPTION
        Uses regex patterns to extract tables succeeded, tables failed,
        total rows, and elapsed time from the DataMigrator output.
        Returns defaults for any values that cannot be parsed.
    .OUTPUTS
        Hashtable with: tablesSucceeded, tablesFailed, totalRows, elapsed
    #>
    [CmdletBinding()]
    param(
        [string]$Output
    )

    $result = @{
        tablesSucceeded = 0
        tablesFailed    = 0
        totalRows       = 0
        elapsed         = 0
    }

    if ([string]::IsNullOrWhiteSpace($Output)) {
        return $result
    }

    # Parse tables succeeded (patterns: "X tables succeeded", "X table(s) succeeded", "succeeded: X")
    if ($Output -match '(\d+)\s+tables?\s+succeeded') {
        $result.tablesSucceeded = [int]$Matches[1]
    }
    elseif ($Output -match 'succeeded[:\s]+(\d+)') {
        $result.tablesSucceeded = [int]$Matches[1]
    }

    # Parse tables failed (patterns: "X tables failed", "X table(s) failed", "failed: X")
    if ($Output -match '(\d+)\s+tables?\s+failed') {
        $result.tablesFailed = [int]$Matches[1]
    }
    elseif ($Output -match 'failed[:\s]+(\d+)') {
        $result.tablesFailed = [int]$Matches[1]
    }

    # Parse total rows (patterns: "X total rows", "X rows", "rows: X", "total rows: X")
    if ($Output -match '(\d+)\s+total\s+rows') {
        $result.totalRows = [int]$Matches[1]
    }
    elseif ($Output -match 'total\s+rows[:\s]+(\d+)') {
        $result.totalRows = [int]$Matches[1]
    }
    elseif ($Output -match '(\d+)\s+rows?\s+(migrated|copied|transferred)') {
        $result.totalRows = [int]$Matches[1]
    }

    # Parse elapsed time (patterns: "elapsed: Xs", "elapsed: X.Xs", "X seconds", "Xs elapsed")
    if ($Output -match 'elapsed[:\s]+(\d+\.?\d*)\s*s') {
        $result.elapsed = [double]$Matches[1]
    }
    elseif ($Output -match '(\d+\.?\d*)\s*s(?:econds?)?\s+elapsed') {
        $result.elapsed = [double]$Matches[1]
    }
    elseif ($Output -match 'in\s+(\d+\.?\d*)\s*s') {
        $result.elapsed = [double]$Matches[1]
    }

    return $result
}

# Export module functions when loaded as a module
if ($MyInvocation.MyCommand.ScriptBlock.Module) {
    Export-ModuleMember -Function Invoke-DataMigration
}
