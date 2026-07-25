<#
.SYNOPSIS
    AI-Assisted Fix Loop module for the Migration Validation Pipeline.

.DESCRIPTION
    Orchestrates the AI-assisted iterative fix cycle for DDL statements that failed
    to apply to the PostgreSQL destination database. Invokes the SchemaConversion.Cli
    `fix` command for each failed object and parses the JSON result.

.NOTES
    Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7
#>

function Invoke-FixLoop {
    <#
    .SYNOPSIS
        Attempts AI-assisted correction for DDL statements that failed to apply.

    .PARAMETER FailedObjects
        Array of objects with properties: objectName, ddl, errorMessage, sourceTSql

    .PARAMETER PgConnectionString
        PostgreSQL connection string for the destination database where corrected DDL will be re-applied.

    .PARAMETER MaxAttempts
        Maximum number of fix attempts per object. Default: 2.

    .PARAMETER CliProjectPath
        Path to the SchemaConversion.Cli project directory.

    .OUTPUTS
        Array of objects with: objectName, finalStatus (fixed/unfixable), attempts, fixedDdl, explanation, errors
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [array]$FailedObjects,

        [Parameter(Mandatory)]
        [string]$PgConnectionString,

        [int]$MaxAttempts = 2,

        [Parameter(Mandatory)]
        [string]$CliProjectPath
    )

    $results = @()

    foreach ($failedObj in $FailedObjects) {
        $objectName = $failedObj.objectName

        Write-PipelineLog -Level "INFO" -Step "fix-loop" -Message "Starting fix loop for '$objectName' (max $MaxAttempts attempts)"

        $result = Invoke-FixCliCommand `
            -ObjectName $objectName `
            -FailedDdl $failedObj.ddl `
            -ErrorMessage $failedObj.errorMessage `
            -SourceTSql $failedObj.sourceTSql `
            -PgConnectionString $PgConnectionString `
            -MaxAttempts $MaxAttempts `
            -CliProjectPath $CliProjectPath

        $results += $result
    }

    return $results
}

function Invoke-FixCliCommand {
    <#
    .SYNOPSIS
        Invokes the SchemaConversion.Cli fix command for a single failed object.
    .OUTPUTS
        Object with: objectName, finalStatus, attempts, fixedDdl, explanation, errors
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ObjectName,

        [Parameter(Mandatory)]
        [string]$FailedDdl,

        [Parameter(Mandatory)]
        [string]$ErrorMessage,

        [Parameter(Mandatory)]
        [string]$SourceTSql,

        [Parameter(Mandatory)]
        [string]$PgConnectionString,

        [Parameter(Mandatory)]
        [int]$MaxAttempts,

        [Parameter(Mandatory)]
        [string]$CliProjectPath
    )

    $result = [PSCustomObject]@{
        objectName  = $ObjectName
        finalStatus = "unfixable"
        attempts    = 0
        fixedDdl    = $null
        explanation = $null
        errors      = @()
    }

    try {
        # Build arguments for dotnet run -- fix command
        $fixArgs = @(
            "run", "--project", $CliProjectPath, "--",
            "fix",
            "--failed-ddl", $FailedDdl,
            "--error", $ErrorMessage,
            "--source-tsql", $SourceTSql,
            "--pg-connection", $PgConnectionString,
            "--max-attempts", $MaxAttempts.ToString()
        )

        # Quote arguments containing spaces (same pattern as Invoke-PipelineStep)
        $processInfo = New-Object System.Diagnostics.ProcessStartInfo
        $processInfo.FileName = "dotnet"
        $processInfo.Arguments = ($fixArgs | ForEach-Object {
            if ($_ -match '\s') { "`"$_`"" } else { $_ }
        }) -join ' '
        $processInfo.RedirectStandardOutput = $true
        $processInfo.RedirectStandardError = $true
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $true

        Write-PipelineLog -Level "INFO" -Step "fix-loop" -Message "Invoking fix CLI for '$ObjectName'"

        $process = [System.Diagnostics.Process]::Start($processInfo)

        # Read stdout and stderr asynchronously to avoid deadlock
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        $exitCode = $process.ExitCode

        if ($exitCode -ne 0) {
            # Non-zero exit code — treat as unfixable
            $errMsg = if ($stderr) { $stderr.Trim() } else { "Fix CLI exited with code $exitCode" }
            $result.errors = @($errMsg)
            Write-PipelineLog -Level "ERROR" -Step "fix-loop" -Message "Fix CLI failed for '$ObjectName': $errMsg"
            return $result
        }

        # Parse JSON output from fix command
        $fixOutput = $null
        try {
            $fixOutput = $stdout | ConvertFrom-Json
        }
        catch {
            $result.errors = @("Failed to parse fix CLI JSON output: $($_.Exception.Message)")
            Write-PipelineLog -Level "ERROR" -Step "fix-loop" -Message "Failed to parse JSON output for '$ObjectName'"
            return $result
        }

        # Map CLI output to result object
        $result.attempts = if ($fixOutput.attempts) { $fixOutput.attempts } else { 0 }
        $result.explanation = $fixOutput.explanation
        $result.errors = if ($fixOutput.errors) { @($fixOutput.errors) } else { @() }

        if ($fixOutput.success -eq $true) {
            $result.finalStatus = "fixed"
            $result.fixedDdl = $fixOutput.fixedDdl
            Write-PipelineLog -Level "INFO" -Step "fix-loop" -Message "Fixed '$ObjectName' after $($result.attempts) attempt(s)"
        }
        else {
            $result.finalStatus = "unfixable"
            Write-PipelineLog -Level "WARN" -Step "fix-loop" -Message "Could not fix '$ObjectName' after $($result.attempts) attempt(s)"
        }
    }
    catch {
        $result.errors = @($_.Exception.Message)
        Write-PipelineLog -Level "ERROR" -Step "fix-loop" -Message "Exception during fix loop for '$ObjectName': $($_.Exception.Message)"
    }

    return $result
}

# Export module functions when loaded as a module
if ($MyInvocation.MyCommand.ScriptBlock.Module) {
    Export-ModuleMember -Function Invoke-FixLoop
}
