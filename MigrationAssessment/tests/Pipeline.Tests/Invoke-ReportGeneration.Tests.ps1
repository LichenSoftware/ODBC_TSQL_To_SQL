#Requires -Module Pester

<#
.SYNOPSIS
    Pester unit tests for Invoke-ReportGeneration.

.DESCRIPTION
    Validates that the Report Generator produces correct JSON structure,
    generates valid UUIDs and ISO-8601 timestamps, includes per-database entries,
    computes aggregate scores, includes diagnostics, saves output files, and
    uses previous report data for delta computation.

    Requirements validated: 2.4, 4.5
#>

BeforeAll {
    # Dot-source the modules under test
    . "$PSScriptRoot/../../scripts/lib/Invoke-Scoring.ps1"
    . "$PSScriptRoot/../../scripts/lib/Invoke-DiagnosticsClassification.ps1"
    . "$PSScriptRoot/../../scripts/lib/Invoke-ReportGeneration.ps1"
}

Describe 'Invoke-ReportGeneration' {

    BeforeAll {
        # Common test data for multiple tests
        $script:objectResults = @(
            @{ objectName = 'dbo.Users'; objectType = 'Table'; databaseName = 'TestDB'; sessionName = 'session1'; status = 'pass' }
            @{ objectName = 'dbo.Orders'; objectType = 'Table'; databaseName = 'TestDB'; sessionName = 'session1'; status = 'pass' }
            @{ objectName = 'dbo.sp_GetUsers'; objectType = 'StoredProcedure'; databaseName = 'TestDB'; sessionName = 'session1'; status = 'fail-syntax'; errorMessage = 'type "hierarchyid" does not exist'; errorLineNumber = 5; generatedDdl = 'CREATE TABLE users (id hierarchyid)' }
            @{ objectName = 'dbo.vw_Summary'; objectType = 'View'; databaseName = 'TestDB'; sessionName = 'session1'; status = 'pass' }
            @{ objectName = 'dbo.fn_Calc'; objectType = 'Function'; databaseName = 'TestDB'; sessionName = 'session1'; status = 'fail-convert'; errorMessage = 'conversion failed'; errorLineNumber = 1; generatedDdl = '' }
            @{ objectName = 'dbo.syn_Active'; objectType = 'Synonym'; databaseName = 'TestDB'; sessionName = 'session1'; status = 'skip' }
        )

        $script:scoringResult = Invoke-Scoring -ObjectResults $script:objectResults -PreviousScores @{}
        $script:diagnosticsResult = Invoke-DiagnosticsClassification -FailedObjects ($script:objectResults | Where-Object { $_.status -eq 'fail-syntax' -or $_.status -eq 'fail-convert' })

        $script:configHashes = @{
            'type-mappings.json'     = 'sha256-abc123'
            'function-mappings.json' = 'sha256-def456'
        }

        # Use a temp directory for output
        $script:testOutputDir = Join-Path $TestDrive 'pipeline-reports'
    }

    Context 'JSON structure has required top-level fields' {

        BeforeAll {
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 42.5 `
                -ValidationMode 'live-instance' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory $script:testOutputDir
            $script:report = $script:result.Report
        }

        It 'Should have reportId field' {
            $script:report.reportId | Should -Not -BeNullOrEmpty
        }

        It 'Should have timestamp field' {
            $script:report.timestamp | Should -Not -BeNullOrEmpty
        }

        It 'Should have totalElapsedSeconds field' {
            $script:report.totalElapsedSeconds | Should -Be 42.5
        }

        It 'Should have validationMode field' {
            $script:report.validationMode | Should -Be 'live-instance'
        }

        It 'Should have configHashes field' {
            $script:report.configHashes | Should -Not -BeNull
        }

        It 'Should have databases field as array' {
            $script:report.databases | Should -Not -BeNull
            $script:report.databases.Count | Should -BeGreaterOrEqual 1
        }

        It 'Should have aggregate field' {
            $script:report.aggregate | Should -Not -BeNull
        }

        It 'Should have diagnostics field' {
            $script:report.diagnostics | Should -Not -BeNull
        }
    }

    Context 'reportId is a valid UUID' {

        BeforeAll {
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 10.0 `
                -ValidationMode 'syntax-only' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory (Join-Path $TestDrive 'uuid-test')
            $script:report = $script:result.Report
        }

        It 'Should match UUID format (8-4-4-4-12 hex pattern)' {
            $script:report.reportId | Should -Match '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        }

        It 'Should be parseable as a GUID' {
            { [System.Guid]::Parse($script:report.reportId) } | Should -Not -Throw
        }
    }

    Context 'timestamp is valid ISO-8601 format' {

        BeforeAll {
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 10.0 `
                -ValidationMode 'syntax-only' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory (Join-Path $TestDrive 'timestamp-test')
            $script:report = $script:result.Report
        }

        It 'Should match ISO-8601 pattern with timezone' {
            # Matches patterns like: 2024-01-15T10:30:45.1234567+00:00
            $script:report.timestamp | Should -Match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}'
        }

        It 'Should be parseable as a DateTimeOffset' {
            { [System.DateTimeOffset]::Parse($script:report.timestamp) } | Should -Not -Throw
        }
    }

    Context 'databases array contains per-database entries' {

        BeforeAll {
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 30.0 `
                -ValidationMode 'live-instance' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory (Join-Path $TestDrive 'db-entries-test')
            $script:report = $script:result.Report
            $script:dbEntry = $script:report.databases[0]
        }

        It 'Should have a name field' {
            $script:dbEntry.name | Should -Be 'TestDB'
        }

        It 'Should have a score section with compatibilityScore' {
            $script:dbEntry.score.compatibilityScore | Should -Not -BeNull
        }

        It 'Should have a byType section' {
            $script:dbEntry.byType | Should -Not -BeNull
        }

        It 'Should have an objects array' {
            $script:dbEntry.objects | Should -Not -BeNull
            $script:dbEntry.objects.Count | Should -BeGreaterOrEqual 1
        }

        It 'Should include object entries with name, type, and status' {
            $firstObj = $script:dbEntry.objects[0]
            $firstObj.name | Should -Not -BeNullOrEmpty
            $firstObj.type | Should -Not -BeNullOrEmpty
            $firstObj.status | Should -Not -BeNullOrEmpty
        }
    }

    Context 'aggregate section has required fields' {

        BeforeAll {
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 20.0 `
                -ValidationMode 'live-instance' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory (Join-Path $TestDrive 'aggregate-test')
            $script:report = $script:result.Report
            $script:agg = $script:report.aggregate
        }

        It 'Should have compatibilityScore' {
            $script:agg.compatibilityScore | Should -Not -BeNull
        }

        It 'Should have totalPass' {
            $script:agg.totalPass | Should -BeOfType [int]
        }

        It 'Should have totalFailSyntax' {
            $script:agg.totalFailSyntax | Should -BeOfType [int]
        }

        It 'Should have totalFailConvert' {
            $script:agg.totalFailConvert | Should -BeOfType [int]
        }

        It 'Should have totalSkip' {
            $script:agg.totalSkip | Should -BeOfType [int]
        }

        It 'Should compute correct aggregate score' {
            # 3 pass, 1 fail-syntax, 1 fail-convert = 3/5 * 100 = 60.0%
            $script:agg.compatibilityScore | Should -Be 60.0
        }
    }

    Context 'diagnostics section has rootCauseCategories and topFailingTypes' {

        BeforeAll {
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 15.0 `
                -ValidationMode 'live-instance' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory (Join-Path $TestDrive 'diag-test')
            $script:report = $script:result.Report
            $script:diag = $script:report.diagnostics
        }

        It 'Should have rootCauseCategories as an array' {
            $script:diag.rootCauseCategories | Should -Not -BeNull
            , $script:diag.rootCauseCategories | Should -BeOfType [array]
        }

        It 'Should have topFailingTypes as an array' {
            $script:diag.topFailingTypes | Should -Not -BeNull
            , $script:diag.topFailingTypes | Should -BeOfType [array]
        }

        It 'Should include category entries with category, count, objects fields' {
            if ($script:diag.rootCauseCategories.Count -gt 0) {
                $firstCat = $script:diag.rootCauseCategories[0]
                $firstCat.category | Should -Not -BeNullOrEmpty
                $firstCat.count | Should -BeGreaterThan 0
                $firstCat.objects | Should -Not -BeNull
            }
        }
    }

    Context 'Report is saved to output directory as JSON file' {

        BeforeAll {
            $script:outputDir = Join-Path $TestDrive 'save-test'
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 5.0 `
                -ValidationMode 'syntax-only' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory $script:outputDir
        }

        It 'Should create the output directory' {
            Test-Path $script:outputDir | Should -BeTrue
        }

        It 'Should save a JSON file in the output directory' {
            $script:result.ReportPath | Should -Not -BeNullOrEmpty
            Test-Path $script:result.ReportPath | Should -BeTrue
        }

        It 'Should produce a file with valid JSON content' {
            $content = Get-Content -Path $script:result.ReportPath -Raw
            { $content | ConvertFrom-Json } | Should -Not -Throw
        }

        It 'Should have scoring-report prefix in filename' {
            $fileName = [System.IO.Path]::GetFileName($script:result.ReportPath)
            $fileName | Should -Match '^scoring-report-\d{8}-\d{6}\.json$'
        }
    }

    Context 'Previous report data is used for delta computation' {

        BeforeAll {
            # Create a "previous" report JSON with a known score
            $script:prevReportDir = Join-Path $TestDrive 'prev-reports'
            New-Item -ItemType Directory -Path $script:prevReportDir -Force | Out-Null

            $previousReport = @{
                reportId            = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
                timestamp           = '2024-01-01T00:00:00.0000000+00:00'
                totalElapsedSeconds = 30.0
                validationMode      = 'live-instance'
                configHashes        = @{ 'type-mappings.json' = 'old-hash' }
                databases           = @(
                    @{
                        name  = 'TestDB'
                        score = @{
                            compatibilityScore = 40.0
                            pass               = 2
                            failSyntax         = 2
                            failConvert        = 1
                            skip               = 0
                        }
                    }
                )
                aggregate = @{
                    compatibilityScore = 40.0
                    totalPass          = 2
                    totalFailSyntax    = 2
                    totalFailConvert   = 1
                    totalSkip          = 0
                }
                diagnostics = @{
                    rootCauseCategories = @()
                    topFailingTypes     = @()
                }
            }

            $prevReportPath = Join-Path $script:prevReportDir 'previous-report.json'
            $previousReport | ConvertTo-Json -Depth 10 | Set-Content -Path $prevReportPath -Encoding UTF8

            $script:deltaOutputDir = Join-Path $TestDrive 'delta-test'
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 25.0 `
                -ValidationMode 'live-instance' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory $script:deltaOutputDir `
                -PreviousReportPath $prevReportPath
            $script:report = $script:result.Report
            $script:dbEntry = $script:report.databases[0]
        }

        It 'Should include previousScore in per-database score section' {
            $script:dbEntry.score.previousScore | Should -Be 40.0
        }

        It 'Should compute correct delta (current - previous)' {
            # Current score = 60.0, previous = 40.0 → delta = 20.0
            $script:dbEntry.score.delta | Should -Be 20.0
        }

        It 'Should include previousScore in aggregate section' {
            $script:report.aggregate.previousScore | Should -Be 40.0
        }

        It 'Should compute correct aggregate delta' {
            # Current aggregate = 60.0, previous = 40.0 → delta = 20.0
            $script:report.aggregate.delta | Should -Be 20.0
        }
    }

    Context 'Config hashes are included in report' {

        BeforeAll {
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 5.0 `
                -ValidationMode 'live-instance' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory (Join-Path $TestDrive 'hash-test')
            $script:report = $script:result.Report
        }

        It 'Should include type-mappings hash' {
            $script:report.configHashes.'type-mappings.json' | Should -Be 'sha256-abc123'
        }

        It 'Should include function-mappings hash' {
            $script:report.configHashes.'function-mappings.json' | Should -Be 'sha256-def456'
        }
    }

    Context 'End-to-end section is null when no EndToEndResults provided' {

        BeforeAll {
            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 10.0 `
                -ValidationMode 'live-instance' `
                -ConfigHashes $script:configHashes `
                -OutputDirectory (Join-Path $TestDrive 'no-e2e-test')
            $script:report = $script:result.Report
        }

        It 'Should have endToEnd field as null' {
            $script:report.endToEnd | Should -BeNull
        }
    }

    Context 'End-to-end section is populated when EndToEndResults provided' {

        BeforeAll {
            $script:e2eResults = @{
                scoring = @{
                    endToEndScore  = 85.2
                    ddlRate        = 0.882
                    dataRate       = 0.875
                    testRate       = 0.85
                    appliedFirstTry = 11
                    appliedAfterFix = 4
                    unfixable       = 2
                }
                ddlResults = @(
                    @{ objectName = 'dbo.Users'; status = 'applied'; errorMessage = $null; elapsedMs = 50 }
                    @{ objectName = 'dbo.Orders'; status = 'applied'; errorMessage = $null; elapsedMs = 45 }
                    @{ objectName = 'dbo.sp_GetUsers'; status = 'failed'; errorMessage = 'type error'; elapsedMs = 10 }
                )
                fixResults = @(
                    @{
                        objectName  = 'dbo.sp_GetUsers'
                        finalStatus = 'fixed'
                        attempts    = 2
                        fixedDdl    = 'CREATE OR REPLACE FUNCTION sp_getusers() ...'
                        explanation = 'Changed hierarchyid to uuid'
                        errors      = @('type "hierarchyid" does not exist', 'syntax error near uuid')
                    }
                )
                dataMigrationResults = @{
                    tablesSucceeded = 7
                    tablesFailed    = 1
                    totalRows       = 15420
                    elapsed         = 12.3
                }
                functionalTestResults = @{
                    total   = 20
                    passed  = 17
                    failed  = 3
                    results = @(
                        @{ scriptName = 'basic-queries.sql'; testName = 'Select all departments'; status = 'pass'; errorMessage = $null }
                        @{ scriptName = 'proc-tests.sql'; testName = 'Call sp_GetUsers'; status = 'fail'; errorMessage = 'timeout exceeded' }
                    )
                }
                timing = @{
                    applyElapsed          = 8.2
                    fixLoopElapsed        = 45.1
                    dataMigrationElapsed  = 12.3
                    functionalTestElapsed = 6.7
                    totalEndToEndElapsed  = 72.3
                }
            }

            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 120.0 `
                -ValidationMode 'live-instance' `
                -ConfigHashes $script:configHashes `
                -EndToEndResults $script:e2eResults `
                -OutputDirectory (Join-Path $TestDrive 'e2e-test')
            $script:report = $script:result.Report
            $script:e2e = $script:report.endToEnd
        }

        It 'Should have endToEnd section not null' {
            $script:e2e | Should -Not -BeNull
        }

        It 'Should have enabled set to true' {
            $script:e2e.enabled | Should -BeTrue
        }

        It 'Should have endToEndScore of 85.2' {
            $script:e2e.endToEndScore | Should -Be 85.2
        }

        It 'Should have previousEndToEndScore as null when no previous report' {
            $script:e2e.previousEndToEndScore | Should -BeNull
        }

        It 'Should have endToEndDelta as null when no previous report' {
            $script:e2e.endToEndDelta | Should -BeNull
        }

        It 'Should have ddlApplication section with correct total' {
            $script:e2e.ddlApplication.total | Should -Be 3
        }

        It 'Should have ddlApplication.appliedFirstTry' {
            $script:e2e.ddlApplication.appliedFirstTry | Should -Be 11
        }

        It 'Should have ddlApplication.appliedAfterFix' {
            $script:e2e.ddlApplication.appliedAfterFix | Should -Be 4
        }

        It 'Should have ddlApplication.unfixable' {
            $script:e2e.ddlApplication.unfixable | Should -Be 2
        }

        It 'Should have ddlApplication.rate as percentage' {
            $script:e2e.ddlApplication.rate | Should -Be 88.2
        }

        It 'Should have fixLoop section with totalAttempted' {
            $script:e2e.fixLoop.totalAttempted | Should -Be 1
        }

        It 'Should have fixLoop.totalFixed' {
            $script:e2e.fixLoop.totalFixed | Should -Be 1
        }

        It 'Should have fixLoop.averageAttempts' {
            $script:e2e.fixLoop.averageAttempts | Should -Be 2.0
        }

        It 'Should have fixLoop.objects with per-object details' {
            $script:e2e.fixLoop.objects.Count | Should -Be 1
            $script:e2e.fixLoop.objects[0].name | Should -Be 'dbo.sp_GetUsers'
            $script:e2e.fixLoop.objects[0].attempts | Should -Be 2
            $script:e2e.fixLoop.objects[0].finalStatus | Should -Be 'fixed'
            $script:e2e.fixLoop.objects[0].explanation | Should -Be 'Changed hierarchyid to uuid'
        }

        It 'Should include originalError from first error in fix loop objects' {
            $script:e2e.fixLoop.objects[0].originalError | Should -Be 'type "hierarchyid" does not exist'
        }

        It 'Should include finalDdl in fix loop objects' {
            $script:e2e.fixLoop.objects[0].finalDdl | Should -Be 'CREATE OR REPLACE FUNCTION sp_getusers() ...'
        }

        It 'Should have dataMigration section with correct tables total' {
            $script:e2e.dataMigration.tablesTotal | Should -Be 8
        }

        It 'Should have dataMigration.tablesSucceeded' {
            $script:e2e.dataMigration.tablesSucceeded | Should -Be 7
        }

        It 'Should have dataMigration.tablesFailed' {
            $script:e2e.dataMigration.tablesFailed | Should -Be 1
        }

        It 'Should have dataMigration.totalRows' {
            $script:e2e.dataMigration.totalRows | Should -Be 15420
        }

        It 'Should have dataMigration.rate as percentage' {
            $script:e2e.dataMigration.rate | Should -Be 87.5
        }

        It 'Should have dataMigration.elapsed' {
            $script:e2e.dataMigration.elapsed | Should -Be 12.3
        }

        It 'Should have functionalTests section with totals' {
            $script:e2e.functionalTests.total | Should -Be 20
            $script:e2e.functionalTests.passed | Should -Be 17
            $script:e2e.functionalTests.failed | Should -Be 3
        }

        It 'Should have functionalTests.rate as percentage' {
            $script:e2e.functionalTests.rate | Should -Be 85.0
        }

        It 'Should have functionalTests.results with per-script entries' {
            $script:e2e.functionalTests.results.Count | Should -Be 2
            $script:e2e.functionalTests.results[0].script | Should -Be 'basic-queries.sql'
            $script:e2e.functionalTests.results[0].test | Should -Be 'Select all departments'
            $script:e2e.functionalTests.results[0].status | Should -Be 'pass'
        }

        It 'Should include errorMessage in failed functional test results' {
            $script:e2e.functionalTests.results[1].errorMessage | Should -Be 'timeout exceeded'
        }

        It 'Should have timing section with all step timings' {
            $script:e2e.timing.applyElapsed | Should -Be 8.2
            $script:e2e.timing.fixLoopElapsed | Should -Be 45.1
            $script:e2e.timing.dataMigrationElapsed | Should -Be 12.3
            $script:e2e.timing.functionalTestElapsed | Should -Be 6.7
            $script:e2e.timing.totalEndToEndElapsed | Should -Be 72.3
        }
    }

    Context 'End-to-end delta is computed from previous report' {

        BeforeAll {
            # Create a previous report with an E2E score
            $script:prevE2EDir = Join-Path $TestDrive 'prev-e2e-reports'
            New-Item -ItemType Directory -Path $script:prevE2EDir -Force | Out-Null

            $previousReport = @{
                reportId            = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
                timestamp           = '2024-01-01T00:00:00.0000000+00:00'
                totalElapsedSeconds = 100.0
                validationMode      = 'live-instance'
                configHashes        = @{ 'type-mappings.json' = 'old-hash' }
                databases           = @(
                    @{
                        name  = 'TestDB'
                        score = @{
                            compatibilityScore = 40.0
                            pass               = 2
                            failSyntax         = 2
                            failConvert        = 1
                            skip               = 0
                        }
                    }
                )
                aggregate = @{
                    compatibilityScore = 40.0
                    totalPass          = 2
                    totalFailSyntax    = 2
                    totalFailConvert   = 1
                    totalSkip          = 0
                }
                diagnostics = @{
                    rootCauseCategories = @()
                    topFailingTypes     = @()
                }
                endToEnd = @{
                    enabled       = $true
                    endToEndScore = 72.0
                }
            }

            $prevReportPath = Join-Path $script:prevE2EDir 'previous-e2e-report.json'
            $previousReport | ConvertTo-Json -Depth 10 | Set-Content -Path $prevReportPath -Encoding UTF8

            $e2eResults = @{
                scoring = @{
                    endToEndScore   = 85.2
                    ddlRate         = 0.882
                    dataRate        = 0.875
                    testRate        = 0.85
                    appliedFirstTry = 11
                    appliedAfterFix = 4
                    unfixable       = 2
                }
                ddlResults = @(
                    @{ objectName = 'dbo.Users'; status = 'applied'; errorMessage = $null; elapsedMs = 50 }
                )
                fixResults = @()
                dataMigrationResults = @{
                    tablesSucceeded = 5
                    tablesFailed    = 0
                    totalRows       = 1000
                    elapsed         = 5.0
                }
                functionalTestResults = @{
                    total   = 10
                    passed  = 8
                    failed  = 2
                    results = @()
                }
                timing = @{
                    applyElapsed          = 5.0
                    fixLoopElapsed        = 0.0
                    dataMigrationElapsed  = 5.0
                    functionalTestElapsed = 3.0
                    totalEndToEndElapsed  = 13.0
                }
            }

            $script:result = Invoke-ReportGeneration `
                -ScoringResult $script:scoringResult `
                -DiagnosticsResult $script:diagnosticsResult `
                -ObjectResults $script:objectResults `
                -TotalElapsedSeconds 80.0 `
                -ValidationMode 'live-instance' `
                -ConfigHashes $script:configHashes `
                -EndToEndResults $e2eResults `
                -OutputDirectory (Join-Path $TestDrive 'e2e-delta-test') `
                -PreviousReportPath $prevReportPath
            $script:report = $script:result.Report
            $script:e2e = $script:report.endToEnd
        }

        It 'Should have previousEndToEndScore from previous report' {
            $script:e2e.previousEndToEndScore | Should -Be 72.0
        }

        It 'Should compute correct endToEndDelta (current - previous)' {
            # 85.2 - 72.0 = 13.2
            $script:e2e.endToEndDelta | Should -Be 13.2
        }

        It 'Should also compute compatibility score delta from previous report' {
            $script:report.aggregate.previousScore | Should -Be 40.0
            $script:report.aggregate.delta | Should -Be 20.0
        }
    }
}
