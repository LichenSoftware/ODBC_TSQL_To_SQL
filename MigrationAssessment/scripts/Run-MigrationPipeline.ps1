<#
.SYNOPSIS
    Migration Validation Pipeline Runner.

.DESCRIPTION
    Orchestrates the Extract → Convert → Generate → Validate pipeline for the
    AI-Assisted Schema Conversion tool. Executes steps sequentially via dotnet run,
    tracks elapsed time, halts on failure, and produces a JSON Scoring Report.

.PARAMETER ConnectionString
    SQL Server connection string for single-database mode.

.PARAMETER SessionName
    Session identifier for the pipeline run.

.PARAMETER BatchConfig
    Path to pipeline-config.json for batch mode execution.

.PARAMETER RerunFailures
    Switch to re-convert only failed objects from the most recent Scoring Report.

.PARAMETER ValidationMode
    Validation mode: "live-instance" or "syntax-only". Default: auto-detect.

.PARAMETER PgConnectionString
    PostgreSQL connection string for live-instance validation.

.PARAMETER EndToEnd
    Switch to enable end-to-end validation mode (DDL application, fix loop, data migration, functional tests).

.PARAMETER MaxFixAttempts
    Maximum number of AI-assisted fix attempts per failed DDL object. Default: 2.

.PARAMETER DestPgConnectionString
    Destination PostgreSQL connection string for DDL application in end-to-end mode.

.PARAMETER PgPassthroughPort
    Port for PgPassthrough server during functional testing. Default: 11433.

.EXAMPLE
    .\Run-MigrationPipeline.ps1 -ConnectionString "Server=localhost;Database=TestDB;Trusted_Connection=True;" -SessionName "test-run"

.EXAMPLE
    .\Run-MigrationPipeline.ps1 -BatchConfig "./pipeline-config.json" -PgConnectionString "Host=localhost;Database=validation_scratch;Username=postgres;Password=pass"

.NOTES
    Requirements: 2.1, 2.2, 2.3, 2.4, 2.6
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ConnectionString,

    [Parameter(Mandatory = $false)]
    [string]$SessionName,

    [Parameter(Mandatory = $false)]
    [string]$BatchConfig,

    [switch]$RerunFailures,

    [Parameter(Mandatory = $false)]
    [ValidateSet("live-instance", "syntax-only")]
    [string]$ValidationMode,

    [Parameter(Mandatory = $false)]
    [string]$PgConnectionString,

    [switch]$EndToEnd,

    [Parameter(Mandatory = $false)]
    [int]$MaxFixAttempts = 2,

    [Parameter(Mandatory = $false)]
    [string]$DestPgConnectionString,

    [Parameter(Mandatory = $false)]
    [int]$PgPassthroughPort = 11433
)

# Import library modules
$scriptDir = $PSScriptRoot
$libDir = Join-Path $scriptDir "lib"

. (Join-Path $libDir "Invoke-Scoring.ps1")
. (Join-Path $libDir "Invoke-DiagnosticsClassification.ps1")
. (Join-Path $libDir "Invoke-PgValidation.ps1")
. (Join-Path $libDir "Invoke-ReportGeneration.ps1")
. (Join-Path $libDir "Invoke-DdlApplication.ps1")
. (Join-Path $libDir "Invoke-FixLoop.ps1")
. (Join-Path $libDir "Invoke-DataMigration.ps1")
. (Join-Path $libDir "Invoke-FunctionalTests.ps1")
. (Join-Path $libDir "Invoke-EndToEndScoring.ps1")

# Resolve paths
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..\..\")).Path
$cliProjectPath = Join-Path $repoRoot "AI-AssistedSchemaConversion\src\SchemaConversion.Cli"
$configDir = Join-Path $repoRoot "AI-AssistedSchemaConversion\config"
$sessionsDir = Join-Path $repoRoot "AI-AssistedSchemaConversion\sessions"
$reportsDir = Join-Path $scriptDir "..\pipeline-reports"

# Ensure reports directory exists
if (-not (Test-Path $reportsDir)) {
    New-Item -ItemType Directory -Path $reportsDir -Force | Out-Null
}

function Write-PipelineLog {
    <#
    .SYNOPSIS
        Writes structured pipeline log output.
    #>
    param(
        [string]$Level = "INFO",
        [string]$Step,
        [string]$Message,
        [double]$ElapsedSeconds
    )

    $timestamp = Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ"
    $logEntry = @{
        timestamp      = $timestamp
        level          = $Level
        step           = $Step
        message        = $Message
        elapsedSeconds = $ElapsedSeconds
    }

    $logJson = $logEntry | ConvertTo-Json -Compress
    if ($Level -eq "ERROR") {
        Write-Error $logJson
    }
    else {
        Write-Host $logJson
    }
}

function Invoke-PipelineStep {
    <#
    .SYNOPSIS
        Executes a single pipeline step via dotnet run and tracks elapsed time.
    .OUTPUTS
        Hashtable with exitCode, elapsedSeconds, errorMessage, output
    #>
    param(
        [Parameter(Mandatory)]
        [string]$StepName,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $fullArgs = @("run", "--project", $cliProjectPath, "--") + $Arguments
        $processInfo = New-Object System.Diagnostics.ProcessStartInfo
        $processInfo.FileName = "dotnet"
        $processInfo.Arguments = ($fullArgs | ForEach-Object {
            if ($_ -match '\s') { "`"$_`"" } else { $_ }
        }) -join ' '
        $processInfo.WorkingDirectory = (Join-Path $repoRoot "AI-AssistedSchemaConversion")
        $processInfo.RedirectStandardOutput = $true
        $processInfo.RedirectStandardError = $true
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $true

        $process = [System.Diagnostics.Process]::Start($processInfo)

        # Read stdout and stderr asynchronously to avoid deadlock
        # when the subprocess fills one pipe buffer while we block on the other.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        $stopwatch.Stop()
        $elapsed = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)

        $exitCode = $process.ExitCode
        $errorMessage = $null

        if ($exitCode -ne 0) {
            $errorMessage = if ($stderr) { $stderr.Trim() } else { "Step exited with code $exitCode" }
        }

        return @{
            exitCode       = $exitCode
            elapsedSeconds = $elapsed
            errorMessage   = $errorMessage
            output         = $stdout
        }
    }
    catch {
        $stopwatch.Stop()
        $elapsed = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)

        return @{
            exitCode       = 1
            elapsedSeconds = $elapsed
            errorMessage   = $_.Exception.Message
            output         = $null
        }
    }
}

function Get-ConfigFileHashes {
    <#
    .SYNOPSIS
        Computes SHA-256 hashes for config files (type-mappings, function-mappings,
        schema-mappings, and prompt templates).
    .OUTPUTS
        Hashtable of filename → SHA-256 hash string.
    #>

    $hashes = @{}

    # Mapping config files
    $mappingFiles = @(
        "type-mappings.json",
        "function-mappings.json",
        "schema-mappings.json"
    )

    foreach ($fileName in $mappingFiles) {
        $filePath = Join-Path $configDir $fileName
        if (Test-Path $filePath) {
            $hash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash
            $hashes[$fileName] = $hash
        }
        else {
            $hashes[$fileName] = $null
        }
    }

    # Prompt template files
    $promptsDir = Join-Path $configDir "prompts"
    if (Test-Path $promptsDir) {
        $promptFiles = Get-ChildItem -Path $promptsDir -Filter "*.md" -File
        foreach ($promptFile in $promptFiles) {
            $hash = (Get-FileHash -Path $promptFile.FullName -Algorithm SHA256).Hash
            $hashes[$promptFile.Name] = $hash
        }
    }

    return $hashes
}

function Get-SessionOutputDir {
    <#
    .SYNOPSIS
        Gets the session output directory path for a given session name.
    #>
    param([string]$Session)

    return Join-Path (Join-Path $sessionsDir $Session) "output"
}

function Get-SessionObjectsDir {
    <#
    .SYNOPSIS
        Gets the session objects directory path for a given session name.
    #>
    param([string]$Session)

    return Join-Path (Join-Path $sessionsDir $Session) "objects"
}

function Read-ValidationResults {
    <#
    .SYNOPSIS
        Reads generated DDL from the session output and prepares objects for validation.
    .OUTPUTS
        Array of objects with objectName, objectType, ddl, dependencies properties.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Session
    )

    $outputDir = Get-SessionOutputDir -Session $Session
    $objectsDir = Get-SessionObjectsDir -Session $Session

    $ddlStatements = @()

    # Read from consolidated output if available
    $consolidatedPath = Join-Path $outputDir "consolidated.sql"
    if (Test-Path $consolidatedPath) {
        # If consolidated output exists, we still need individual objects for per-object validation
    }

    # Read individual object files from the session objects directory
    if (Test-Path $objectsDir) {
        $objectFiles = Get-ChildItem -Path $objectsDir -Filter "*.json" -File
        foreach ($objFile in $objectFiles) {
            try {
                $objData = Get-Content -Path $objFile.FullName -Raw | ConvertFrom-Json

                $objectName = $null
                $objectType = $null
                $generatedDdl = $null
                $dependencies = @()

                # The JSON structure has source and result sections
                if ($objData.PSObject.Properties['result'] -and $objData.result) {
                    $objectName = $objData.result.objectName
                    $objectType = $objData.result.objectType
                    if ($objData.result.PSObject.Properties['generatedDdl']) {
                        $generatedDdl = $objData.result.generatedDdl
                    }
                }

                # Fall back to source section for name/type if result is missing
                if (-not $objectName -and $objData.PSObject.Properties['source'] -and $objData.source) {
                    $objectName = $objData.source.name
                    $objectType = $objData.source.objectType
                }

                # Legacy flat structure fallback
                if (-not $objectName) {
                    $objectName = $objData.objectName
                    $objectType = $objData.objectType
                }

                # Look for generated DDL in legacy flat properties
                if (-not $generatedDdl) {
                    if ($objData.PSObject.Properties['convertedDdl']) {
                        $generatedDdl = $objData.convertedDdl
                    }
                    elseif ($objData.PSObject.Properties['generatedDdl']) {
                        $generatedDdl = $objData.generatedDdl
                    }
                    elseif ($objData.PSObject.Properties['postgresDdl']) {
                        $generatedDdl = $objData.postgresDdl
                    }
                }

                # Look for dependencies
                if ($objData.PSObject.Properties['source'] -and $objData.source.PSObject.Properties['dependsOn']) {
                    $dependencies = @($objData.source.dependsOn)
                }
                elseif ($objData.PSObject.Properties['dependencies']) {
                    $dependencies = @($objData.dependencies)
                }

                if ($objectName) {
                    $ddlStatements += [PSCustomObject]@{
                        objectName   = $objectName
                        objectType   = $objectType
                        ddl          = $generatedDdl
                        dependencies = $dependencies
                    }
                }
            }
            catch {
                Write-Warning "Failed to read object file $($objFile.Name): $($_.Exception.Message)"
            }
        }
    }

    return $ddlStatements
}

function Invoke-SingleDatabasePipeline {
    <#
    .SYNOPSIS
        Executes the full pipeline for a single database.
    .OUTPUTS
        Hashtable with success status, step results, object results, elapsed seconds.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ConnString,

        [Parameter(Mandatory)]
        [string]$Session,

        [string]$DbName,

        [string]$ValMode,

        [string]$PgConnString,

        [string[]]$ChangedConfigTypes = @(),

        [bool]$EndToEndEnabled = $false,

        [string]$DestPgConnString,

        [string]$E2eMaintenanceConnString,

        [string]$E2eDatabaseName,

        [int]$E2eMaxFixAttempts = 2,

        [string]$E2eTestScriptDir,

        [string]$E2ePgPassthroughPath,

        [int]$E2ePgPassthroughPort = 11433,

        [int]$E2eTimeoutPerScript = 30
    )

    $pipelineStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $stepResults = @()

    # Step 1: Extract
    Write-Host "[$Session] Step 1/4: Extract..."
    $extractResult = Invoke-PipelineStep -StepName "extract" -Arguments @(
        "extract",
        "--connection", $ConnString,
        "--output", (Join-Path $sessionsDir $Session)
    )

    $stepResults += @{
        step           = "extract"
        exitCode       = $extractResult.exitCode
        elapsedSeconds = $extractResult.elapsedSeconds
        errorMessage   = $extractResult.errorMessage
    }

    if ($extractResult.exitCode -ne 0) {
        Write-PipelineLog -Level "ERROR" -Step "extract" -Message $extractResult.errorMessage -ElapsedSeconds $extractResult.elapsedSeconds
        $pipelineStopwatch.Stop()
        return @{
            success        = $false
            failedStep     = "extract"
            stepResults    = $stepResults
            objectResults  = @()
            elapsedSeconds = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)
        }
    }

    Write-PipelineLog -Level "INFO" -Step "extract" -Message "Extract completed successfully" -ElapsedSeconds $extractResult.elapsedSeconds

    # Step 2: Convert
    Write-Host "[$Session] Step 2/4: Convert..."
    $convertResult = Invoke-PipelineStep -StepName "convert" -Arguments @(
        "convert",
        "--session", $Session
    )

    $stepResults += @{
        step           = "convert"
        exitCode       = $convertResult.exitCode
        elapsedSeconds = $convertResult.elapsedSeconds
        errorMessage   = $convertResult.errorMessage
    }

    if ($convertResult.exitCode -ne 0) {
        Write-PipelineLog -Level "ERROR" -Step "convert" -Message $convertResult.errorMessage -ElapsedSeconds $convertResult.elapsedSeconds
        $pipelineStopwatch.Stop()
        return @{
            success        = $false
            failedStep     = "convert"
            stepResults    = $stepResults
            objectResults  = @()
            elapsedSeconds = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)
        }
    }

    Write-PipelineLog -Level "INFO" -Step "convert" -Message "Convert completed successfully" -ElapsedSeconds $convertResult.elapsedSeconds

    # Step 2b: If config/prompt files changed, selectively re-convert affected object types
    if ($ChangedConfigTypes -and $ChangedConfigTypes.Count -gt 0) {
        Write-Host "[$Session] Step 2b: Re-converting objects for changed config types: $($ChangedConfigTypes -join ', ')..."

        # Read existing session objects to determine which ones need re-conversion by type
        $sessionObjectsDir = Get-SessionObjectsDir -Session $Session
        $objectsToReconvert = @()

        if (Test-Path $sessionObjectsDir) {
            $objectFiles = Get-ChildItem -Path $sessionObjectsDir -Filter "*.json" -File
            foreach ($objFile in $objectFiles) {
                try {
                    $objData = Get-Content -Path $objFile.FullName -Raw | ConvertFrom-Json
                    if ($objData.objectType -and ($ChangedConfigTypes -contains $objData.objectType)) {
                        $objectsToReconvert += $objData.objectName
                    }
                }
                catch {
                    Write-Warning "Failed to read object file $($objFile.Name) for change detection: $($_.Exception.Message)"
                }
            }
        }

        if ($objectsToReconvert.Count -gt 0) {
            Write-Host "[$Session] Re-converting $($objectsToReconvert.Count) objects due to config changes..."

            $reconvertArgs = @("convert", "--session", $Session, "--objects") + $objectsToReconvert
            $reconvertResult = Invoke-PipelineStep -StepName "convert (config-change)" -Arguments $reconvertArgs

            $stepResults += @{
                step           = "convert (config-change)"
                exitCode       = $reconvertResult.exitCode
                elapsedSeconds = $reconvertResult.elapsedSeconds
                errorMessage   = $reconvertResult.errorMessage
            }

            if ($reconvertResult.exitCode -ne 0) {
                Write-PipelineLog -Level "WARN" -Step "convert (config-change)" -Message "Re-conversion for config changes failed: $($reconvertResult.errorMessage)" -ElapsedSeconds $reconvertResult.elapsedSeconds
                # Non-fatal: continue with existing conversions
            }
            else {
                Write-PipelineLog -Level "INFO" -Step "convert (config-change)" -Message "Re-converted $($objectsToReconvert.Count) objects due to config changes" -ElapsedSeconds $reconvertResult.elapsedSeconds
            }
        }
        else {
            Write-Host "[$Session] No objects matched changed config types. Skipping re-conversion."
        }
    }

    # Step 3: Generate
    Write-Host "[$Session] Step 3/4: Generate..."
    $generateOutputDir = Join-Path (Join-Path $sessionsDir $Session) "output"
    if (-not (Test-Path $generateOutputDir)) {
        New-Item -ItemType Directory -Path $generateOutputDir -Force | Out-Null
    }
    $generateResult = Invoke-PipelineStep -StepName "generate" -Arguments @(
        "generate",
        "--session", $Session,
        "--output", $generateOutputDir,
        "--mode", "consolidated"
    )

    $stepResults += @{
        step           = "generate"
        exitCode       = $generateResult.exitCode
        elapsedSeconds = $generateResult.elapsedSeconds
        errorMessage   = $generateResult.errorMessage
    }

    if ($generateResult.exitCode -ne 0) {
        Write-PipelineLog -Level "ERROR" -Step "generate" -Message $generateResult.errorMessage -ElapsedSeconds $generateResult.elapsedSeconds
        $pipelineStopwatch.Stop()
        return @{
            success        = $false
            failedStep     = "generate"
            stepResults    = $stepResults
            objectResults  = @()
            elapsedSeconds = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)
        }
    }

    Write-PipelineLog -Level "INFO" -Step "generate" -Message "Generate completed successfully" -ElapsedSeconds $generateResult.elapsedSeconds

    # Step 4: Validate
    Write-Host "[$Session] Step 4/4: Validate..."
    $validateStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $ddlStatements = Read-ValidationResults -Session $Session

        $validationParams = @{
            DdlStatements = $ddlStatements
        }

        # Determine validation mode
        if ($ValMode -eq "live-instance" -and $PgConnString) {
            $validationParams.PgConnectionString = $PgConnString
        }
        elseif ($ValMode -eq "syntax-only") {
            # No PG connection string - will use syntax-only mode
        }
        elseif ($PgConnString) {
            # Auto-detect: try live instance
            $validationParams.PgConnectionString = $PgConnString
        }

        $validationResults = Invoke-PgValidation @validationParams

        $validateStopwatch.Stop()
        $validateElapsed = [Math]::Round($validateStopwatch.Elapsed.TotalSeconds, 2)

        $stepResults += @{
            step           = "validate"
            exitCode       = 0
            elapsedSeconds = $validateElapsed
            errorMessage   = $null
        }

        Write-PipelineLog -Level "INFO" -Step "validate" -Message "Validation completed successfully" -ElapsedSeconds $validateElapsed

        # Build object results combining DDL info with validation results
        $objectResults = @()
        foreach ($valResult in $validationResults) {
            $ddlObj = $ddlStatements | Where-Object { $_.objectName -eq $valResult.objectName }
            $objectResults += [PSCustomObject]@{
                objectName      = $valResult.objectName
                objectType      = if ($ddlObj) { $ddlObj.objectType } else { "Unknown" }
                databaseName    = if ($DbName) { $DbName } else { $Session }
                status          = $valResult.status
                errorMessage    = $valResult.errorMessage
                errorLineNumber = $valResult.errorLineNumber
                generatedDdl    = if ($ddlObj) { $ddlObj.ddl } else { $null }
                validationMode  = $valResult.validationMode
            }
        }

        # Also include objects that didn't get validation results (fail-convert)
        foreach ($ddlObj in $ddlStatements) {
            $hasResult = $objectResults | Where-Object { $_.objectName -eq $ddlObj.objectName }
            if (-not $hasResult) {
                $status = if ([string]::IsNullOrWhiteSpace($ddlObj.ddl)) { "fail-convert" } else { "skip" }
                $objectResults += [PSCustomObject]@{
                    objectName      = $ddlObj.objectName
                    objectType      = $ddlObj.objectType
                    databaseName    = if ($DbName) { $DbName } else { $Session }
                    status          = $status
                    errorMessage    = if ($status -eq "fail-convert") { "Conversion produced no DDL output" } else { $null }
                    errorLineNumber = $null
                    generatedDdl    = $ddlObj.ddl
                    validationMode  = $null
                }
            }
        }
    }
    catch {
        $validateStopwatch.Stop()
        $validateElapsed = [Math]::Round($validateStopwatch.Elapsed.TotalSeconds, 2)

        Write-PipelineLog -Level "ERROR" -Step "validate" -Message $_.Exception.Message -ElapsedSeconds $validateElapsed
        $stepResults += @{
            step           = "validate"
            exitCode       = 1
            elapsedSeconds = $validateElapsed
            errorMessage   = $_.Exception.Message
        }

        $pipelineStopwatch.Stop()
        return @{
            success        = $false
            failedStep     = "validate"
            stepResults    = $stepResults
            objectResults  = @()
            elapsedSeconds = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)
        }
    }

    # -----------------------------------------------------------------------
    # End-to-End Steps (Steps 5-7): Apply → Fix Loop → Data Migration → Functional Tests → E2E Scoring
    # Gated by -EndToEnd switch or endToEnd config presence
    # -----------------------------------------------------------------------
    $endToEndResults = $null

    # Determine if E2E is enabled (from param or config passed through)
    $e2eEnabled = $false
    if ($EndToEndEnabled) {
        $e2eEnabled = $true
    }

    if ($e2eEnabled -and $DestPgConnString) {
        Write-Host "[$Session] End-to-End validation enabled. Starting E2E steps..."

        $e2eStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $ddlResults = @()
        $fixResults = @()
        $dataMigrationResults = $null
        $functionalTestResults = $null

        # Step 5: DDL Application
        Write-Host "[$Session] Step 5: Apply DDL to destination database..."
        $applyStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

        try {
            $ddlResults = Invoke-DdlApplication `
                -DdlStatements $ddlStatements `
                -PgConnectionString $DestPgConnString `
                -MaintenanceConnectionString $E2eMaintenanceConnString `
                -DatabaseName $E2eDatabaseName

            $applyStopwatch.Stop()
            $applyElapsed = [Math]::Round($applyStopwatch.Elapsed.TotalSeconds, 2)

            $appliedCount = @($ddlResults | Where-Object { $_.status -eq "applied" }).Count
            $failedCount = @($ddlResults | Where-Object { $_.status -eq "failed" }).Count

            $stepResults += @{
                step           = "apply"
                exitCode       = 0
                elapsedSeconds = $applyElapsed
                errorMessage   = $null
            }

            Write-PipelineLog -Level "INFO" -Step "apply" -Message "DDL application completed: $appliedCount applied, $failedCount failed" -ElapsedSeconds $applyElapsed
        }
        catch {
            $applyStopwatch.Stop()
            $applyElapsed = [Math]::Round($applyStopwatch.Elapsed.TotalSeconds, 2)

            $stepResults += @{
                step           = "apply"
                exitCode       = 1
                elapsedSeconds = $applyElapsed
                errorMessage   = $_.Exception.Message
            }

            Write-PipelineLog -Level "ERROR" -Step "apply" -Message "DDL application failed: $($_.Exception.Message)" -ElapsedSeconds $applyElapsed
        }

        # Step 5b: Fix Loop for failed objects
        $failedDdlObjects = @($ddlResults | Where-Object { $_.status -eq "failed" })
        if ($failedDdlObjects.Count -gt 0) {
            Write-Host "[$Session] Step 5b: Fix Loop for $($failedDdlObjects.Count) failed object(s)..."
            $fixStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

            try {
                # Build failed objects array with source T-SQL info
                $failedForFix = @($failedDdlObjects | ForEach-Object {
                    $objName = $_.objectName
                    $ddlObj = $ddlStatements | Where-Object { $_.objectName -eq $objName }
                    @{
                        objectName   = $objName
                        ddl          = if ($ddlObj) { $ddlObj.ddl } else { $null }
                        errorMessage = $_.errorMessage
                        sourceTSql   = $null  # Source T-SQL not readily available in pipeline context
                    }
                })

                $fixResults = Invoke-FixLoop `
                    -FailedObjects $failedForFix `
                    -PgConnectionString $DestPgConnString `
                    -MaxAttempts $E2eMaxFixAttempts `
                    -CliProjectPath $cliProjectPath

                $fixStopwatch.Stop()
                $fixElapsed = [Math]::Round($fixStopwatch.Elapsed.TotalSeconds, 2)

                $fixedCount = @($fixResults | Where-Object { $_.finalStatus -eq "fixed" }).Count
                $unfixableCount = @($fixResults | Where-Object { $_.finalStatus -eq "unfixable" }).Count

                $stepResults += @{
                    step           = "fix-loop"
                    exitCode       = 0
                    elapsedSeconds = $fixElapsed
                    errorMessage   = $null
                }

                Write-PipelineLog -Level "INFO" -Step "fix-loop" -Message "Fix loop completed: $fixedCount fixed, $unfixableCount unfixable" -ElapsedSeconds $fixElapsed
            }
            catch {
                $fixStopwatch.Stop()
                $fixElapsed = [Math]::Round($fixStopwatch.Elapsed.TotalSeconds, 2)

                $stepResults += @{
                    step           = "fix-loop"
                    exitCode       = 1
                    elapsedSeconds = $fixElapsed
                    errorMessage   = $_.Exception.Message
                }

                Write-PipelineLog -Level "ERROR" -Step "fix-loop" -Message "Fix loop failed: $($_.Exception.Message)" -ElapsedSeconds $fixElapsed
            }
        }
        else {
            $stepResults += @{
                step           = "fix-loop"
                exitCode       = 0
                elapsedSeconds = 0
                errorMessage   = $null
            }
            Write-PipelineLog -Level "INFO" -Step "fix-loop" -Message "No failed objects - fix loop skipped" -ElapsedSeconds 0
        }

        # Step 6: Data Migration - only if at least one table was applied or fixed
        $appliedObjects = @($ddlResults | Where-Object { $_.status -eq "applied" })
        $fixedObjects = @($fixResults | Where-Object { $_.finalStatus -eq "fixed" })
        $appliedTables = @($ddlStatements | Where-Object {
            $_.objectType -eq "Table" -and (
                ($appliedObjects | Where-Object { $_.objectName -eq $_.objectName }) -or
                ($fixedObjects | Where-Object { $_.objectName -eq $_.objectName })
            )
        })

        $hasAppliedTables = ($appliedObjects.Count + $fixedObjects.Count) -gt 0

        if ($hasAppliedTables) {
            Write-Host "[$Session] Step 6: Data Migration..."
            $dataMigStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

            try {
                $sessionPath = Join-Path $sessionsDir $Session
                $dataMigratorPath = Join-Path $repoRoot "DataMigrator"

                $dataMigrationResults = Invoke-DataMigration `
                    -SourceConnectionString $ConnString `
                    -TargetConnectionString $DestPgConnString `
                    -SessionPath $sessionPath `
                    -DataMigratorProjectPath $dataMigratorPath

                $dataMigStopwatch.Stop()
                $dataMigElapsed = [Math]::Round($dataMigStopwatch.Elapsed.TotalSeconds, 2)

                $stepResults += @{
                    step           = "data-migration"
                    exitCode       = 0
                    elapsedSeconds = $dataMigElapsed
                    errorMessage   = $null
                }

                Write-PipelineLog -Level "INFO" -Step "data-migration" -Message "Data migration completed: $($dataMigrationResults.tablesSucceeded) tables, $($dataMigrationResults.totalRows) rows" -ElapsedSeconds $dataMigElapsed
            }
            catch {
                $dataMigStopwatch.Stop()
                $dataMigElapsed = [Math]::Round($dataMigStopwatch.Elapsed.TotalSeconds, 2)

                $stepResults += @{
                    step           = "data-migration"
                    exitCode       = 1
                    elapsedSeconds = $dataMigElapsed
                    errorMessage   = $_.Exception.Message
                }

                Write-PipelineLog -Level "ERROR" -Step "data-migration" -Message "Data migration failed: $($_.Exception.Message)" -ElapsedSeconds $dataMigElapsed
            }
        }
        else {
            $stepResults += @{
                step           = "data-migration"
                exitCode       = 0
                elapsedSeconds = 0
                errorMessage   = $null
            }
            Write-PipelineLog -Level "INFO" -Step "data-migration" -Message "No tables applied - data migration skipped" -ElapsedSeconds 0
        }

        # Step 7: Functional Tests - only if data migration succeeded and test scripts exist
        $dataMigSucceeded = ($dataMigrationResults -and $dataMigrationResults.tablesSucceeded -gt 0)

        if ($dataMigSucceeded -and $E2eTestScriptDir -and (Test-Path $E2eTestScriptDir)) {
            Write-Host "[$Session] Step 7: Functional Tests..."
            $funcTestStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

            try {
                $functionalTestResults = Invoke-FunctionalTests `
                    -TestScriptDirectory $E2eTestScriptDir `
                    -PgPassthroughProjectPath $E2ePgPassthroughPath `
                    -PgPassthroughPort $E2ePgPassthroughPort `
                    -DestPgConnectionString $DestPgConnString `
                    -TimeoutPerScript $E2eTimeoutPerScript

                $funcTestStopwatch.Stop()
                $funcTestElapsed = [Math]::Round($funcTestStopwatch.Elapsed.TotalSeconds, 2)

                $stepResults += @{
                    step           = "functional-tests"
                    exitCode       = 0
                    elapsedSeconds = $funcTestElapsed
                    errorMessage   = $null
                }

                Write-PipelineLog -Level "INFO" -Step "functional-tests" -Message "Functional tests completed: $($functionalTestResults.passed) passed, $($functionalTestResults.failed) failed" -ElapsedSeconds $funcTestElapsed
            }
            catch {
                $funcTestStopwatch.Stop()
                $funcTestElapsed = [Math]::Round($funcTestStopwatch.Elapsed.TotalSeconds, 2)

                $stepResults += @{
                    step           = "functional-tests"
                    exitCode       = 1
                    elapsedSeconds = $funcTestElapsed
                    errorMessage   = $_.Exception.Message
                }

                Write-PipelineLog -Level "ERROR" -Step "functional-tests" -Message "Functional tests failed: $($_.Exception.Message)" -ElapsedSeconds $funcTestElapsed
            }
        }
        else {
            $stepResults += @{
                step           = "functional-tests"
                exitCode       = 0
                elapsedSeconds = 0
                errorMessage   = $null
            }
            $skipReason = if (-not $dataMigSucceeded) { "data migration did not succeed" } else { "no test scripts found" }
            Write-PipelineLog -Level "INFO" -Step "functional-tests" -Message "Functional tests skipped: $skipReason" -ElapsedSeconds 0
        }

        # Compute End-to-End Score
        $e2eStopwatch.Stop()
        $e2eTotalElapsed = [Math]::Round($e2eStopwatch.Elapsed.TotalSeconds, 2)

        try {
            $endToEndResults = Invoke-EndToEndScoring `
                -DdlResults $ddlResults `
                -FixResults $fixResults `
                -DataMigrationResults $dataMigrationResults `
                -FunctionalTestResults $functionalTestResults

            Write-Host "[$Session] End-to-End Score: $($endToEndResults.endToEndScore)%"
        }
        catch {
            Write-PipelineLog -Level "WARN" -Step "e2e-scoring" -Message "Failed to compute E2E score: $($_.Exception.Message)" -ElapsedSeconds 0
        }

        Write-PipelineLog -Level "INFO" -Step "end-to-end" -Message "All end-to-end steps completed" -ElapsedSeconds $e2eTotalElapsed
    }
    elseif ($e2eEnabled -and -not $DestPgConnString) {
        Write-PipelineLog -Level "WARN" -Step "end-to-end" -Message "End-to-end mode enabled but no destination connection string provided. Skipping E2E steps." -ElapsedSeconds 0
    }

    $pipelineStopwatch.Stop()
    $totalElapsed = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)

    return @{
        success           = $true
        failedStep        = $null
        stepResults       = $stepResults
        objectResults     = $objectResults
        endToEndResults   = $endToEndResults
        elapsedSeconds    = $totalElapsed
    }
}

function Build-ScoringReport {
    <#
    .SYNOPSIS
        Produces the final Scoring Report JSON from pipeline results.
    #>
    param(
        [Parameter(Mandatory)]
        [array]$ObjectResults,

        [Parameter(Mandatory)]
        [double]$TotalElapsedSeconds,

        [hashtable]$PreviousScores = @{},

        [string]$ValMode
    )

    # Compute scores
    $scoringResult = Invoke-Scoring -ObjectResults $ObjectResults -PreviousScores $PreviousScores

    # Classify failures
    $failedObjects = @($ObjectResults | Where-Object {
        $_.status -eq "fail-syntax" -or $_.status -eq "fail-convert"
    })
    $diagnosticsResult = Invoke-DiagnosticsClassification -FailedObjects $failedObjects

    # Compute config file hashes
    $configHashes = Get-ConfigFileHashes

    # Determine validation mode from results
    $detectedMode = $ValMode
    if (-not $detectedMode) {
        $modeFromResults = $ObjectResults | Where-Object { $_.validationMode } | Select-Object -First 1
        if ($modeFromResults) {
            $detectedMode = $modeFromResults.validationMode
        }
        else {
            $detectedMode = "syntax-only"
        }
    }

    # Build per-database results for the report
    $databaseResults = @()
    $dbGroups = $ObjectResults | Group-Object -Property databaseName
    foreach ($dbGroup in $dbGroups) {
        $dbName = $dbGroup.Name
        $dbObjects = $dbGroup.Group
        $dbScore = $scoringResult.databases[$dbName]

        $dbEntry = @{
            name          = $dbName
            sessionName   = $dbName
            objectCount   = $dbObjects.Count
            score         = @{
                compatibilityScore = $dbScore.compatibilityScore
                previousScore      = $dbScore.previousScore
                delta              = $dbScore.delta
                pass               = $dbScore.pass
                failSyntax         = $dbScore.failSyntax
                failConvert        = $dbScore.failConvert
                skip               = $dbScore.skip
            }
            byType        = $dbScore.byType
            objects       = @($dbObjects | ForEach-Object {
                @{
                    name           = $_.objectName
                    type           = $_.objectType
                    status         = $_.status
                    errorMessage   = $_.errorMessage
                    errorLineNumber = $_.errorLineNumber
                    generatedDdl   = $_.generatedDdl
                }
            })
        }
        $databaseResults += $dbEntry
    }

    # Build top failing types for diagnostics
    $topFailingTypes = @($scoringResult.topFailingTypes | ForEach-Object {
        @{
            type      = $_.type
            failCount = $_.failCount
        }
    })

    # Build the full report
    $report = @{
        reportId            = [guid]::NewGuid().ToString()
        timestamp           = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        totalElapsedSeconds = $TotalElapsedSeconds
        validationMode      = $detectedMode
        configHashes        = $configHashes
        databases           = $databaseResults
        aggregate           = $scoringResult.aggregate
        diagnostics         = @{
            rootCauseCategories = @($diagnosticsResult | ForEach-Object {
                @{
                    category = $_.category
                    count    = $_.count
                    objects  = $_.objects
                }
            })
            topFailingTypes     = $topFailingTypes
        }
    }

    return $report
}

function Save-ScoringReport {
    <#
    .SYNOPSIS
        Saves a Scoring Report to the pipeline-reports directory as JSON.
    #>
    param(
        [Parameter(Mandatory)]
        [hashtable]$Report,

        [string]$OutputDir
    )

    $targetDir = if ($OutputDir) { $OutputDir } else { $reportsDir }

    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    }

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $reportFileName = "scoring-report-$timestamp.json"
    $reportPath = Join-Path $targetDir $reportFileName

    $Report | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath -Encoding UTF8

    Write-Host "Scoring Report saved: $reportPath"
    return $reportPath
}

function Get-PreviousScores {
    <#
    .SYNOPSIS
        Loads previous scoring report scores for delta computation.
    #>
    param([string]$OutputDir)

    $targetDir = if ($OutputDir) { $OutputDir } else { $reportsDir }

    if (-not (Test-Path $targetDir)) {
        return @{}
    }

    $previousReports = Get-ChildItem -Path $targetDir -Filter "scoring-report-*.json" -File |
        Sort-Object -Property LastWriteTime -Descending

    if ($previousReports.Count -eq 0) {
        return @{}
    }

    try {
        $latestReport = Get-Content -Path $previousReports[0].FullName -Raw | ConvertFrom-Json
        $scores = @{}

        if ($latestReport.databases) {
            foreach ($db in $latestReport.databases) {
                if ($db.score -and $db.score.compatibilityScore) {
                    $scores[$db.name] = $db.score.compatibilityScore
                }
            }
        }

        if ($latestReport.aggregate -and $latestReport.aggregate.compatibilityScore) {
            $scores['__aggregate__'] = $latestReport.aggregate.compatibilityScore
        }

        return $scores
    }
    catch {
        Write-Warning "Failed to read previous scoring report: $($_.Exception.Message)"
        return @{}
    }
}

function Get-MostRecentReport {
    <#
    .SYNOPSIS
        Loads the most recent Scoring Report JSON from the pipeline-reports directory.
    .DESCRIPTION
        Finds and parses the most recent scoring report file, optionally filtering
        by session name if the report contains database entries matching the session.
    .OUTPUTS
        The parsed report object, or $null if no report is found.
    #>
    param(
        [string]$OutputDir,
        [string]$Session
    )

    $targetDir = if ($OutputDir) { $OutputDir } else { $reportsDir }

    if (-not (Test-Path $targetDir)) {
        return $null
    }

    $reportFiles = Get-ChildItem -Path $targetDir -Filter "scoring-report-*.json" -File |
        Sort-Object -Property LastWriteTime -Descending

    if ($reportFiles.Count -eq 0) {
        return $null
    }

    # If session is specified, find the most recent report containing that session
    foreach ($reportFile in $reportFiles) {
        try {
            $report = Get-Content -Path $reportFile.FullName -Raw | ConvertFrom-Json

            if ($Session) {
                # Check if this report contains data for the specified session
                $hasSession = $report.databases | Where-Object {
                    $_.sessionName -eq $Session -or $_.name -eq $Session
                }
                if ($hasSession) {
                    return $report
                }
            }
            else {
                # Return the most recent report regardless
                return $report
            }
        }
        catch {
            Write-Warning "Failed to parse report $($reportFile.Name): $($_.Exception.Message)"
            continue
        }
    }

    # If no session-specific report was found, return the most recent one
    if ($Session) {
        try {
            return Get-Content -Path $reportFiles[0].FullName -Raw | ConvertFrom-Json
        }
        catch {
            return $null
        }
    }

    return $null
}

function Get-FailedObjectsFromReport {
    <#
    .SYNOPSIS
        Extracts objects with status "fail-syntax" or "fail-convert" from a Scoring Report.
    .OUTPUTS
        Array of object detail hashtables from the report that have a failed status.
    #>
    param(
        [Parameter(Mandatory)]
        [PSObject]$Report,

        [string]$Session
    )

    $failedObjects = @()

    if (-not $Report.databases) {
        return $failedObjects
    }

    foreach ($db in $Report.databases) {
        # If session specified, only look at matching database entries
        if ($Session -and ($db.sessionName -ne $Session -and $db.name -ne $Session)) {
            continue
        }

        if ($db.objects) {
            foreach ($obj in $db.objects) {
                if ($obj.status -eq "fail-syntax" -or $obj.status -eq "fail-convert") {
                    $failedObjects += [PSCustomObject]@{
                        objectName   = $obj.name
                        objectType   = $obj.type
                        databaseName = $db.name
                        status       = $obj.status
                        errorMessage = $obj.errorMessage
                    }
                }
            }
        }
    }

    return $failedObjects
}

function Get-PreservedObjectsFromReport {
    <#
    .SYNOPSIS
        Extracts objects with status "pass" or "skip" from a Scoring Report (to preserve them).
    .OUTPUTS
        Array of PSCustomObjects matching the objectResults format used elsewhere in the pipeline.
    #>
    param(
        [Parameter(Mandatory)]
        [PSObject]$Report,

        [string]$Session
    )

    $preservedObjects = @()

    if (-not $Report.databases) {
        return $preservedObjects
    }

    foreach ($db in $Report.databases) {
        # If session specified, only look at matching database entries
        if ($Session -and ($db.sessionName -ne $Session -and $db.name -ne $Session)) {
            continue
        }

        if ($db.objects) {
            foreach ($obj in $db.objects) {
                if ($obj.status -eq "pass" -or $obj.status -eq "skip") {
                    $preservedObjects += [PSCustomObject]@{
                        objectName      = $obj.name
                        objectType      = $obj.type
                        databaseName    = $db.name
                        status          = $obj.status
                        errorMessage    = $obj.errorMessage
                        errorLineNumber = $obj.errorLineNumber
                        generatedDdl    = $obj.generatedDdl
                        validationMode  = $null
                    }
                }
            }
        }
    }

    return $preservedObjects
}

function Invoke-RerunFailures {
    <#
    .SYNOPSIS
        Re-runs convert → generate → validate for failed objects from the most recent
        Scoring Report, preserving pass/skip results and returning merged object results.
    .DESCRIPTION
        Implements Requirement 4.3 (rerun-failures mode):
        1. Reads the most recent Scoring Report for the specified session from ./pipeline-reports/
        2. Identifies objects with status "fail-syntax" or "fail-convert"
        3. Re-runs only convert → generate → validate for those specific objects
        4. Preserves all "pass" and "skip" results from the previous report unchanged
        5. Merges the new results with the preserved results
        6. Returns the merged object results array
    .PARAMETER Session
        The session name used to locate the most recent Scoring Report and re-run the CLI steps.
    .PARAMETER DbName
        Optional display name for the database (defaults to Session if omitted).
    .PARAMETER ValMode
        Validation mode override: "live-instance" or "syntax-only".
    .PARAMETER PgConnString
        Optional PostgreSQL connection string for live-instance validation.
    .OUTPUTS
        Array of PSCustomObjects (merged pass/skip preserved + newly re-validated results).
        Returns $null if no previous Scoring Report is found for the session.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Session,

        [string]$DbName,

        [string]$ValMode,

        [string]$PgConnString
    )

    $effectiveDbName = if ($DbName) { $DbName } else { $Session }

    # 1. Read the most recent Scoring Report for this session
    Write-Host "[$Session] Invoke-RerunFailures: Loading most recent Scoring Report..."
    $previousReport = Get-MostRecentReport -Session $Session

    if (-not $previousReport) {
        Write-PipelineLog -Level "ERROR" -Step "rerun-load" `
            -Message "No previous Scoring Report found for session '$Session'. Cannot determine which objects to re-run." `
            -ElapsedSeconds 0
        return $null
    }

    # 2. Identify objects with status "fail-syntax" or "fail-convert"
    $failedObjects   = Get-FailedObjectsFromReport   -Report $previousReport -Session $Session
    $preservedObjects = Get-PreservedObjectsFromReport -Report $previousReport -Session $Session

    if ($failedObjects.Count -eq 0) {
        Write-Host "[$Session] Invoke-RerunFailures: No failed objects found in the previous report. Returning preserved results."
        return $preservedObjects
    }

    $failedNames = @($failedObjects | ForEach-Object { $_.objectName })
    Write-Host "[$Session] Invoke-RerunFailures: $($failedObjects.Count) failed object(s) to re-convert:"
    foreach ($name in $failedNames) {
        Write-Host "  - $name"
    }
    Write-Host "[$Session] Invoke-RerunFailures: $($preservedObjects.Count) object(s) preserved (pass/skip)."

    # 3a. Re-convert only the failed objects
    Write-Host "[$Session] Step 1/3 (rerun): Convert..."
    $convertArgs  = @("convert", "--session", $Session, "--objects") + $failedNames
    $convertResult = Invoke-PipelineStep -StepName "convert (rerun)" -Arguments $convertArgs

    if ($convertResult.exitCode -ne 0) {
        Write-PipelineLog -Level "ERROR" -Step "convert (rerun)" `
            -Message $convertResult.errorMessage `
            -ElapsedSeconds $convertResult.elapsedSeconds
        return $null
    }
    Write-PipelineLog -Level "INFO" -Step "convert (rerun)" `
        -Message "Re-converted $($failedNames.Count) object(s)" `
        -ElapsedSeconds $convertResult.elapsedSeconds

    # 3b. Re-generate DDL for the re-converted objects
    Write-Host "[$Session] Step 2/3 (rerun): Generate..."
    $generateArgs  = @("generate", "--session", $Session, "--mode", "consolidated", "--objects") + $failedNames
    $generateResult = Invoke-PipelineStep -StepName "generate (rerun)" -Arguments $generateArgs

    if ($generateResult.exitCode -ne 0) {
        Write-PipelineLog -Level "ERROR" -Step "generate (rerun)" `
            -Message $generateResult.errorMessage `
            -ElapsedSeconds $generateResult.elapsedSeconds
        return $null
    }
    Write-PipelineLog -Level "INFO" -Step "generate (rerun)" `
        -Message "Re-generated DDL for $($failedNames.Count) object(s)" `
        -ElapsedSeconds $generateResult.elapsedSeconds

    # 3c. Re-validate the re-converted objects
    Write-Host "[$Session] Step 3/3 (rerun): Validate..."
    $validateStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        # Read all DDL from the session, filter to only the re-converted (previously failed) objects
        $allDdlStatements   = Read-ValidationResults -Session $Session
        $rerunDdlStatements = @($allDdlStatements | Where-Object { $failedNames -contains $_.objectName })

        $validationParams = @{ DdlStatements = $rerunDdlStatements }
        if ($ValMode -eq "live-instance" -and $PgConnString) {
            $validationParams.PgConnectionString = $PgConnString
        }
        elseif ($PgConnString -and $ValMode -ne "syntax-only") {
            $validationParams.PgConnectionString = $PgConnString
        }

        $validationResults = Invoke-PgValidation @validationParams

        $validateStopwatch.Stop()
        $validateElapsed = [Math]::Round($validateStopwatch.Elapsed.TotalSeconds, 2)
        Write-PipelineLog -Level "INFO" -Step "validate (rerun)" `
            -Message "Re-validated $($rerunDdlStatements.Count) object(s)" `
            -ElapsedSeconds $validateElapsed

        # Build object results for re-validated objects
        $rerunObjectResults = @()
        foreach ($valResult in $validationResults) {
            $ddlObj = $rerunDdlStatements | Where-Object { $_.objectName -eq $valResult.objectName }
            $rerunObjectResults += [PSCustomObject]@{
                objectName      = $valResult.objectName
                objectType      = if ($ddlObj) { $ddlObj.objectType } else { "Unknown" }
                databaseName    = $effectiveDbName
                status          = $valResult.status
                errorMessage    = $valResult.errorMessage
                errorLineNumber = $valResult.errorLineNumber
                generatedDdl    = if ($ddlObj) { $ddlObj.ddl } else { $null }
                validationMode  = $valResult.validationMode
            }
        }

        # Objects that were re-converted but produced no validation result → fail-convert
        foreach ($ddlObj in $rerunDdlStatements) {
            $hasResult = $rerunObjectResults | Where-Object { $_.objectName -eq $ddlObj.objectName }
            if (-not $hasResult) {
                $status = if ([string]::IsNullOrWhiteSpace($ddlObj.ddl)) { "fail-convert" } else { "skip" }
                $rerunObjectResults += [PSCustomObject]@{
                    objectName      = $ddlObj.objectName
                    objectType      = $ddlObj.objectType
                    databaseName    = $effectiveDbName
                    status          = $status
                    errorMessage    = if ($status -eq "fail-convert") { "Conversion produced no DDL output" } else { $null }
                    errorLineNumber = $null
                    generatedDdl    = $ddlObj.ddl
                    validationMode  = $null
                }
            }
        }
    }
    catch {
        $validateStopwatch.Stop()
        $validateElapsed = [Math]::Round($validateStopwatch.Elapsed.TotalSeconds, 2)
        Write-PipelineLog -Level "ERROR" -Step "validate (rerun)" `
            -Message $_.Exception.Message `
            -ElapsedSeconds $validateElapsed
        return $null
    }

    # 5. Merge: preserved pass/skip + newly re-validated results
    $mergedResults = @()
    $mergedResults += $preservedObjects
    $mergedResults += $rerunObjectResults

    $nowPassCount  = @($rerunObjectResults | Where-Object { $_.status -eq "pass" }).Count
    $stillFailCount = @($rerunObjectResults | Where-Object { $_.status -in @("fail-syntax","fail-convert") }).Count
    Write-Host "[$Session] Invoke-RerunFailures: $nowPassCount of $($failedObjects.Count) previously-failed object(s) now pass; $stillFailCount still failing."

    # 6. Return the merged object results array
    return $mergedResults
}

function Invoke-RerunFailuresPipeline {
    <#
    .SYNOPSIS
        Re-converts only failed objects from the most recent Scoring Report, then
        re-generates and re-validates them, merging results with preserved pass/skip objects.
    .DESCRIPTION
        Implements the "rerun-failures" mode per Requirement 4.3:
        - Reads the most recent Scoring Report for the specified session
        - Identifies objects with status "fail-syntax" or "fail-convert"
        - Re-converts only those objects (passing object names to the CLI)
        - Re-runs generate + validate for those objects
        - Merges re-converted results into new Scoring Report alongside preserved pass/skip results
    .OUTPUTS
        Hashtable with success status, objectResults (merged), and elapsed seconds.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Session,

        [string]$DbName,

        [string]$ValMode,

        [string]$PgConnString
    )

    $pipelineStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $stepResults = @()

    # Step 1: Load the most recent scoring report
    Write-Host "[$Session] Rerun-Failures: Loading most recent Scoring Report..."
    $previousReport = Get-MostRecentReport -Session $Session

    if (-not $previousReport) {
        Write-PipelineLog -Level "ERROR" -Step "rerun-load" -Message "No previous Scoring Report found for session '$Session'" -ElapsedSeconds 0
        $pipelineStopwatch.Stop()
        return @{
            success        = $false
            failedStep     = "rerun-load"
            stepResults    = @()
            objectResults  = @()
            elapsedSeconds = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)
        }
    }

    # Step 2: Identify failed objects
    $failedObjects = Get-FailedObjectsFromReport -Report $previousReport -Session $Session
    $preservedObjects = Get-PreservedObjectsFromReport -Report $previousReport -Session $Session

    if ($failedObjects.Count -eq 0) {
        Write-Host "[$Session] Rerun-Failures: No failed objects found in previous report. Nothing to re-run."
        $pipelineStopwatch.Stop()
        return @{
            success        = $true
            failedStep     = $null
            stepResults    = @()
            objectResults  = $preservedObjects
            elapsedSeconds = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)
        }
    }

    $failedNames = @($failedObjects | ForEach-Object { $_.objectName })
    Write-Host "[$Session] Rerun-Failures: Found $($failedObjects.Count) failed objects to re-convert:"
    foreach ($name in $failedNames) {
        Write-Host "  - $name"
    }

    # Step 3: Re-convert only the failed objects
    Write-Host "[$Session] Rerun-Failures: Re-converting failed objects..."
    $convertArgs = @("convert", "--session", $Session, "--objects") + $failedNames

    $convertResult = Invoke-PipelineStep -StepName "convert" -Arguments $convertArgs

    $stepResults += @{
        step           = "convert (rerun)"
        exitCode       = $convertResult.exitCode
        elapsedSeconds = $convertResult.elapsedSeconds
        errorMessage   = $convertResult.errorMessage
    }

    if ($convertResult.exitCode -ne 0) {
        Write-PipelineLog -Level "ERROR" -Step "convert (rerun)" -Message $convertResult.errorMessage -ElapsedSeconds $convertResult.elapsedSeconds
        $pipelineStopwatch.Stop()
        return @{
            success        = $false
            failedStep     = "convert (rerun)"
            stepResults    = $stepResults
            objectResults  = @()
            elapsedSeconds = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)
        }
    }

    Write-PipelineLog -Level "INFO" -Step "convert (rerun)" -Message "Re-conversion completed for $($failedNames.Count) objects" -ElapsedSeconds $convertResult.elapsedSeconds

    # Step 4: Re-generate for the re-converted objects
    Write-Host "[$Session] Rerun-Failures: Re-generating DDL for failed objects..."
    $generateArgs = @("generate", "--session", $Session, "--mode", "consolidated", "--objects") + $failedNames

    $generateResult = Invoke-PipelineStep -StepName "generate" -Arguments $generateArgs

    $stepResults += @{
        step           = "generate (rerun)"
        exitCode       = $generateResult.exitCode
        elapsedSeconds = $generateResult.elapsedSeconds
        errorMessage   = $generateResult.errorMessage
    }

    if ($generateResult.exitCode -ne 0) {
        Write-PipelineLog -Level "ERROR" -Step "generate (rerun)" -Message $generateResult.errorMessage -ElapsedSeconds $generateResult.elapsedSeconds
        $pipelineStopwatch.Stop()
        return @{
            success        = $false
            failedStep     = "generate (rerun)"
            stepResults    = $stepResults
            objectResults  = @()
            elapsedSeconds = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)
        }
    }

    Write-PipelineLog -Level "INFO" -Step "generate (rerun)" -Message "Re-generation completed for $($failedNames.Count) objects" -ElapsedSeconds $generateResult.elapsedSeconds

    # Step 5: Re-validate the re-converted objects
    Write-Host "[$Session] Rerun-Failures: Re-validating re-converted objects..."
    $validateStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        # Read only the re-converted objects from session
        $allDdlStatements = Read-ValidationResults -Session $Session

        # Filter to only re-converted (failed) objects
        $rerunDdlStatements = @($allDdlStatements | Where-Object { $failedNames -contains $_.objectName })

        $validationParams = @{
            DdlStatements = $rerunDdlStatements
        }

        # Determine validation mode
        if ($ValMode -eq "live-instance" -and $PgConnString) {
            $validationParams.PgConnectionString = $PgConnString
        }
        elseif ($ValMode -eq "syntax-only") {
            # No PG connection string - will use syntax-only mode
        }
        elseif ($PgConnString) {
            # Auto-detect: try live instance
            $validationParams.PgConnectionString = $PgConnString
        }

        $validationResults = Invoke-PgValidation @validationParams

        $validateStopwatch.Stop()
        $validateElapsed = [Math]::Round($validateStopwatch.Elapsed.TotalSeconds, 2)

        $stepResults += @{
            step           = "validate (rerun)"
            exitCode       = 0
            elapsedSeconds = $validateElapsed
            errorMessage   = $null
        }

        Write-PipelineLog -Level "INFO" -Step "validate (rerun)" -Message "Re-validation completed for $($rerunDdlStatements.Count) objects" -ElapsedSeconds $validateElapsed

        # Build object results for re-validated objects
        $rerunObjectResults = @()
        foreach ($valResult in $validationResults) {
            $ddlObj = $rerunDdlStatements | Where-Object { $_.objectName -eq $valResult.objectName }
            $rerunObjectResults += [PSCustomObject]@{
                objectName      = $valResult.objectName
                objectType      = if ($ddlObj) { $ddlObj.objectType } else { "Unknown" }
                databaseName    = if ($DbName) { $DbName } else { $Session }
                status          = $valResult.status
                errorMessage    = $valResult.errorMessage
                errorLineNumber = $valResult.errorLineNumber
                generatedDdl    = if ($ddlObj) { $ddlObj.ddl } else { $null }
                validationMode  = $valResult.validationMode
            }
        }

        # Include objects that were supposed to be re-validated but got no validation result (fail-convert)
        foreach ($ddlObj in $rerunDdlStatements) {
            $hasResult = $rerunObjectResults | Where-Object { $_.objectName -eq $ddlObj.objectName }
            if (-not $hasResult) {
                $status = if ([string]::IsNullOrWhiteSpace($ddlObj.ddl)) { "fail-convert" } else { "skip" }
                $rerunObjectResults += [PSCustomObject]@{
                    objectName      = $ddlObj.objectName
                    objectType      = $ddlObj.objectType
                    databaseName    = if ($DbName) { $DbName } else { $Session }
                    status          = $status
                    errorMessage    = if ($status -eq "fail-convert") { "Conversion produced no DDL output" } else { $null }
                    errorLineNumber = $null
                    generatedDdl    = $ddlObj.ddl
                    validationMode  = $null
                }
            }
        }
    }
    catch {
        $validateStopwatch.Stop()
        $validateElapsed = [Math]::Round($validateStopwatch.Elapsed.TotalSeconds, 2)

        Write-PipelineLog -Level "ERROR" -Step "validate (rerun)" -Message $_.Exception.Message -ElapsedSeconds $validateElapsed
        $stepResults += @{
            step           = "validate (rerun)"
            exitCode       = 1
            elapsedSeconds = $validateElapsed
            errorMessage   = $_.Exception.Message
        }

        $pipelineStopwatch.Stop()
        return @{
            success        = $false
            failedStep     = "validate (rerun)"
            stepResults    = $stepResults
            objectResults  = @()
            elapsedSeconds = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)
        }
    }

    # Step 6: Merge re-converted results with preserved pass/skip results
    Write-Host "[$Session] Rerun-Failures: Merging results..."
    $mergedResults = @()

    # Add all preserved (pass/skip) objects
    $mergedResults += $preservedObjects

    # Add all re-validated objects (replacing old failed results)
    $mergedResults += $rerunObjectResults

    $pipelineStopwatch.Stop()
    $totalElapsed = [Math]::Round($pipelineStopwatch.Elapsed.TotalSeconds, 2)

    $rerunPassCount = @($rerunObjectResults | Where-Object { $_.status -eq "pass" }).Count
    $rerunStillFailCount = @($rerunObjectResults | Where-Object { $_.status -eq "fail-syntax" -or $_.status -eq "fail-convert" }).Count
    Write-Host "[$Session] Rerun-Failures: $rerunPassCount of $($failedObjects.Count) previously-failed objects now pass. $rerunStillFailCount still failing."

    return @{
        success        = $true
        failedStep     = $null
        stepResults    = $stepResults
        objectResults  = $mergedResults
        elapsedSeconds = $totalElapsed
    }
}

function Get-PreviousReport {
    <#
    .SYNOPSIS
        Loads the most recent previous Scoring Report as a full object.
    .OUTPUTS
        PSCustomObject of the parsed JSON report, or $null if no previous report exists.
    #>
    param([string]$OutputDir)

    $targetDir = if ($OutputDir) { $OutputDir } else { $reportsDir }

    if (-not (Test-Path $targetDir)) {
        return $null
    }

    $previousReports = Get-ChildItem -Path $targetDir -Filter "scoring-report-*.json" -File |
        Sort-Object -Property LastWriteTime -Descending

    if ($previousReports.Count -eq 0) {
        return $null
    }

    try {
        $report = Get-Content -Path $previousReports[0].FullName -Raw | ConvertFrom-Json
        return $report
    }
    catch {
        Write-Warning "Failed to read previous scoring report: $($_.Exception.Message)"
        return $null
    }
}

function Get-TypesRequiringReconversion {
    <#
    .SYNOPSIS
        Determines which object types need re-conversion by comparing config file hashes.
    .DESCRIPTION
        Compares SHA-256 hashes from $PreviousConfigHashes against $CurrentConfigHashes.
        For every file whose hash differs (or that was added/removed), maps the file name
        to the object types it affects and returns the distinct set of those types.

        Prompt-to-type mapping:
        - stored-procedure.*.md       → StoredProcedure
        - function.*.md               → Function
        - view.*.md                   → View
        - trigger.*.md                → Trigger
        - complex-object.*.md         → all types (Table, View, StoredProcedure, Function, Trigger)
        - type-mappings.json          → all types
        - function-mappings.json      → all types
        - schema-mappings.json        → all types

    .PARAMETER PreviousConfigHashes
        Hashtable of filename → SHA-256 hash as stored in the previous Scoring Report.

    .PARAMETER CurrentConfigHashes
        Hashtable of filename → SHA-256 hash computed from the current config files on disk.

    .OUTPUTS
        Array of distinct object type strings that need re-conversion, or an empty array
        when no hash differences are detected.

    .NOTES
        Requirements: 4.4
    #>
    param(
        [Parameter(Mandatory)]
        [hashtable]$PreviousConfigHashes,

        [Parameter(Mandatory)]
        [hashtable]$CurrentConfigHashes
    )

    $allTypes = @("StoredProcedure", "Function", "View", "Trigger", "Table")

    # Static mapping for well-known config/mapping files
    $fileTypeMapping = @{
        "type-mappings.json"     = $allTypes
        "function-mappings.json" = $allTypes
        "schema-mappings.json"   = $allTypes
    }

    # Ordered prompt-template patterns; first match wins
    $promptPatterns = @(
        @{ Pattern = "^stored-procedure\..*\.md$"; Types = @("StoredProcedure") }
        @{ Pattern = "^function\..*\.md$";         Types = @("Function") }
        @{ Pattern = "^view\..*\.md$";             Types = @("View") }
        @{ Pattern = "^trigger\..*\.md$";          Types = @("Trigger") }
        @{ Pattern = "^complex-object\..*\.md$";   Types = $allTypes }
    )

    $affectedTypes = @()

    # Examine every file name present in either hash set
    $allFileNames = @($CurrentConfigHashes.Keys) + @($PreviousConfigHashes.Keys) |
        Select-Object -Unique

    foreach ($fileName in $allFileNames) {
        $currentHash  = $CurrentConfigHashes[$fileName]
        $previousHash = $PreviousConfigHashes[$fileName]

        # Detect any change: added, removed, or modified
        $hashChanged = (
            ($null -eq $previousHash -and $null -ne $currentHash) -or
            ($null -ne $previousHash -and $null -eq $currentHash) -or
            ($currentHash -ne $previousHash)
        )

        if (-not $hashChanged) {
            continue
        }

        # Resolve affected types for this file
        $typesForFile = $null

        if ($fileTypeMapping.ContainsKey($fileName)) {
            $typesForFile = $fileTypeMapping[$fileName]
        }
        else {
            foreach ($patternEntry in $promptPatterns) {
                if ($fileName -match $patternEntry.Pattern) {
                    $typesForFile = $patternEntry.Types
                    break
                }
            }
        }

        if ($typesForFile) {
            $affectedTypes += $typesForFile
            Write-Host "Config change detected: $fileName (affects: $($typesForFile -join ', '))"
        }
    }

    # Return distinct, sorted list for deterministic output
    $distinctTypes = @($affectedTypes | Select-Object -Unique | Sort-Object)
    return $distinctTypes
}

function Get-ChangedConfigTypes {
    <#
    .SYNOPSIS
        Detects which object types need re-conversion due to config/prompt file changes.
    .DESCRIPTION
        Loads hashes from the most recent Scoring Report and the current config files on
        disk, then delegates comparison to Get-TypesRequiringReconversion.
    .OUTPUTS
        Array of distinct object type strings that need re-conversion, or empty array if
        no changes detected.
    #>
    param([string]$OutputDir)

    # Load previous report to get stored config hashes
    $previousReport = Get-PreviousReport -OutputDir $OutputDir
    if (-not $previousReport) {
        # No previous report – no baseline to compare against
        return @()
    }

    # Convert PSCustomObject properties to a plain hashtable
    $previousHashes = @{}
    if ($previousReport.configHashes) {
        $previousReport.configHashes.PSObject.Properties | ForEach-Object {
            $previousHashes[$_.Name] = $_.Value
        }
    }

    if ($previousHashes.Count -eq 0) {
        # Previous report had no config hashes recorded
        return @()
    }

    # Compute current hashes from disk
    $currentHashes = Get-ConfigFileHashes

    return Get-TypesRequiringReconversion `
        -PreviousConfigHashes $previousHashes `
        -CurrentConfigHashes  $currentHashes
}

function Write-BatchSummaryTable {
    <#
    .SYNOPSIS
        Prints a formatted summary table for a completed batch execution.
    .DESCRIPTION
        Outputs a table with columns: Database | Objects | Pass | Fail | Score
        Databases that failed completely show "ERROR" in the Score column.
        Requirements: 5.3, 5.5
    #>
    param(
        [Parameter(Mandatory)]
        [array]$BatchResults
    )

    # Column widths
    $dbWidth    = [Math]::Max(8,  ($BatchResults | ForEach-Object { $_.DatabaseName.Length } | Measure-Object -Maximum).Maximum)
    $objWidth   = 7   # "Objects"
    $passWidth  = 4   # "Pass"
    $failWidth  = 4   # "Fail"
    $scoreWidth = 7   # "Score"

    $separator = "+-$("-" * $dbWidth)-+-$("-" * $objWidth)-+-$("-" * $passWidth)-+-$("-" * $failWidth)-+-$("-" * $scoreWidth)-+"
    $header    = "| $("Database".PadRight($dbWidth)) | $("Objects".PadRight($objWidth)) | $("Pass".PadRight($passWidth)) | $("Fail".PadRight($failWidth)) | $("Score".PadRight($scoreWidth)) |"

    Write-Host ""
    Write-Host "=== Batch Execution Summary ==="
    Write-Host $separator
    Write-Host $header
    Write-Host $separator

    foreach ($entry in $BatchResults) {
        $dbCol    = $entry.DatabaseName.PadRight($dbWidth)
        $objCol   = if ($entry.Failed) { "N/A".PadRight($objWidth) } else { [string]$entry.ObjectCount }
        $passCol  = if ($entry.Failed) { "N/A".PadRight($passWidth) } else { [string]$entry.PassCount }
        $failCol  = if ($entry.Failed) { "N/A".PadRight($failWidth) } else { [string]$entry.FailCount }
        $scoreCol = if ($entry.Failed) { "ERROR".PadRight($scoreWidth) } else {
            if ($entry.Score -eq "N/A") { "N/A".PadRight($scoreWidth) }
            else { "$($entry.Score)%".PadRight($scoreWidth) }
        }

        # Right-align numeric columns
        if (-not $entry.Failed) {
            $objCol  = $objCol.ToString().PadLeft($objWidth)
            $passCol = $passCol.ToString().PadLeft($passWidth)
            $failCol = $failCol.ToString().PadLeft($failWidth)
        }

        Write-Host "| $dbCol | $objCol | $passCol | $failCol | $scoreCol |"
    }

    Write-Host $separator
    Write-Host ""
}

function Invoke-BatchPipeline {
    <#
    .SYNOPSIS
        Executes the migration validation pipeline for each database defined in the
        pipeline-config.json file, collects results, and produces a combined Scoring Report.
    .DESCRIPTION
        Implements Requirements 5.1, 5.2, 5.3, 5.4, 5.5:
        - Parses pipeline-config.json and iterates over each database entry sequentially
        - On connection failure or complete database failure, logs error and continues
        - Collects all successful results into a combined object results array
        - After all databases, produces a combined scoring report
        - Prints a summary table with database name, object count, pass/fail counts, score
        - Shows "ERROR" for databases that failed completely
        - Returns a hashtable with the report path, batch results, and overall exit code
    .PARAMETER ConfigPath
        Path to the pipeline-config.json batch configuration file.
    .PARAMETER ValMode
        Validation mode override for all databases.
    .PARAMETER PgConnStringOverride
        Override for PostgreSQL connection string (takes precedence over config file value).
    .OUTPUTS
        Hashtable with keys: Success (bool), ReportPath (string), BatchResults (array),
        HasCompleteFailures (bool)
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath,

        [string]$ValMode,

        [string]$PgConnStringOverride
    )

    # -----------------------------------------------------------------------
    # 1. Parse configuration file (Req 5.1)
    # -----------------------------------------------------------------------
    if (-not (Test-Path $ConfigPath)) {
        Write-Error "Batch config file not found: $ConfigPath"
        return @{ Success = $false; HasCompleteFailures = $true }
    }

    $configRaw = $null
    try {
        $configRaw = Get-Content -Path $ConfigPath -Raw | ConvertFrom-Json
    }
    catch {
        Write-Error "Failed to parse batch config file '$ConfigPath': $($_.Exception.Message)"
        return @{ Success = $false; HasCompleteFailures = $true }
    }

    if (-not $configRaw.databases -or $configRaw.databases.Count -eq 0) {
        Write-Error "Batch config file contains no database entries."
        return @{ Success = $false; HasCompleteFailures = $true }
    }

    # Resolve validation and reporting settings from config
    $configPgConnString  = if ($configRaw.validation -and $configRaw.validation.pgConnectionString) {
        $configRaw.validation.pgConnectionString
    } else { $null }

    $effectivePgConnString = if ($PgConnStringOverride) { $PgConnStringOverride }
                             elseif ($configPgConnString)  { $configPgConnString }
                             else { $null }

    $outputDir = if ($configRaw.reporting -and $configRaw.reporting.outputDirectory) {
        # Resolve relative paths relative to the config file's directory
        $configFileDir = Split-Path -Parent (Resolve-Path $ConfigPath).Path
        Join-Path $configFileDir $configRaw.reporting.outputDirectory
    } else { $reportsDir }

    # Ensure reports directory exists
    if (-not (Test-Path $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    # Load previous scores for delta computation
    $batchPreviousScores = Get-PreviousScores -OutputDir $outputDir

    # Resolve end-to-end configuration from batch config
    $batchE2eEnabled = $false
    $batchE2eDestConnStr = $null
    $batchE2eMaintenanceConnStr = $null
    $batchE2eMaxFixAttempts = 2
    $batchE2eTestScriptDir = $null
    $batchE2ePgPassthroughPath = $null
    $batchE2ePgPassthroughPort = 11433
    $batchE2eTimeoutPerScript = 30

    if ($configRaw.endToEnd) {
        if ($configRaw.endToEnd.enabled) {
            $batchE2eEnabled = $true
        }
        if ($configRaw.endToEnd.destinationConnectionString) {
            $batchE2eDestConnStr = $configRaw.endToEnd.destinationConnectionString
        }
        if ($configRaw.endToEnd.maintenanceConnectionString) {
            $batchE2eMaintenanceConnStr = $configRaw.endToEnd.maintenanceConnectionString
        }
        if ($configRaw.endToEnd.maxFixAttempts) {
            $batchE2eMaxFixAttempts = $configRaw.endToEnd.maxFixAttempts
        }
        if ($configRaw.endToEnd.testScriptDirectory) {
            $batchE2eTestScriptDir = $configRaw.endToEnd.testScriptDirectory
        }
        if ($configRaw.endToEnd.pgPassthroughPath) {
            $batchE2ePgPassthroughPath = $configRaw.endToEnd.pgPassthroughPath
        }
        if ($configRaw.endToEnd.pgPassthroughPort) {
            $batchE2ePgPassthroughPort = $configRaw.endToEnd.pgPassthroughPort
        }
        if ($configRaw.endToEnd.timeoutPerScript) {
            $batchE2eTimeoutPerScript = $configRaw.endToEnd.timeoutPerScript
        }
    }

    # Validate E2E configuration for batch mode (Requirement 6.5)
    if ($batchE2eEnabled) {
        $batchE2eConfigErrors = @()

        if (-not $batchE2eDestConnStr) {
            $batchE2eConfigErrors += "End-to-end mode enabled in config but no destinationConnectionString provided."
        }

        # Derive maintenance connection from destination if not provided
        if (-not $batchE2eMaintenanceConnStr) {
            if ($batchE2eDestConnStr) {
                $batchE2eMaintenanceConnStr = $batchE2eDestConnStr -replace 'Database=[^;]+', 'Database=postgres'
                if ($batchE2eMaintenanceConnStr -eq $batchE2eDestConnStr) {
                    $batchE2eMaintenanceConnStr = "$batchE2eDestConnStr;Database=postgres"
                }
                Write-Host "  Maintenance connection derived from destination (targeting 'postgres' database)"
            }
            else {
                $batchE2eConfigErrors += "No maintenanceConnectionString provided and cannot derive from missing destinationConnectionString."
            }
        }

        # Check pgPassthroughPath exists if specified
        if ($batchE2ePgPassthroughPath -and -not (Test-Path $batchE2ePgPassthroughPath)) {
            $batchE2eConfigErrors += "PgPassthrough path does not exist: '$batchE2ePgPassthroughPath'. Functional tests will not be able to run."
        }

        # Validate scoring weights if present
        if ($configRaw.endToEnd.scoring) {
            $scoring = $configRaw.endToEnd.scoring
            $weightSum = 0
            if ($scoring.ddlWeight) { $weightSum += $scoring.ddlWeight }
            if ($scoring.dataWeight) { $weightSum += $scoring.dataWeight }
            if ($scoring.testWeight) { $weightSum += $scoring.testWeight }
            if ([Math]::Abs($weightSum - 1.0) -gt 0.01) {
                $batchE2eConfigErrors += "Scoring weights do not sum to 1.0 (actual: $weightSum). DDL=$($scoring.ddlWeight), Data=$($scoring.dataWeight), Test=$($scoring.testWeight)"
            }
        }

        # Report errors
        if ($batchE2eConfigErrors.Count -gt 0) {
            foreach ($err in $batchE2eConfigErrors) {
                Write-PipelineLog -Level "ERROR" -Step "e2e-config-validation" -Message $err -ElapsedSeconds 0
            }
            if (-not $batchE2eDestConnStr) {
                $batchE2eEnabled = $false
            }
        }
    }

    # Resolve default PgPassthrough path for batch mode
    if (-not $batchE2ePgPassthroughPath) {
        $batchE2ePgPassthroughPath = Join-Path $repoRoot "PgPassthrough\src\PgPassthrough.Server"
    }

    Write-Host "=== Migration Validation Pipeline (Batch Mode) ==="
    Write-Host "Config:    $ConfigPath"
    Write-Host "Databases: $($configRaw.databases.Count)"
    Write-Host "Output:    $outputDir"
    Write-Host "=================================================="
    Write-Host ""

    # -----------------------------------------------------------------------
    # 2. Iterate each database entry sequentially (Req 5.2, 5.4)
    # -----------------------------------------------------------------------
    $batchStopwatch      = [System.Diagnostics.Stopwatch]::StartNew()
    $allObjectResults    = @()
    $batchResults        = @()    # summary rows
    $hasCompleteFailures = $false

    foreach ($dbEntry in $configRaw.databases) {
        # Validate required fields; skip with warning on missing data
        if (-not $dbEntry.name) {
            Write-Warning "Skipping database entry with no 'name' field."
            continue
        }
        if (-not $dbEntry.connectionString) {
            Write-Warning "Skipping database '$($dbEntry.name)': missing 'connectionString'."
            $batchResults += [PSCustomObject]@{
                DatabaseName  = $dbEntry.name
                Failed        = $true
                FailureReason = "Missing connectionString in config"
                ObjectCount   = 0
                PassCount     = 0
                FailCount     = 0
                Score         = "N/A"
            }
            $hasCompleteFailures = $true
            continue
        }
        if (-not $dbEntry.sessionName) {
            Write-Warning "Skipping database '$($dbEntry.name)': missing 'sessionName'."
            $batchResults += [PSCustomObject]@{
                DatabaseName  = $dbEntry.name
                Failed        = $true
                FailureReason = "Missing sessionName in config"
                ObjectCount   = 0
                PassCount     = 0
                FailCount     = 0
                Score         = "N/A"
            }
            $hasCompleteFailures = $true
            continue
        }

        $dbName     = $dbEntry.name
        $connString = $dbEntry.connectionString
        $session    = $dbEntry.sessionName

        Write-Host "--- Database: $dbName (session: $session) ---"

        # Detect config changes that require re-conversion for this run
        $changedConfigTypes = Get-ChangedConfigTypes -OutputDir $outputDir

        # Execute the pipeline for this database (Req 5.4 – failure does not halt batch)
        $dbResult = $null
        try {
            # Resolve per-database E2E settings
            $dbE2eDestConnStr = $batchE2eDestConnStr
            $dbE2eDbName = "${dbName}_e2e"
            $dbE2eTestScriptDir = $batchE2eTestScriptDir

            if ($batchE2eEnabled -and $configRaw.endToEnd.databases) {
                $dbE2eOverride = $configRaw.endToEnd.databases.PSObject.Properties[$dbName]
                if ($dbE2eOverride) {
                    if ($dbE2eOverride.Value.destinationDatabase) {
                        $dbE2eDbName = $dbE2eOverride.Value.destinationDatabase
                    }
                    if ($dbE2eOverride.Value.testScripts) {
                        $dbE2eTestScriptDir = $dbE2eOverride.Value.testScripts
                    }
                }
            }

            # Resolve test script directory relative to MigrationAssessment root if not absolute
            if ($dbE2eTestScriptDir -and -not [System.IO.Path]::IsPathRooted($dbE2eTestScriptDir)) {
                $dbE2eTestScriptDir = Join-Path $scriptDir "..\$dbE2eTestScriptDir"
            }
            elseif (-not $dbE2eTestScriptDir) {
                $dbE2eTestScriptDir = Join-Path $scriptDir "..\tests\functional\$dbName"
            }

            $dbResult = Invoke-SingleDatabasePipeline `
                -ConnString        $connString `
                -Session           $session `
                -DbName            $dbName `
                -ValMode           $ValMode `
                -PgConnString      $effectivePgConnString `
                -ChangedConfigTypes $changedConfigTypes `
                -EndToEndEnabled   $batchE2eEnabled `
                -DestPgConnString  $dbE2eDestConnStr `
                -E2eMaintenanceConnString $batchE2eMaintenanceConnStr `
                -E2eDatabaseName   $dbE2eDbName `
                -E2eMaxFixAttempts $batchE2eMaxFixAttempts `
                -E2eTestScriptDir  $dbE2eTestScriptDir `
                -E2ePgPassthroughPath $batchE2ePgPassthroughPath `
                -E2ePgPassthroughPort $batchE2ePgPassthroughPort `
                -E2eTimeoutPerScript $batchE2eTimeoutPerScript
        }
        catch {
            # Unhandled exception (e.g. connection failure) – log and continue (Req 5.4)
            $errMsg = $_.Exception.Message
            Write-PipelineLog -Level "ERROR" -Step "batch:$dbName" `
                -Message "Database '$dbName' failed with unhandled exception: $errMsg" `
                -ElapsedSeconds 0
            $batchResults += [PSCustomObject]@{
                DatabaseName  = $dbName
                Failed        = $true
                FailureReason = $errMsg
                ObjectCount   = 0
                PassCount     = 0
                FailCount     = 0
                Score         = "N/A"
            }
            $hasCompleteFailures = $true
            continue
        }

        if (-not $dbResult -or -not $dbResult.success) {
            # Pipeline failed for this database – log and continue (Req 5.4)
            $failedStep = if ($dbResult) { $dbResult.failedStep } else { "unknown" }
            $errMsg     = "Pipeline failed at step '$failedStep' for database '$dbName'"
            Write-PipelineLog -Level "ERROR" -Step "batch:$dbName" -Message $errMsg -ElapsedSeconds 0
            $batchResults += [PSCustomObject]@{
                DatabaseName  = $dbName
                Failed        = $true
                FailureReason = $errMsg
                ObjectCount   = 0
                PassCount     = 0
                FailCount     = 0
                Score         = "N/A"
            }
            $hasCompleteFailures = $true
            continue
        }

        # Successful database run – accumulate object results
        $dbObjects    = @($dbResult.objectResults)
        $allObjectResults += $dbObjects

        # Compute per-database summary counts for the table
        $passCount    = @($dbObjects | Where-Object { $_.status -eq "pass" }).Count
        $failSyntax   = @($dbObjects | Where-Object { $_.status -eq "fail-syntax" }).Count
        $failConvert  = @($dbObjects | Where-Object { $_.status -eq "fail-convert" }).Count
        $totalFail    = $failSyntax + $failConvert
        $divisor      = $passCount + $totalFail
        $score        = if ($divisor -gt 0) {
            [Math]::Round(($passCount / $divisor) * 100, 1)
        } else { "N/A" }

        $batchResults += [PSCustomObject]@{
            DatabaseName  = $dbName
            Failed        = $false
            FailureReason = $null
            ObjectCount   = $dbObjects.Count
            PassCount     = $passCount
            FailCount     = $totalFail
            Score         = $score
        }

        Write-Host "[$dbName] Completed: $($dbObjects.Count) objects | Pass: $passCount | Fail: $totalFail | Score: $score%"
        Write-Host ""
    }

    $batchStopwatch.Stop()
    $totalElapsed = [Math]::Round($batchStopwatch.Elapsed.TotalSeconds, 2)

    # -----------------------------------------------------------------------
    # 3. Produce combined Scoring Report (Req 5.2)
    # -----------------------------------------------------------------------
    $reportPath = $null

    if ($allObjectResults.Count -gt 0) {
        Write-Host "Building combined Scoring Report for $($allObjectResults.Count) objects across $(@($batchResults | Where-Object { -not $_.Failed }).Count) database(s)..."

        # Wire modules: Validation outputs → Scoring → Diagnostics → Report Generation
        $scoringResult = Invoke-Scoring -ObjectResults $allObjectResults -PreviousScores $batchPreviousScores

        $failedObjects = @($allObjectResults | Where-Object {
            $_.status -eq "fail-syntax" -or $_.status -eq "fail-convert"
        })
        $diagnosticsResult = Invoke-DiagnosticsClassification -FailedObjects $failedObjects

        $configHashes = Get-ConfigFileHashes

        # Determine effective validation mode
        $effectiveValMode = $ValMode
        if (-not $effectiveValMode) {
            $modeFromResults = $allObjectResults | Where-Object { $_.validationMode } | Select-Object -First 1
            if ($modeFromResults) {
                $effectiveValMode = $modeFromResults.validationMode
            }
            else {
                $effectiveValMode = "syntax-only"
            }
        }

        # Find previous report path for delta computation
        $previousReportPath = $null
        $previousReportFiles = Get-ChildItem -Path $outputDir -Filter "scoring-report-*.json" -File -ErrorAction SilentlyContinue |
            Sort-Object -Property LastWriteTime -Descending
        if ($previousReportFiles -and $previousReportFiles.Count -gt 0) {
            $previousReportPath = $previousReportFiles[0].FullName
        }

        # Generate and save report via Invoke-ReportGeneration
        $reportGenResult = Invoke-ReportGeneration `
            -ScoringResult $scoringResult `
            -DiagnosticsResult $diagnosticsResult `
            -ObjectResults $allObjectResults `
            -TotalElapsedSeconds $totalElapsed `
            -ValidationMode $effectiveValMode `
            -ConfigHashes $configHashes `
            -OutputDirectory $outputDir `
            -PreviousReportPath $previousReportPath

        $report = $reportGenResult.Report
        $reportPath = $reportGenResult.ReportPath

        # Add failed database entries to the report's databases array with pipeline-error status
        $failedEntries = @($batchResults | Where-Object { $_.Failed })
        if ($failedEntries.Count -gt 0) {
            $errorDatabases = @($failedEntries | ForEach-Object {
                [PSCustomObject][ordered]@{
                    name           = $_.DatabaseName
                    sessionName    = $_.DatabaseName
                    objectCount    = 0
                    elapsedSeconds = 0
                    status         = "pipeline-error"
                    errorMessage   = $_.FailureReason
                    score          = [PSCustomObject]@{
                        compatibilityScore = $null
                        previousScore      = $null
                        delta              = $null
                        pass               = 0
                        failSyntax         = 0
                        failConvert        = 0
                        skip               = 0
                    }
                    byType         = [PSCustomObject]@{}
                    objects        = @()
                }
            })
            # Append error databases to the report
            $report.databases = @($report.databases) + $errorDatabases
        }
    }
    else {
        Write-Warning "No successful database runs produced any object results. Skipping combined Scoring Report."
    }

    # -----------------------------------------------------------------------
    # 4. Print summary table (Req 5.3, 5.5)
    # -----------------------------------------------------------------------
    Write-BatchSummaryTable -BatchResults $batchResults

    # Batch totals line
    $totalObjects = ($batchResults | Where-Object { -not $_.Failed } | Measure-Object -Property ObjectCount -Sum).Sum
    $totalPass    = ($batchResults | Where-Object { -not $_.Failed } | Measure-Object -Property PassCount   -Sum).Sum
    $totalFail    = ($batchResults | Where-Object { -not $_.Failed } | Measure-Object -Property FailCount   -Sum).Sum
    $successCount = @($batchResults | Where-Object { -not $_.Failed }).Count
    $failedCount  = @($batchResults | Where-Object { $_.Failed }).Count

    Write-Host "Batch complete: $successCount succeeded, $failedCount failed | Total objects: $totalObjects | Pass: $totalPass | Fail: $totalFail"
    Write-Host "Total elapsed: $totalElapsed seconds"
    if ($reportPath) {
        Write-Host "Combined report: $reportPath"
    }
    Write-Host ""

    return @{
        Success             = $true
        ReportPath          = $reportPath
        BatchResults        = $batchResults
        HasCompleteFailures = $hasCompleteFailures
    }
}

# ============================================================================
# Main Execution
# ============================================================================

# Validate parameters
if (-not $ConnectionString -and -not $BatchConfig -and -not $RerunFailures) {
    Write-Error "Either -ConnectionString, -BatchConfig, or -RerunFailures must be specified."
    exit 1
}

if ($ConnectionString -and -not $SessionName) {
    Write-Error "-SessionName is required when using -ConnectionString."
    exit 1
}

if ($RerunFailures -and -not $SessionName) {
    Write-Error "-SessionName is required when using -RerunFailures."
    exit 1
}

# Load previous scores for delta computation
$previousScores = Get-PreviousScores

# -----------------------------------------------------------------------
# Batch mode (Req 5.1 – 5.5)
# -----------------------------------------------------------------------
if ($BatchConfig) {
    $batchOutcome = Invoke-BatchPipeline `
        -ConfigPath          $BatchConfig `
        -ValMode             $ValidationMode `
        -PgConnStringOverride $PgConnectionString

    # Req 5.5: non-zero exit if any database failed completely; zero otherwise
    if ($batchOutcome.HasCompleteFailures) {
        exit 1
    }
    exit 0
}

# Rerun-Failures mode
if ($RerunFailures) {
    Write-Host "=== Migration Validation Pipeline ==="
    Write-Host "Mode: Rerun Failures"
    Write-Host "Session: $SessionName"
    Write-Host "======================================"
    Write-Host ""

    $rerunStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    $mergedObjectResults = Invoke-RerunFailures `
        -Session $SessionName `
        -DbName $SessionName `
        -ValMode $ValidationMode `
        -PgConnString $PgConnectionString

    $rerunStopwatch.Stop()
    $rerunElapsed = [Math]::Round($rerunStopwatch.Elapsed.TotalSeconds, 2)

    if ($null -eq $mergedObjectResults) {
        Write-Host ""
        Write-Host "RERUN-FAILURES FAILED. See error messages above." -ForegroundColor Red
        Write-Host "Total elapsed: $rerunElapsed seconds"
        exit 1
    }

    # Wire modules: Validation outputs → Scoring → Diagnostics → Report Generation
    $scoringResult = Invoke-Scoring -ObjectResults $mergedObjectResults -PreviousScores $previousScores

    $failedObjects = @($mergedObjectResults | Where-Object {
        $_.status -eq "fail-syntax" -or $_.status -eq "fail-convert"
    })
    $diagnosticsResult = Invoke-DiagnosticsClassification -FailedObjects $failedObjects

    $configHashes = Get-ConfigFileHashes

    # Determine effective validation mode
    $effectiveValMode = $ValidationMode
    if (-not $effectiveValMode) {
        $modeFromResults = $mergedObjectResults | Where-Object { $_.validationMode } | Select-Object -First 1
        if ($modeFromResults) {
            $effectiveValMode = $modeFromResults.validationMode
        }
        else {
            $effectiveValMode = "syntax-only"
        }
    }

    # Find previous report path for delta computation
    $previousReportPath = $null
    $previousReportFiles = Get-ChildItem -Path $reportsDir -Filter "scoring-report-*.json" -File -ErrorAction SilentlyContinue |
        Sort-Object -Property LastWriteTime -Descending
    if ($previousReportFiles -and $previousReportFiles.Count -gt 0) {
        $previousReportPath = $previousReportFiles[0].FullName
    }

    # Generate and save report via Invoke-ReportGeneration
    $reportGenResult = Invoke-ReportGeneration `
        -ScoringResult $scoringResult `
        -DiagnosticsResult $diagnosticsResult `
        -ObjectResults $mergedObjectResults `
        -TotalElapsedSeconds $rerunElapsed `
        -ValidationMode $effectiveValMode `
        -ConfigHashes $configHashes `
        -OutputDirectory $reportsDir `
        -PreviousReportPath $previousReportPath

    $report = $reportGenResult.Report
    $reportPath = $reportGenResult.ReportPath

    # Print summary
    Write-Host ""
    Write-Host "=== Rerun-Failures Complete ==="
    Write-Host "Total elapsed: $rerunElapsed seconds"
    Write-Host "Compatibility Score: $($report.aggregate.compatibilityScore)%"
    Write-Host "Pass: $($report.aggregate.totalPass) | Fail-Syntax: $($report.aggregate.totalFailSyntax) | Fail-Convert: $($report.aggregate.totalFailConvert) | Skip: $($report.aggregate.totalSkip)"
    Write-Host "Report: $reportPath"

    exit 0
}

# Single-database mode
if ($ConnectionString -and $SessionName) {
    Write-Host "=== Migration Validation Pipeline ==="
    Write-Host "Mode: Single Database"
    Write-Host "Session: $SessionName"
    Write-Host "======================================"
    Write-Host ""

    # Check for config/prompt file changes that require re-conversion of specific types
    $changedConfigTypes = Get-ChangedConfigTypes
    if ($changedConfigTypes.Count -gt 0) {
        Write-Host "Config/prompt file changes detected. Types requiring re-conversion: $($changedConfigTypes -join ', ')"
        Write-Host ""
    }

    # Determine end-to-end configuration
    $e2eEnabled = $EndToEnd.IsPresent
    $e2eDestConnStr = $DestPgConnectionString
    $e2eMaintenanceConnStr = $null
    $e2eDbName = $null
    $e2eMaxAttempts = $MaxFixAttempts
    $e2eTestScriptDir = $null
    $e2ePgPassthroughPath = $null
    $e2ePgPassthroughPortVal = $PgPassthroughPort
    $e2eTimeoutPerScript = 30

    # Load config if BatchConfig-style config exists for single-database mode
    # Check for pipeline-config.json in the default location
    $singleDbConfigPath = Join-Path $scriptDir "..\pipeline-config.json"
    if (-not (Test-Path $singleDbConfigPath)) {
        $singleDbConfigPath = Join-Path $scriptDir "pipeline-config.json"
    }
    if (Test-Path $singleDbConfigPath) {
        try {
            $singleDbConfig = Get-Content -Path $singleDbConfigPath -Raw | ConvertFrom-Json
            if ($singleDbConfig.endToEnd) {
                if ($singleDbConfig.endToEnd.enabled -and -not $e2eEnabled) {
                    $e2eEnabled = $true
                }
                if (-not $e2eDestConnStr -and $singleDbConfig.endToEnd.destinationConnectionString) {
                    $e2eDestConnStr = $singleDbConfig.endToEnd.destinationConnectionString
                }
                if ($singleDbConfig.endToEnd.maintenanceConnectionString) {
                    $e2eMaintenanceConnStr = $singleDbConfig.endToEnd.maintenanceConnectionString
                }
                if ($singleDbConfig.endToEnd.maxFixAttempts) {
                    $e2eMaxAttempts = $singleDbConfig.endToEnd.maxFixAttempts
                }
                if ($singleDbConfig.endToEnd.testScriptDirectory) {
                    $e2eTestScriptDir = $singleDbConfig.endToEnd.testScriptDirectory
                }
                if ($singleDbConfig.endToEnd.pgPassthroughPath) {
                    $e2ePgPassthroughPath = $singleDbConfig.endToEnd.pgPassthroughPath
                }
                if ($singleDbConfig.endToEnd.pgPassthroughPort) {
                    $e2ePgPassthroughPortVal = $singleDbConfig.endToEnd.pgPassthroughPort
                }
                if ($singleDbConfig.endToEnd.timeoutPerScript) {
                    $e2eTimeoutPerScript = $singleDbConfig.endToEnd.timeoutPerScript
                }
                if ($singleDbConfig.endToEnd.scoring) {
                    # Scoring weights are available for downstream scoring module
                    # They are read by Invoke-EndToEndScoring from the config directly
                }

                # Per-database overrides (Requirement 6.4): look up overrides for this session/database
                if ($singleDbConfig.endToEnd.databases) {
                    # Try matching by SessionName first, then by database name patterns
                    $dbOverride = $null
                    if ($singleDbConfig.endToEnd.databases.PSObject.Properties[$SessionName]) {
                        $dbOverride = $singleDbConfig.endToEnd.databases.$SessionName
                    }
                    # Also try finding a matching database name from the databases array
                    if (-not $dbOverride -and $singleDbConfig.databases) {
                        $matchingDb = $singleDbConfig.databases | Where-Object { $_.sessionName -eq $SessionName }
                        if ($matchingDb -and $singleDbConfig.endToEnd.databases.PSObject.Properties[$matchingDb.name]) {
                            $dbOverride = $singleDbConfig.endToEnd.databases.($matchingDb.name)
                        }
                    }

                    if ($dbOverride) {
                        if ($dbOverride.destinationDatabase) {
                            $e2eDbName = $dbOverride.destinationDatabase
                        }
                        if ($dbOverride.testScripts) {
                            $e2eTestScriptDir = $dbOverride.testScripts
                        }
                    }
                }
            }
        }
        catch {
            Write-Warning "Failed to read pipeline config for E2E settings: $($_.Exception.Message)"
        }
    }

    # Validate E2E configuration at startup (Requirement 6.5)
    if ($e2eEnabled) {
        $e2eConfigErrors = @()

        # Check destinationConnectionString is provided
        if (-not $e2eDestConnStr) {
            $e2eConfigErrors += "End-to-end mode enabled but no destination PostgreSQL connection string provided. Use -DestPgConnectionString parameter or set endToEnd.destinationConnectionString in config."
        }

        # Check maintenanceConnectionString - derive from destination if not provided
        if (-not $e2eMaintenanceConnStr) {
            if ($e2eDestConnStr) {
                # Derive maintenance connection from destination by targeting 'postgres' database
                # Parse the connection string and replace the Database component
                $derivedMaintConn = $e2eDestConnStr -replace 'Database=[^;]+', 'Database=postgres'
                if ($derivedMaintConn -eq $e2eDestConnStr) {
                    # If no Database= was found, append it
                    $derivedMaintConn = "$e2eDestConnStr;Database=postgres"
                }
                $e2eMaintenanceConnStr = $derivedMaintConn
                Write-Host "  Maintenance connection derived from destination (targeting 'postgres' database)"
            }
            else {
                $e2eConfigErrors += "No maintenanceConnectionString provided and cannot derive from missing destinationConnectionString."
            }
        }

        # Check pgPassthroughPath exists if functional tests are expected
        if ($e2eTestScriptDir -and (Test-Path $e2eTestScriptDir)) {
            # Test scripts exist, so PgPassthrough is needed
            if ($e2ePgPassthroughPath -and -not (Test-Path $e2ePgPassthroughPath)) {
                $e2eConfigErrors += "PgPassthrough path does not exist: '$e2ePgPassthroughPath'. Functional tests will not be able to run."
            }
        }

        # Validate scoring weights sum to approximately 1.0 if scoring section is present
        if ($singleDbConfig -and $singleDbConfig.endToEnd -and $singleDbConfig.endToEnd.scoring) {
            $scoring = $singleDbConfig.endToEnd.scoring
            $weightSum = 0
            if ($scoring.ddlWeight) { $weightSum += $scoring.ddlWeight }
            if ($scoring.dataWeight) { $weightSum += $scoring.dataWeight }
            if ($scoring.testWeight) { $weightSum += $scoring.testWeight }
            if ([Math]::Abs($weightSum - 1.0) -gt 0.01) {
                $e2eConfigErrors += "Scoring weights do not sum to 1.0 (actual: $weightSum). DDL=$($scoring.ddlWeight), Data=$($scoring.dataWeight), Test=$($scoring.testWeight)"
            }
        }

        # Report errors or proceed
        if ($e2eConfigErrors.Count -gt 0) {
            foreach ($err in $e2eConfigErrors) {
                Write-PipelineLog -Level "ERROR" -Step "e2e-config-validation" -Message $err -ElapsedSeconds 0
            }
            # If destination connection is missing, disable E2E entirely
            if (-not $e2eDestConnStr) {
                Write-Host "End-to-End mode: DISABLED (configuration errors - see above)" -ForegroundColor Yellow
                $e2eEnabled = $false
            }
            else {
                # Non-fatal warnings (e.g. PgPassthrough path) - continue with E2E but some steps may be skipped
                Write-Host "End-to-End mode: ENABLED (with warnings - see above)" -ForegroundColor Yellow
                Write-Host "  Destination: $($e2eDestConnStr.Substring(0, [Math]::Min(50, $e2eDestConnStr.Length)))..."
                Write-Host "  Max fix attempts: $e2eMaxAttempts"
                Write-Host ""
            }
        }
        else {
            Write-Host "End-to-End mode: ENABLED"
            Write-Host "  Destination: $($e2eDestConnStr.Substring(0, [Math]::Min(50, $e2eDestConnStr.Length)))..."
            Write-Host "  Max fix attempts: $e2eMaxAttempts"
            Write-Host ""
        }
    }

    # Resolve E2E database name (default to session name)
    if (-not $e2eDbName) {
        $e2eDbName = "${SessionName}_e2e"
    }

    # Resolve default PgPassthrough path
    if (-not $e2ePgPassthroughPath) {
        $e2ePgPassthroughPath = Join-Path $repoRoot "PgPassthrough\src\PgPassthrough.Server"
    }

    # Resolve test script directory
    if (-not $e2eTestScriptDir) {
        $e2eTestScriptDir = Join-Path $scriptDir "..\tests\functional\$SessionName"
    }

    $result = Invoke-SingleDatabasePipeline `
        -ConnString $ConnectionString `
        -Session $SessionName `
        -DbName $SessionName `
        -ValMode $ValidationMode `
        -PgConnString $PgConnectionString `
        -ChangedConfigTypes $changedConfigTypes `
        -EndToEndEnabled $e2eEnabled `
        -DestPgConnString $e2eDestConnStr `
        -E2eMaintenanceConnString $e2eMaintenanceConnStr `
        -E2eDatabaseName $e2eDbName `
        -E2eMaxFixAttempts $e2eMaxAttempts `
        -E2eTestScriptDir $e2eTestScriptDir `
        -E2ePgPassthroughPath $e2ePgPassthroughPath `
        -E2ePgPassthroughPort $e2ePgPassthroughPortVal `
        -E2eTimeoutPerScript $e2eTimeoutPerScript

    if (-not $result.success) {
        Write-Host ""
        Write-Host "PIPELINE FAILED at step: $($result.failedStep)" -ForegroundColor Red
        Write-Host "Total elapsed: $($result.elapsedSeconds) seconds"
        exit 1
    }

    # Wire modules: Validation outputs → Scoring → Diagnostics → Report Generation
    $scoringResult = Invoke-Scoring -ObjectResults $result.objectResults -PreviousScores $previousScores

    $failedObjects = @($result.objectResults | Where-Object {
        $_.status -eq "fail-syntax" -or $_.status -eq "fail-convert"
    })
    $diagnosticsResult = Invoke-DiagnosticsClassification -FailedObjects $failedObjects

    $configHashes = Get-ConfigFileHashes

    # Determine effective validation mode
    $effectiveValMode = $ValidationMode
    if (-not $effectiveValMode) {
        $modeFromResults = $result.objectResults | Where-Object { $_.validationMode } | Select-Object -First 1
        if ($modeFromResults) {
            $effectiveValMode = $modeFromResults.validationMode
        }
        else {
            $effectiveValMode = "syntax-only"
        }
    }

    # Find previous report path for delta computation
    $previousReportPath = $null
    $previousReportFiles = Get-ChildItem -Path $reportsDir -Filter "scoring-report-*.json" -File -ErrorAction SilentlyContinue |
        Sort-Object -Property LastWriteTime -Descending
    if ($previousReportFiles -and $previousReportFiles.Count -gt 0) {
        $previousReportPath = $previousReportFiles[0].FullName
    }

    # Generate and save report via Invoke-ReportGeneration
    $reportGenResult = Invoke-ReportGeneration `
        -ScoringResult $scoringResult `
        -DiagnosticsResult $diagnosticsResult `
        -ObjectResults $result.objectResults `
        -TotalElapsedSeconds $result.elapsedSeconds `
        -ValidationMode $effectiveValMode `
        -ConfigHashes $configHashes `
        -OutputDirectory $reportsDir `
        -PreviousReportPath $previousReportPath

    $report = $reportGenResult.Report
    $reportPath = $reportGenResult.ReportPath

    # Print summary
    Write-Host ""
    Write-Host "=== Pipeline Complete ==="
    Write-Host "Total elapsed: $($result.elapsedSeconds) seconds"
    Write-Host "Compatibility Score: $($report.aggregate.compatibilityScore)%"
    Write-Host "Pass: $($report.aggregate.totalPass) | Fail-Syntax: $($report.aggregate.totalFailSyntax) | Fail-Convert: $($report.aggregate.totalFailConvert) | Skip: $($report.aggregate.totalSkip)"
    if ($result.endToEndResults) {
        Write-Host "End-to-End Score: $($result.endToEndResults.endToEndScore)%"
        Write-Host "  DDL Rate: $($result.endToEndResults.ddlRate)% | Data Rate: $($result.endToEndResults.dataRate)% | Test Rate: $($result.endToEndResults.testRate)%"
        Write-Host "  Applied (first try): $($result.endToEndResults.appliedFirstTry) | Applied (after fix): $($result.endToEndResults.appliedAfterFix) | Unfixable: $($result.endToEndResults.unfixable)"
    }
    Write-Host "Report: $reportPath"

    exit 0
}
