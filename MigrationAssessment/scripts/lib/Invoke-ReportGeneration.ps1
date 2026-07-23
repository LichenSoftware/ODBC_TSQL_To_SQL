<#
.SYNOPSIS
    Generates a JSON Scoring Report for the Migration Validation Pipeline.

.DESCRIPTION
    Serializes pipeline results into a structured JSON Scoring Report matching
    the schema defined in the design document. Includes reportId (UUID),
    timestamp (ISO-8601), per-database results with scores and breakdowns,
    aggregate scores with delta from previous run, and diagnostics section.
    Supports loading a previous report for delta computation.

.PARAMETER ScoringResult
    Output from Invoke-Scoring. Hashtable with:
      - databases: per-database scoring results
      - aggregate: aggregate score info
      - topFailingTypes: array of top failing types

.PARAMETER DiagnosticsResult
    Output from Invoke-DiagnosticsClassification. Array of category objects with:
      - category, count, objects, details

.PARAMETER ObjectResults
    Array of per-object validation results. Each element should have:
      - objectName, objectType, databaseName, sessionName, status
      - errorMessage, errorLineNumber, generatedDdl (for failures)

.PARAMETER TotalElapsedSeconds
    Total elapsed time for the pipeline run in seconds.

.PARAMETER ValidationMode
    The validation mode used: "live-instance" or "syntax-only".

.PARAMETER ConfigHashes
    Hashtable of config file hashes (SHA-256). Keys are filenames, values are hash strings.

.PARAMETER OutputDirectory
    Directory where the report JSON will be saved. Default: "./pipeline-reports/"

.PARAMETER PreviousReportPath
    Optional path to a previous report JSON file for loading previous scores
    to compute deltas.

.OUTPUTS
    PSCustomObject representing the full report structure. The report is also
    saved to the OutputDirectory as a JSON file.

.EXAMPLE
    $report = Invoke-ReportGeneration `
        -ScoringResult $scoring `
        -DiagnosticsResult $diagnostics `
        -ObjectResults $objects `
        -TotalElapsedSeconds 145.3 `
        -ValidationMode "live-instance" `
        -ConfigHashes @{ "type-mappings.json" = "sha256-abc" }

.NOTES
    Requirements: 2.4, 3.1, 3.3, 3.5, 4.1, 4.5
#>
function Invoke-ReportGeneration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$ScoringResult,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [array]$DiagnosticsResult,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [array]$ObjectResults,

        [Parameter(Mandatory)]
        [double]$TotalElapsedSeconds,

        [Parameter(Mandatory)]
        [ValidateSet('live-instance', 'syntax-only')]
        [string]$ValidationMode,

        [Parameter(Mandatory)]
        [hashtable]$ConfigHashes,

        [string]$OutputDirectory = './pipeline-reports/',

        [string]$PreviousReportPath
    )

    # Load previous report for delta computation if provided
    $previousReport = $null
    if ($PreviousReportPath -and (Test-Path -Path $PreviousReportPath)) {
        try {
            $previousReport = Get-Content -Path $PreviousReportPath -Raw | ConvertFrom-Json
            Write-Verbose "Loaded previous report from: $PreviousReportPath"
        }
        catch {
            Write-Warning "Failed to load previous report from '$PreviousReportPath': $_"
        }
    }

    # Extract previous scores from loaded report for delta computation
    $previousScores = @{}
    if ($null -ne $previousReport) {
        if ($previousReport.databases) {
            foreach ($db in $previousReport.databases) {
                if ($db.name -and $null -ne $db.score -and $null -ne $db.score.compatibilityScore) {
                    $previousScores[$db.name] = $db.score.compatibilityScore
                }
            }
        }
        if ($previousReport.aggregate -and $null -ne $previousReport.aggregate.compatibilityScore) {
            $previousScores['__aggregate__'] = $previousReport.aggregate.compatibilityScore
        }
    }

    # Generate report metadata
    $reportId = [System.Guid]::NewGuid().ToString()
    $timestamp = [System.DateTimeOffset]::UtcNow.ToString('o')

    # Build per-database results
    $databaseEntries = [System.Collections.ArrayList]::new()

    # Group object results by database
    $objectsByDatabase = @{}
    foreach ($obj in $ObjectResults) {
        $dbName = $obj.databaseName
        if (-not $objectsByDatabase.ContainsKey($dbName)) {
            $objectsByDatabase[$dbName] = [System.Collections.ArrayList]::new()
        }
        [void]$objectsByDatabase[$dbName].Add($obj)
    }

    foreach ($dbName in $objectsByDatabase.Keys) {
        $dbObjects = $objectsByDatabase[$dbName]
        $dbScoring = $null
        if ($ScoringResult.databases -and $ScoringResult.databases.ContainsKey($dbName)) {
            $dbScoring = $ScoringResult.databases[$dbName]
        }

        # Determine session name from the first object in this database
        $sessionName = $null
        foreach ($obj in $dbObjects) {
            if ($obj.sessionName) {
                $sessionName = $obj.sessionName
                break
            }
        }

        # Compute elapsed seconds per database if available, otherwise null
        $dbElapsedSeconds = $null
        foreach ($obj in $dbObjects) {
            if ($null -ne $obj.elapsedSeconds) {
                $dbElapsedSeconds = $obj.elapsedSeconds
                break
            }
        }

        # Build score section
        $compatibilityScore = if ($dbScoring) { $dbScoring.compatibilityScore } else { 'N/A' }
        $pass = if ($dbScoring) { $dbScoring.pass } else { 0 }
        $failSyntax = if ($dbScoring) { $dbScoring.failSyntax } else { 0 }
        $failConvert = if ($dbScoring) { $dbScoring.failConvert } else { 0 }
        $skip = if ($dbScoring) { $dbScoring.skip } else { 0 }

        # Delta from previous run
        $previousScore = $null
        $delta = $null
        if ($dbScoring -and $null -ne $dbScoring.previousScore) {
            $previousScore = $dbScoring.previousScore
            $delta = $dbScoring.delta
        }
        elseif ($previousScores.ContainsKey($dbName)) {
            $previousScore = $previousScores[$dbName]
            if ($compatibilityScore -ne 'N/A' -and $null -ne $previousScore -and $previousScore -ne 'N/A') {
                $delta = [Math]::Round($compatibilityScore - $previousScore, 1)
            }
        }

        $scoreSection = [ordered]@{
            compatibilityScore = $compatibilityScore
            previousScore      = $previousScore
            delta              = $delta
            pass               = $pass
            failSyntax         = $failSyntax
            failConvert        = $failConvert
            skip               = $skip
        }

        # Build byType section
        $byTypeSection = [ordered]@{}
        if ($dbScoring -and $dbScoring.byType) {
            foreach ($typeName in $dbScoring.byType.Keys) {
                $typeData = $dbScoring.byType[$typeName]
                $byTypeSection[$typeName] = [ordered]@{
                    pass  = $typeData.pass
                    fail  = $typeData.fail
                    score = $typeData.score
                }
            }
        }

        # Build per-object details
        $objectEntries = [System.Collections.ArrayList]::new()
        foreach ($obj in $dbObjects) {
            $objectEntry = [ordered]@{
                name   = $obj.objectName
                type   = $obj.objectType
                status = $obj.status
            }
            if ($obj.status -eq 'fail-syntax' -or $obj.status -eq 'fail-convert') {
                $objectEntry['errorMessage'] = if ($obj.errorMessage) { $obj.errorMessage } else { $null }
                $objectEntry['errorLineNumber'] = if ($null -ne $obj.errorLineNumber) { $obj.errorLineNumber } else { $null }
                $objectEntry['generatedDdl'] = if ($obj.generatedDdl) { $obj.generatedDdl } else { $null }
            }
            [void]$objectEntries.Add([PSCustomObject]$objectEntry)
        }

        $dbEntry = [ordered]@{
            name           = $dbName
            sessionName    = $sessionName
            objectCount    = $dbObjects.Count
            elapsedSeconds = $dbElapsedSeconds
            score          = [PSCustomObject]$scoreSection
            byType         = [PSCustomObject]$byTypeSection
            objects        = @($objectEntries)
        }

        [void]$databaseEntries.Add([PSCustomObject]$dbEntry)
    }

    # Build aggregate section
    $aggregateScoring = $ScoringResult.aggregate
    $aggregatePreviousScore = $null
    $aggregateDelta = $null
    if ($aggregateScoring) {
        $aggregatePreviousScore = $aggregateScoring.previousScore
        $aggregateDelta = $aggregateScoring.delta

        # If no previous score from scoring result, try from loaded previous report
        if ($null -eq $aggregatePreviousScore -and $previousScores.ContainsKey('__aggregate__')) {
            $aggregatePreviousScore = $previousScores['__aggregate__']
            if ($aggregateScoring.compatibilityScore -ne 'N/A' -and $null -ne $aggregatePreviousScore -and $aggregatePreviousScore -ne 'N/A') {
                $aggregateDelta = [Math]::Round($aggregateScoring.compatibilityScore - $aggregatePreviousScore, 1)
            }
        }
    }

    $aggregateSection = [ordered]@{
        compatibilityScore = if ($aggregateScoring) { $aggregateScoring.compatibilityScore } else { 'N/A' }
        previousScore      = $aggregatePreviousScore
        delta              = $aggregateDelta
        totalPass          = if ($aggregateScoring) { $aggregateScoring.totalPass } else { 0 }
        totalFailSyntax    = if ($aggregateScoring) { $aggregateScoring.totalFailSyntax } else { 0 }
        totalFailConvert   = if ($aggregateScoring) { $aggregateScoring.totalFailConvert } else { 0 }
        totalSkip          = if ($aggregateScoring) { $aggregateScoring.totalSkip } else { 0 }
    }

    # Build diagnostics section
    $rootCauseCategories = [System.Collections.ArrayList]::new()
    foreach ($category in $DiagnosticsResult) {
        [void]$rootCauseCategories.Add([PSCustomObject][ordered]@{
            category = $category.category
            count    = $category.count
            objects  = @($category.objects)
        })
    }

    $topFailingTypes = [System.Collections.ArrayList]::new()
    if ($ScoringResult.topFailingTypes) {
        foreach ($entry in $ScoringResult.topFailingTypes) {
            [void]$topFailingTypes.Add([PSCustomObject][ordered]@{
                type      = $entry.type
                failCount = $entry.failCount
            })
        }
    }

    $diagnosticsSection = [ordered]@{
        rootCauseCategories = @($rootCauseCategories)
        topFailingTypes     = @($topFailingTypes)
    }

    # Assemble the full report
    $report = [PSCustomObject][ordered]@{
        reportId            = $reportId
        timestamp           = $timestamp
        totalElapsedSeconds = $TotalElapsedSeconds
        validationMode      = $ValidationMode
        configHashes        = [PSCustomObject]$ConfigHashes
        databases           = @($databaseEntries)
        aggregate           = [PSCustomObject]$aggregateSection
        diagnostics         = [PSCustomObject]$diagnosticsSection
    }

    # Ensure output directory exists
    if (-not (Test-Path -Path $OutputDirectory)) {
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
        Write-Verbose "Created output directory: $OutputDirectory"
    }

    # Generate filename with timestamp for uniqueness
    $fileTimestamp = [System.DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $reportFileName = "scoring-report-$fileTimestamp.json"
    $reportPath = Join-Path -Path $OutputDirectory -ChildPath $reportFileName

    # Serialize to JSON and save
    $jsonContent = $report | ConvertTo-Json -Depth 20
    Set-Content -Path $reportPath -Value $jsonContent -Encoding UTF8

    Write-Verbose "Scoring Report saved to: $reportPath"
    Write-Host "Scoring Report generated: $reportPath"

    # Return the report object and path
    return @{
        Report     = $report
        ReportPath = $reportPath
    }
}
