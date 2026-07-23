<#
.SYNOPSIS
    Scoring Engine for the Migration Validation Pipeline.

.DESCRIPTION
    Computes per-database and aggregate Compatibility_Scores from object validation results.
    Classifies objects, generates per-type breakdowns, computes score deltas from previous runs,
    and identifies top failing types when aggregate score falls below 70%.

.PARAMETER ObjectResults
    Array of per-object validation results. Each element should be a hashtable/PSObject with:
      - objectName   : e.g., "dbo.sp_ProcessOrder"
      - objectType   : e.g., "StoredProcedure", "Table", "View", "Function", "Trigger"
      - databaseName : e.g., "ProcedureComplexityDB"
      - status       : "pass", "fail-syntax", "fail-convert", or "skip"

.PARAMETER PreviousScores
    Hashtable keyed by database name with previous Compatibility_Score values.
    Used for delta computation. Pass $null or empty hashtable if no previous run exists.

.OUTPUTS
    Hashtable with:
      - databases       : Hashtable keyed by database name with per-database score info
      - aggregate       : Aggregate score info across all databases
      - topFailingTypes : Array of up to 5 failing types when aggregate < 70%
#>
function Invoke-Scoring {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [array]$ObjectResults,

        [hashtable]$PreviousScores = @{}
    )

    if ($null -eq $PreviousScores) {
        $PreviousScores = @{}
    }

    $validObjectTypes = @('Table', 'View', 'StoredProcedure', 'Function', 'Trigger')

    # Group results by database
    $byDatabase = @{}
    foreach ($obj in $ObjectResults) {
        $dbName = $obj.databaseName
        if (-not $byDatabase.ContainsKey($dbName)) {
            $byDatabase[$dbName] = [System.Collections.ArrayList]::new()
        }
        [void]$byDatabase[$dbName].Add($obj)
    }

    # Process each database
    $databaseResults = @{}
    $totalPass = 0
    $totalFailSyntax = 0
    $totalFailConvert = 0
    $totalSkip = 0
    $includedInAggregate = [System.Collections.ArrayList]::new()

    foreach ($dbName in $byDatabase.Keys) {
        $dbObjects = $byDatabase[$dbName]

        # Count statuses
        $pass = 0
        $failSyntax = 0
        $failConvert = 0
        $skip = 0

        foreach ($obj in $dbObjects) {
            switch ($obj.status) {
                'pass'         { $pass++ }
                'fail-syntax'  { $failSyntax++ }
                'fail-convert' { $failConvert++ }
                'skip'         { $skip++ }
            }
        }

        # Compute per-database score
        $convertibleCount = $pass + $failSyntax + $failConvert
        if ($convertibleCount -eq 0) {
            # All objects are skip - report N/A
            $compatibilityScore = 'N/A'
        }
        else {
            $compatibilityScore = [Math]::Round(($pass / $convertibleCount) * 100, 1)
            # Accumulate for aggregate
            $totalPass += $pass
            $totalFailSyntax += $failSyntax
            $totalFailConvert += $failConvert
            [void]$includedInAggregate.Add($dbName)
        }
        $totalSkip += $skip

        # Compute per-type breakdown
        $byType = @{}
        foreach ($typeName in $validObjectTypes) {
            $typeObjects = $dbObjects | Where-Object { $_.objectType -eq $typeName }
            if ($null -eq $typeObjects) {
                continue
            }
            # Ensure it's an array
            if ($typeObjects -isnot [array]) {
                $typeObjects = @($typeObjects)
            }
            if ($typeObjects.Count -eq 0) {
                continue
            }

            $typePass = @($typeObjects | Where-Object { $_.status -eq 'pass' }).Count
            $typeFail = @($typeObjects | Where-Object { $_.status -eq 'fail-syntax' -or $_.status -eq 'fail-convert' }).Count

            $typeConvertible = $typePass + $typeFail
            if ($typeConvertible -gt 0) {
                $typeScore = [Math]::Round(($typePass / $typeConvertible) * 100, 1)
            }
            else {
                $typeScore = 'N/A'
            }

            $byType[$typeName] = @{
                pass  = $typePass
                fail  = $typeFail
                score = $typeScore
            }
        }

        # Compute delta from previous run
        $previousScore = $null
        $delta = $null
        if ($PreviousScores.ContainsKey($dbName)) {
            $previousScore = $PreviousScores[$dbName]
            if ($compatibilityScore -ne 'N/A' -and $null -ne $previousScore -and $previousScore -ne 'N/A') {
                $delta = [Math]::Round($compatibilityScore - $previousScore, 1)
            }
        }

        $databaseResults[$dbName] = @{
            compatibilityScore = $compatibilityScore
            pass               = $pass
            failSyntax         = $failSyntax
            failConvert        = $failConvert
            skip               = $skip
            byType             = $byType
            previousScore      = $previousScore
            delta              = $delta
        }
    }

    # Compute aggregate score (excluding N/A databases)
    $aggregateConvertible = $totalPass + $totalFailSyntax + $totalFailConvert
    if ($aggregateConvertible -eq 0) {
        $aggregateScore = 'N/A'
    }
    else {
        $aggregateScore = [Math]::Round(($totalPass / $aggregateConvertible) * 100, 1)
    }

    # Compute aggregate delta if previous aggregate exists
    $aggregatePreviousScore = $null
    $aggregateDelta = $null
    if ($PreviousScores.ContainsKey('__aggregate__')) {
        $aggregatePreviousScore = $PreviousScores['__aggregate__']
        if ($aggregateScore -ne 'N/A' -and $null -ne $aggregatePreviousScore -and $aggregatePreviousScore -ne 'N/A') {
            $aggregateDelta = [Math]::Round($aggregateScore - $aggregatePreviousScore, 1)
        }
    }

    # Top failing types when aggregate < 70%
    $topFailingTypes = @()
    if ($aggregateScore -ne 'N/A' -and $aggregateScore -lt 70.0) {
        # Collect failure counts per object type across all databases
        $typeFailureCounts = @{}
        foreach ($obj in $ObjectResults) {
            if ($obj.status -eq 'fail-syntax' -or $obj.status -eq 'fail-convert') {
                $objType = $obj.objectType
                if (-not $typeFailureCounts.ContainsKey($objType)) {
                    $typeFailureCounts[$objType] = 0
                }
                $typeFailureCounts[$objType]++
            }
        }

        # Sort by failure count descending, take top 5
        $topFailingTypes = $typeFailureCounts.GetEnumerator() |
            Sort-Object -Property Value -Descending |
            Select-Object -First 5 |
            ForEach-Object {
                @{
                    type      = $_.Key
                    failCount = $_.Value
                }
            }

        # Ensure it's an array
        if ($topFailingTypes -isnot [array]) {
            $topFailingTypes = @($topFailingTypes)
        }
    }

    # Return results
    return @{
        databases       = $databaseResults
        aggregate       = @{
            compatibilityScore = $aggregateScore
            previousScore      = $aggregatePreviousScore
            delta              = $aggregateDelta
            totalPass          = $totalPass
            totalFailSyntax    = $totalFailSyntax
            totalFailConvert   = $totalFailConvert
            totalSkip          = $totalSkip
        }
        topFailingTypes = $topFailingTypes
    }
}
