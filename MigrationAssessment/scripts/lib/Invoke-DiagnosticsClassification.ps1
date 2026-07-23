<#
.SYNOPSIS
    Classifies migration pipeline failures into root cause categories.

.DESCRIPTION
    Accepts an array of failed object results and classifies each failure into
    exactly one root cause category using regex pattern matching on the error
    message and generated DDL context. Groups failures by category, ranks by
    failure count descending, and includes affected object names and details.

.PARAMETER FailedObjects
    Array of objects representing failed validations. Each object should have:
    - objectName: e.g., "dbo.sp_ProcessOrder"
    - objectType: e.g., "StoredProcedure"
    - status: "fail-syntax" or "fail-convert"
    - errorMessage: the specific error message
    - errorLineNumber: line number where parsing failed
    - generatedDdl: the full generated DDL text

.OUTPUTS
    Array of category objects sorted by count descending. Each contains:
    - category: the root cause category name
    - count: number of failures in this category
    - objects: array of affected object names
    - details: array of objects with errorMessage, lineNumber, and ddl

.EXAMPLE
    $failures = @(
        @{
            objectName = "dbo.sp_ProcessOrder"
            objectType = "StoredProcedure"
            status = "fail-syntax"
            errorMessage = "type `"hierarchyid`" does not exist"
            errorLineNumber = 5
            generatedDdl = "CREATE TABLE..."
        }
    )
    $results = Invoke-DiagnosticsClassification -FailedObjects $failures
#>
function Invoke-DiagnosticsClassification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [array]$FailedObjects
    )

    # Define regex patterns for each root cause category.
    # Order matters: patterns are evaluated top-to-bottom, first match wins.
    $categoryPatterns = [ordered]@{
        'AI prompt deficiency' = @{
            ErrorPattern = '(?i)(empty|placeholder|todo|not implemented|stub|no output|null|blank)\s*(output|result|conversion|body|content)?'
            DdlPattern   = '(?i)^(\s*(--|/\*.*\*/)\s*)*$|^\s*$|TODO|PLACEHOLDER|NOT_IMPLEMENTED'
            Description  = 'Conversion produced empty or placeholder output'
        }
        'type mapping gap' = @{
            ErrorPattern = '(?i)(type\s+"?[\w.]+"?\s+(does not exist|is not defined|unknown|undefined))|(unrecognized\s+data\s*type)|(cannot\s+cast.*type)|(column\s+"?\w+"?\s+.*type\s+"?[\w.]+"?\s+(does not exist|unknown))'
            DdlPattern   = $null
            Description  = 'Error references unrecognized data type'
        }
        'function mapping gap' = @{
            ErrorPattern = '(?i)(function\s+"?[\w.]+"?\s*(\(.*\)\s+)?does not exist)|(operator\s+(does not exist|is not unique))|(undefined\s+function)|(unknown\s+function)|(no\s+function\s+matches)'
            DdlPattern   = $null
            Description  = 'Error references undefined function or operator'
        }
        'procedural pattern not handled' = @{
            ErrorPattern = '(?i)(syntax\s+error.*(BEGIN|END|DECLARE|LOOP|IF|ELSE|RETURN|RAISE|EXCEPTION|EXECUTE|PERFORM))|(at\s+or\s+near\s+"?(BEGIN|END|DECLARE|LOOP|IF|ELSE|RETURN|RAISE|EXCEPTION)"?)|(ERROR.*PL/pgSQL)|(unterminated\s+(block|function|procedure))'
            DdlPattern   = '(?i)(CREATE\s+(OR\s+REPLACE\s+)?(FUNCTION|PROCEDURE)\b)|(DO\s*\$\$)|\$\$\s*LANGUAGE\s+plpgsql|(BEGIN\s)'
            Description  = 'Error occurs within PL/pgSQL block body'
        }
        'dependency resolution failure' = @{
            ErrorPattern = '(?i)(relation\s+"?[\w.]+"?\s+(does not exist|not found|cannot be found|unknown|undefined|missing))|(table\s+"?[\w.]+"?\s+(does not exist|not found))|(view\s+"?[\w.]+"?\s+(does not exist|not found))|(schema\s+"?[\w.]+"?\s+(does not exist|not found))'
            DdlPattern   = $null
            Description  = 'Error references missing prerequisite object'
        }
    }

    # Initialize category buckets
    $categories = @{}
    foreach ($catName in $categoryPatterns.Keys) {
        $categories[$catName] = @{
            category = $catName
            count    = 0
            objects  = [System.Collections.Generic.List[string]]::new()
            details  = [System.Collections.Generic.List[object]]::new()
        }
    }

    # Classify each failed object
    foreach ($obj in $FailedObjects) {
        $errorMsg = if ($obj.errorMessage) { $obj.errorMessage } else { '' }
        $ddl = if ($obj.generatedDdl) { $obj.generatedDdl } else { '' }
        $objectName = if ($obj.objectName) { $obj.objectName } else { 'unknown' }
        $lineNumber = if ($null -ne $obj.errorLineNumber) { $obj.errorLineNumber } else { $null }

        $classified = $false

        foreach ($catName in $categoryPatterns.Keys) {
            $pattern = $categoryPatterns[$catName]

            $errorMatch = $false
            $ddlMatch = $false

            # Check error message pattern
            if ($pattern.ErrorPattern -and $errorMsg -match $pattern.ErrorPattern) {
                $errorMatch = $true
            }

            # Check DDL pattern (used for AI prompt deficiency and procedural pattern)
            if ($pattern.DdlPattern -and $ddl -match $pattern.DdlPattern) {
                $ddlMatch = $true
            }

            # Classification logic:
            # - AI prompt deficiency: match on DDL pattern (empty/placeholder) OR error pattern
            # - procedural pattern not handled: match on error pattern AND DDL contains PL/pgSQL block
            # - Others: match on error pattern alone
            $isMatch = $false

            switch ($catName) {
                'AI prompt deficiency' {
                    # Empty/placeholder DDL is the primary signal
                    if ($ddlMatch) {
                        $isMatch = $true
                    }
                    elseif ($errorMatch -and [string]::IsNullOrWhiteSpace($ddl)) {
                        $isMatch = $true
                    }
                }
                'procedural pattern not handled' {
                    # Error must reference procedural constructs, and DDL should be in a PL/pgSQL context
                    if ($errorMatch) {
                        # If DDL pattern is defined and matches, strong signal
                        if ($ddlMatch) {
                            $isMatch = $true
                        }
                        # Even without DDL match, if error clearly references PL/pgSQL patterns
                        elseif ($errorMsg -match '(?i)PL/pgSQL|plpgsql') {
                            $isMatch = $true
                        }
                        # If error is about procedural keywords inside a function/procedure body
                        elseif ($errorMatch) {
                            $isMatch = $true
                        }
                    }
                }
                default {
                    if ($errorMatch) {
                        $isMatch = $true
                    }
                }
            }

            if ($isMatch) {
                $categories[$catName].count++
                $categories[$catName].objects.Add($objectName)
                $categories[$catName].details.Add([PSCustomObject]@{
                    errorMessage = $errorMsg
                    lineNumber   = $lineNumber
                    ddl          = $ddl
                })
                $classified = $true
                break  # Each failure classified into exactly one category
            }
        }

        # If no pattern matched, default to 'procedural pattern not handled' as catch-all
        # since unrecognized errors within conversion are likely procedural issues
        if (-not $classified) {
            $defaultCategory = 'procedural pattern not handled'
            $categories[$defaultCategory].count++
            $categories[$defaultCategory].objects.Add($objectName)
            $categories[$defaultCategory].details.Add([PSCustomObject]@{
                errorMessage = $errorMsg
                lineNumber   = $lineNumber
                ddl          = $ddl
            })
        }
    }

    # Filter out empty categories and sort by count descending
    $result = $categories.Values |
        Where-Object { $_.count -gt 0 } |
        Sort-Object { $_.count } -Descending |
        ForEach-Object {
            [PSCustomObject]@{
                category = $_.category
                count    = $_.count
                objects  = @($_.objects)
                details  = @($_.details)
            }
        }

    return @($result)
}
