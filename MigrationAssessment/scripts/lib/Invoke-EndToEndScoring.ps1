<#
.SYNOPSIS
    End-to-End Scoring module for the Migration Validation Pipeline.

.DESCRIPTION
    Computes a composite End_To_End_Score from DDL application, data migration,
    and functional test results. Applies configurable weights and handles
    skip scenarios (re-weighting when tests or data migration are skipped).

.NOTES
    Requirements: 5.1, 5.2, 5.4, 5.5, 5.6
#>

function Invoke-EndToEndScoring {
    <#
    .SYNOPSIS
        Computes the composite End-to-End Score from pipeline step results.

    .PARAMETER DdlResults
        Array from Invoke-DdlApplication. Each element has: objectName, status ("applied"/"failed"), errorMessage, elapsedMs.

    .PARAMETER FixResults
        Array from Invoke-FixLoop. Each element has: objectName, finalStatus ("fixed"/"unfixable"), attempts, fixedDdl, explanation, errors.

    .PARAMETER DataMigrationResults
        Hashtable from Invoke-DataMigration with: tablesSucceeded, tablesFailed, totalRows, elapsed, rawOutput, status ("completed"/"skipped"/"failed").

    .PARAMETER FunctionalTestResults
        Hashtable from Invoke-FunctionalTests with: total, passed, failed, results[], status ("completed"/"skipped"/"failed").

    .PARAMETER Weights
        Hashtable with scoring weights. Default: @{ ddl = 0.4; data = 0.3; test = 0.3 }.

    .OUTPUTS
        PSCustomObject with: endToEndScore, ddlRate, dataRate, testRate, appliedFirstTry, appliedAfterFix, unfixable
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [array]$DdlResults,

        [Parameter()]
        [array]$FixResults = @(),

        [Parameter()]
        [hashtable]$DataMigrationResults = @{ status = "skipped" },

        [Parameter()]
        [hashtable]$FunctionalTestResults = @{ status = "skipped" },

        [Parameter()]
        [hashtable]$Weights = @{ ddl = 0.4; data = 0.3; test = 0.3 }
    )

    # Count DDL application categories
    $appliedFirstTry = @($DdlResults | Where-Object { $_.status -eq "applied" }).Count
    $appliedAfterFix = @($FixResults | Where-Object { $_.finalStatus -eq "fixed" }).Count
    $unfixable = @($FixResults | Where-Object { $_.finalStatus -eq "unfixable" }).Count

    # DDL Rate: (applied first try + fixed) / total objects
    $totalObjects = $DdlResults.Count
    if ($totalObjects -gt 0) {
        $ddlRate = ($appliedFirstTry + $appliedAfterFix) / $totalObjects
    }
    else {
        $ddlRate = 0.0
    }

    # Data Rate: tables migrated / total tables
    $dataSkipped = ($null -eq $DataMigrationResults) -or ($DataMigrationResults.status -eq "skipped")
    $dataRate = 0.0
    if (-not $dataSkipped) {
        $totalTables = $DataMigrationResults.tablesSucceeded + $DataMigrationResults.tablesFailed
        if ($totalTables -gt 0) {
            $dataRate = $DataMigrationResults.tablesSucceeded / $totalTables
        }
    }

    # Test Rate: tests passed / total tests
    $testsSkipped = ($null -eq $FunctionalTestResults) -or ($FunctionalTestResults.status -eq "skipped")
    $testRate = 0.0
    if (-not $testsSkipped) {
        $totalTests = $FunctionalTestResults.total
        if ($totalTests -gt 0) {
            $testRate = $FunctionalTestResults.passed / $totalTests
        }
    }

    # Compute composite score with appropriate weighting
    if ($dataSkipped) {
        # Data migration was skipped — return DDL rate only as E2E score
        $endToEndScore = [math]::Round($ddlRate * 100, 1)
    }
    elseif ($testsSkipped) {
        # Functional tests were skipped — re-weight: DDL=57%, Data=43%
        $endToEndScore = [math]::Round((0.57 * $ddlRate + 0.43 * $dataRate) * 100, 1)
    }
    else {
        # All steps available — use configured weights
        $ddlWeight = $Weights.ddl
        $dataWeight = $Weights.data
        $testWeight = $Weights.test
        $endToEndScore = [math]::Round(($ddlWeight * $ddlRate + $dataWeight * $dataRate + $testWeight * $testRate) * 100, 1)
    }

    # Round individual rates to percentages for reporting
    $ddlRatePercent = [math]::Round($ddlRate * 100, 1)
    $dataRatePercent = [math]::Round($dataRate * 100, 1)
    $testRatePercent = [math]::Round($testRate * 100, 1)

    return [PSCustomObject]@{
        endToEndScore  = $endToEndScore
        ddlRate        = $ddlRatePercent
        dataRate       = $dataRatePercent
        testRate       = $testRatePercent
        appliedFirstTry = $appliedFirstTry
        appliedAfterFix = $appliedAfterFix
        unfixable      = $unfixable
    }
}

# Export module functions when loaded as a module
if ($MyInvocation.MyCommand.ScriptBlock.Module) {
    Export-ModuleMember -Function Invoke-EndToEndScoring
}
