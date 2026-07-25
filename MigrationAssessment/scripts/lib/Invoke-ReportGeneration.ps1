<#
.SYNOPSIS
    Generates a JSON Scoring Report for the Migration Validation Pipeline.

.DESCRIPTION
    Serializes pipeline results into a structured JSON Scoring Report matching
    the schema defined in the design document. Includes reportId (UUID),
    timestamp (ISO-8601), per-database results with scores and breakdowns,
    aggregate scores with delta from previous run, diagnostics section, and
    optional end-to-end validation results (DDL application, fix loop, data
    migration, functional tests, and composite End_To_End_Score).
    Supports loading a previous report for delta computation of both
    Compatibility Score and End_To_End_Score.

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

.PARAMETER EndToEndResults
    Optional hashtable containing end-to-end validation results with keys:
      - scoring: output from Invoke-EndToEndScoring (endToEndScore, ddlRate, dataRate, testRate, appliedFirstTry, appliedAfterFix, unfixable)
      - ddlResults: array of DDL application results (objectName, status, errorMessage, elapsedMs)
      - fixResults: array of fix loop results (objectName, finalStatus, attempts, fixedDdl, explanation, errors)
      - dataMigrationResults: hashtable (tablesSucceeded, tablesFailed, totalRows, elapsed)
      - functionalTestResults: hashtable (total, passed, failed, results[])
      - timing: hashtable (applyElapsed, fixLoopElapsed, dataMigrationElapsed, functionalTestElapsed, totalEndToEndElapsed)

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

.EXAMPLE
    $report = Invoke-ReportGeneration `
        -ScoringResult $scoring `
        -DiagnosticsResult $diagnostics `
        -ObjectResults $objects `
        -TotalElapsedSeconds 245.8 `
        -ValidationMode "live-instance" `
        -ConfigHashes @{ "type-mappings.json" = "sha256-abc" } `
        -EndToEndResults @{
            scoring = $e2eScoring
            ddlResults = $ddlResults
            fixResults = $fixResults
            dataMigrationResults = $dataMigration
            functionalTestResults = $functionalTests
            timing = @{ applyElapsed = 8.2; fixLoopElapsed = 45.1; dataMigrationElapsed = 12.3; functionalTestElapsed = 6.7; totalEndToEndElapsed = 72.3 }
        }

.NOTES
    Requirements: 2.4, 3.1, 3.3, 3.5, 4.1, 4.5, 5.2, 5.3, 5.5, 7.1, 7.2, 7.3, 7.4
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

        [hashtable]$EndToEndResults,

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
    $previousEndToEndScore = $null
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
        # Extract previous End_To_End_Score for delta comparison (Requirement 7.2)
        if ($previousReport.endToEnd -and $null -ne $previousReport.endToEnd.endToEndScore) {
            $previousEndToEndScore = $previousReport.endToEnd.endToEndScore
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

    # Build end-to-end section if results are provided (Requirements 7.1, 7.2, 7.3, 7.4)
    $endToEndSection = $null
    if ($null -ne $EndToEndResults) {
        $e2eScoring = $EndToEndResults.scoring
        $e2eDdlResults = $EndToEndResults.ddlResults
        $e2eFixResults = $EndToEndResults.fixResults
        $e2eDataMigration = $EndToEndResults.dataMigrationResults
        $e2eFunctionalTests = $EndToEndResults.functionalTestResults
        $e2eTiming = $EndToEndResults.timing

        # Compute current E2E score
        $currentE2EScore = if ($e2eScoring -and $null -ne $e2eScoring.endToEndScore) { $e2eScoring.endToEndScore } else { $null }

        # Compute E2E delta from previous report (Requirement 7.2)
        $e2eDelta = $null
        if ($null -ne $currentE2EScore -and $null -ne $previousEndToEndScore) {
            $e2eDelta = [Math]::Round($currentE2EScore - $previousEndToEndScore, 1)
        }

        # Build DDL application subsection (Requirement 5.5)
        $ddlApplicationSection = $null
        if ($null -ne $e2eDdlResults) {
            $totalDdl = @($e2eDdlResults).Count
            $appliedFirstTry = if ($e2eScoring) { $e2eScoring.appliedFirstTry } else { 0 }
            $appliedAfterFix = if ($e2eScoring) { $e2eScoring.appliedAfterFix } else { 0 }
            $unfixable = if ($e2eScoring) { $e2eScoring.unfixable } else { 0 }
            $ddlRate = if ($e2eScoring -and $null -ne $e2eScoring.ddlRate) { [Math]::Round($e2eScoring.ddlRate * 100, 1) } else { 0.0 }

            $ddlApplicationSection = [ordered]@{
                total           = $totalDdl
                appliedFirstTry = $appliedFirstTry
                appliedAfterFix = $appliedAfterFix
                unfixable       = $unfixable
                rate            = $ddlRate
            }
        }

        # Build fix loop subsection with per-object details (Requirements 7.3, 7.4)
        $fixLoopSection = $null
        if ($null -ne $e2eFixResults) {
            $fixObjects = [System.Collections.ArrayList]::new()
            $totalAttempted = @($e2eFixResults).Count
            $totalFixed = 0
            $totalAttempts = 0

            foreach ($fixObj in $e2eFixResults) {
                $attempts = if ($null -ne $fixObj.attempts) { $fixObj.attempts } else { 0 }
                $totalAttempts += $attempts
                if ($fixObj.finalStatus -eq 'fixed') { $totalFixed++ }

                $fixEntry = [ordered]@{
                    name         = $fixObj.objectName
                    attempts     = $attempts
                    finalStatus  = $fixObj.finalStatus
                    explanation  = if ($fixObj.explanation) { $fixObj.explanation } else { $null }
                    originalError = if ($fixObj.errors -and $fixObj.errors.Count -gt 0) { $fixObj.errors[0] } else { $null }
                    finalDdl     = if ($fixObj.fixedDdl) { $fixObj.fixedDdl } else { $null }
                }
                [void]$fixObjects.Add([PSCustomObject]$fixEntry)
            }

            $averageAttempts = if ($totalAttempted -gt 0) { [Math]::Round($totalAttempts / $totalAttempted, 1) } else { 0.0 }

            $fixLoopSection = [ordered]@{
                totalAttempted  = $totalAttempted
                totalFixed      = $totalFixed
                averageAttempts = $averageAttempts
                objects         = @($fixObjects)
            }
        }

        # Build data migration subsection
        $dataMigrationSection = $null
        if ($null -ne $e2eDataMigration) {
            $tablesTotal = ($e2eDataMigration.tablesSucceeded + $e2eDataMigration.tablesFailed)
            $dataRate = if ($e2eScoring -and $null -ne $e2eScoring.dataRate) { [Math]::Round($e2eScoring.dataRate * 100, 1) } else { 0.0 }

            $dataMigrationSection = [ordered]@{
                tablesTotal     = $tablesTotal
                tablesSucceeded = $e2eDataMigration.tablesSucceeded
                tablesFailed    = $e2eDataMigration.tablesFailed
                totalRows       = if ($null -ne $e2eDataMigration.totalRows) { $e2eDataMigration.totalRows } else { 0 }
                rate            = $dataRate
                elapsed         = if ($null -ne $e2eDataMigration.elapsed) { $e2eDataMigration.elapsed } else { 0.0 }
            }
        }

        # Build functional tests subsection
        $functionalTestsSection = $null
        if ($null -ne $e2eFunctionalTests) {
            $testResults = [System.Collections.ArrayList]::new()
            if ($e2eFunctionalTests.results) {
                foreach ($testResult in $e2eFunctionalTests.results) {
                    [void]$testResults.Add([PSCustomObject][ordered]@{
                        script       = if ($testResult.scriptName) { $testResult.scriptName } else { $testResult.script }
                        test         = if ($testResult.testName) { $testResult.testName } else { $testResult.test }
                        status       = $testResult.status
                        errorMessage = if ($testResult.errorMessage) { $testResult.errorMessage } else { $null }
                    })
                }
            }

            $testRate = if ($e2eScoring -and $null -ne $e2eScoring.testRate) { [Math]::Round($e2eScoring.testRate * 100, 1) } else { 0.0 }

            $functionalTestsSection = [ordered]@{
                total   = if ($null -ne $e2eFunctionalTests.total) { $e2eFunctionalTests.total } else { 0 }
                passed  = if ($null -ne $e2eFunctionalTests.passed) { $e2eFunctionalTests.passed } else { 0 }
                failed  = if ($null -ne $e2eFunctionalTests.failed) { $e2eFunctionalTests.failed } else { 0 }
                rate    = $testRate
                results = @($testResults)
            }
        }

        # Build timing subsection (Requirement 7.4)
        $timingSection = $null
        if ($null -ne $e2eTiming) {
            $timingSection = [ordered]@{
                applyElapsed          = if ($null -ne $e2eTiming.applyElapsed) { $e2eTiming.applyElapsed } else { 0.0 }
                fixLoopElapsed        = if ($null -ne $e2eTiming.fixLoopElapsed) { $e2eTiming.fixLoopElapsed } else { 0.0 }
                dataMigrationElapsed  = if ($null -ne $e2eTiming.dataMigrationElapsed) { $e2eTiming.dataMigrationElapsed } else { 0.0 }
                functionalTestElapsed = if ($null -ne $e2eTiming.functionalTestElapsed) { $e2eTiming.functionalTestElapsed } else { 0.0 }
                totalEndToEndElapsed  = if ($null -ne $e2eTiming.totalEndToEndElapsed) { $e2eTiming.totalEndToEndElapsed } else { 0.0 }
            }
        }

        # Assemble the endToEnd section
        $endToEndSection = [PSCustomObject][ordered]@{
            enabled               = $true
            endToEndScore         = $currentE2EScore
            previousEndToEndScore = $previousEndToEndScore
            endToEndDelta         = $e2eDelta
            ddlApplication        = if ($ddlApplicationSection) { [PSCustomObject]$ddlApplicationSection } else { $null }
            fixLoop               = if ($fixLoopSection) { [PSCustomObject]$fixLoopSection } else { $null }
            dataMigration         = if ($dataMigrationSection) { [PSCustomObject]$dataMigrationSection } else { $null }
            functionalTests       = if ($functionalTestsSection) { [PSCustomObject]$functionalTestsSection } else { $null }
            timing                = if ($timingSection) { [PSCustomObject]$timingSection } else { $null }
        }

        Write-Verbose "End-to-end section added to report. E2E Score: $currentE2EScore"
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
        endToEnd            = $endToEndSection
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
