<#
.SYNOPSIS
    PostgreSQL DDL Validation module for the Migration Validation Pipeline.

.DESCRIPTION
    Validates generated PostgreSQL DDL statements against a live PostgreSQL instance
    or falls back to syntax-only validation when no instance is available.
    Implements dependency resolution via topological sort and circular dependency detection.

.NOTES
    Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6
#>

function Invoke-PgValidation {
    <#
    .SYNOPSIS
        Validates PostgreSQL DDL statements with dependency resolution and isolation.

    .PARAMETER DdlStatements
        Array of objects with properties: objectName, objectType, ddl, dependencies

    .PARAMETER PgConnectionString
        Optional PostgreSQL connection string. If provided and connection succeeds,
        live-instance validation is used. Otherwise falls back to syntax-only mode.

    .PARAMETER TimeoutSeconds
        Timeout per DDL statement in seconds. Default is 30.

    .OUTPUTS
        Array of objects with: objectName, status, errorMessage, errorLineNumber, validationMode
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [array]$DdlStatements,

        [string]$PgConnectionString,

        [int]$TimeoutSeconds = 30
    )

    # Determine validation mode
    $validationMode = "syntax-only"
    $connection = $null

    if ($PgConnectionString) {
        $connection = Connect-PostgreSQL -ConnectionString $PgConnectionString
        if ($connection) {
            $validationMode = "live-instance"
        }
    }

    try {
        # Build dependency graph and detect cycles
        $graph = Build-DependencyGraph -DdlStatements $DdlStatements
        $cycles = Find-CircularDependencies -Graph $graph
        $sortedObjects = Get-TopologicalSort -Graph $graph -Cycles $cycles

        # Initialize results array
        $results = @()

        # Mark cycle members as fail-syntax
        foreach ($cycleMember in $cycles) {
            $stmt = $DdlStatements | Where-Object { $_.objectName -eq $cycleMember }
            $results += [PSCustomObject]@{
                objectName      = $cycleMember
                status          = "fail-syntax"
                errorMessage    = "Circular dependency detected: object participates in a dependency cycle"
                errorLineNumber = $null
                validationMode  = $validationMode
            }
        }

        # Validate each non-cycle object in topological order
        foreach ($objectName in $sortedObjects) {
            $stmt = $DdlStatements | Where-Object { $_.objectName -eq $objectName }
            if (-not $stmt) { continue }

            if ($validationMode -eq "live-instance") {
                $result = Invoke-LiveInstanceValidation -Statement $stmt -DdlStatements $DdlStatements -Connection $connection -TimeoutSeconds $TimeoutSeconds
            }
            else {
                $result = Invoke-SyntaxOnlyValidation -Statement $stmt
            }

            $result.validationMode = $validationMode
            $results += $result
        }

        return $results
    }
    finally {
        if ($connection) {
            try { $connection.Close() } catch { }
            try { $connection.Dispose() } catch { }
        }
    }
}

function Connect-PostgreSQL {
    <#
    .SYNOPSIS
        Attempts to connect to a PostgreSQL instance.
    .OUTPUTS
        Returns connection object on success, $null on failure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ConnectionString
    )

    try {
        # Load Npgsql if available
        $npgsqlLoaded = $false
        try {
            [void][Npgsql.NpgsqlConnection]
            $npgsqlLoaded = $true
        }
        catch {
            # Try to load from common locations
            $possiblePaths = @(
                (Join-Path $PSScriptRoot "..\..\packages\Npgsql.dll"),
                (Join-Path $PSScriptRoot "Npgsql.dll")
            )
            foreach ($path in $possiblePaths) {
                if (Test-Path $path) {
                    Add-Type -Path $path
                    $npgsqlLoaded = $true
                    break
                }
            }
        }

        if (-not $npgsqlLoaded) {
            Write-Verbose "Npgsql assembly not available. Falling back to syntax-only mode."
            return $null
        }

        $conn = New-Object Npgsql.NpgsqlConnection($ConnectionString)
        $conn.Open()
        return $conn
    }
    catch {
        Write-Verbose "Failed to connect to PostgreSQL: $($_.Exception.Message)"
        return $null
    }
}

function Build-DependencyGraph {
    <#
    .SYNOPSIS
        Builds an adjacency list representation of the dependency graph.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [array]$DdlStatements
    )

    $graph = @{}

    foreach ($stmt in $DdlStatements) {
        $name = $stmt.objectName
        if (-not $graph.ContainsKey($name)) {
            $graph[$name] = @()
        }

        if ($stmt.dependencies) {
            # Filter dependencies to only those that exist in our statement set
            $knownObjects = $DdlStatements | ForEach-Object { $_.objectName }
            $validDeps = @($stmt.dependencies | Where-Object { $_ -in $knownObjects })
            $graph[$name] = $validDeps
        }
    }

    return $graph
}

function Find-CircularDependencies {
    <#
    .SYNOPSIS
        Detects circular dependencies using DFS cycle detection.
    .OUTPUTS
        Array of object names that participate in cycles.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Graph
    )

    $WHITE = 0  # Not visited
    $GRAY = 1   # In current DFS path
    $BLACK = 2  # Fully processed

    $color = @{}
    $cycleMembers = [System.Collections.Generic.HashSet[string]]::new()

    foreach ($node in $Graph.Keys) {
        $color[$node] = $WHITE
    }

    foreach ($node in $Graph.Keys) {
        if ($color[$node] -eq $WHITE) {
            $stack = [System.Collections.Generic.Stack[string]]::new()
            Find-CyclesFromNode -Node $node -Graph $Graph -Color $color -Stack $stack -CycleMembers $cycleMembers
        }
    }

    return @($cycleMembers)
}

function Find-CyclesFromNode {
    <#
    .SYNOPSIS
        DFS helper to find cycles starting from a given node.
    #>
    [CmdletBinding()]
    param(
        [string]$Node,
        [hashtable]$Graph,
        [hashtable]$Color,
        [System.Collections.Generic.Stack[string]]$Stack,
        [System.Collections.Generic.HashSet[string]]$CycleMembers
    )

    $Color[$Node] = 1  # GRAY

    $Stack.Push($Node)

    $deps = $Graph[$Node]
    if ($deps) {
        foreach ($dep in $deps) {
            if (-not $Color.ContainsKey($dep)) {
                # Dependency refers to object not in our set - skip
                continue
            }

            if ($Color[$dep] -eq 1) {
                # Found a cycle - collect all nodes in the cycle from stack
                $cycleNodes = @($Node)
                foreach ($stackItem in $Stack) {
                    if ($stackItem -eq $dep) {
                        $cycleNodes += $stackItem
                        break
                    }
                    $cycleNodes += $stackItem
                }
                foreach ($cn in $cycleNodes) {
                    [void]$CycleMembers.Add($cn)
                }
            }
            elseif ($Color[$dep] -eq 0) {
                Find-CyclesFromNode -Node $dep -Graph $Graph -Color $Color -Stack $Stack -CycleMembers $CycleMembers
            }
        }
    }

    [void]$Stack.Pop()
    $Color[$Node] = 2  # BLACK
}

function Get-TopologicalSort {
    <#
    .SYNOPSIS
        Returns objects in dependency order (dependencies first), excluding cycle members.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Graph,

        [array]$Cycles = @()
    )

    $cycleSet = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($c in $Cycles) {
        [void]$cycleSet.Add($c)
    }

    # Filter out cycle members
    $filteredGraph = @{}
    foreach ($key in $Graph.Keys) {
        if (-not $cycleSet.Contains($key)) {
            $filteredDeps = @($Graph[$key] | Where-Object { -not $cycleSet.Contains($_) })
            $filteredGraph[$key] = $filteredDeps
        }
    }

    # Kahn's algorithm for topological sort
    # graph[A] = [B, C] means A depends on B and C (B and C must come first)
    # For Kahn's: edge from B -> A, edge from C -> A
    # in-degree of A = number of dependencies A has that exist in our graph
    $inDegree = @{}
    foreach ($node in $filteredGraph.Keys) {
        if (-not $inDegree.ContainsKey($node)) {
            $inDegree[$node] = 0
        }
    }

    foreach ($node in $filteredGraph.Keys) {
        foreach ($dep in $filteredGraph[$node]) {
            if ($filteredGraph.ContainsKey($dep)) {
                # dep -> node edge: node's in-degree increases
                if (-not $inDegree.ContainsKey($node)) {
                    $inDegree[$node] = 0
                }
                $inDegree[$node] = $inDegree[$node] + 1
            }
        }
    }

    # Start with nodes that have no dependencies (in-degree 0)
    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($node in $inDegree.Keys) {
        if ($inDegree[$node] -eq 0) {
            $queue.Enqueue($node)
        }
    }

    $sorted = @()
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        $sorted += $current

        # Find all nodes that depend on $current
        foreach ($node in $filteredGraph.Keys) {
            if ($filteredGraph[$node] -contains $current) {
                $inDegree[$node] = $inDegree[$node] - 1
                if ($inDegree[$node] -eq 0) {
                    $queue.Enqueue($node)
                }
            }
        }
    }

    # If there are nodes not in sorted (should not happen without cycles), add them
    foreach ($node in $filteredGraph.Keys) {
        if ($node -notin $sorted) {
            $sorted += $node
        }
    }

    return $sorted
}

function Invoke-LiveInstanceValidation {
    <#
    .SYNOPSIS
        Validates a DDL statement against a live PostgreSQL instance.
        Creates prerequisite objects in the same transaction before validating.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Statement,

        [Parameter(Mandatory)]
        [array]$DdlStatements,

        [Parameter(Mandatory)]
        $Connection,

        [int]$TimeoutSeconds = 30
    )

    $objectName = $Statement.objectName
    $result = [PSCustomObject]@{
        objectName      = $objectName
        status          = "pass"
        errorMessage    = $null
        errorLineNumber = $null
        validationMode  = "live-instance"
    }

    $transaction = $null
    try {
        $transaction = $Connection.BeginTransaction()

        # Create prerequisite objects first (dependency resolution)
        if ($Statement.dependencies) {
            foreach ($depName in $Statement.dependencies) {
                $depStmt = $DdlStatements | Where-Object { $_.objectName -eq $depName }
                if ($depStmt -and $depStmt.ddl) {
                    try {
                        $depCmd = $Connection.CreateCommand()
                        $depCmd.Transaction = $transaction
                        $depCmd.CommandText = $depStmt.ddl
                        $depCmd.CommandTimeout = $TimeoutSeconds
                        [void]$depCmd.ExecuteNonQuery()
                    }
                    catch {
                        # If a dependency fails to create, continue anyway
                        # The main object validation will capture the real error
                    }
                    finally {
                        if ($depCmd) { $depCmd.Dispose() }
                    }
                }
            }
        }

        # Now validate the target DDL
        $cmd = $Connection.CreateCommand()
        $cmd.Transaction = $transaction
        $cmd.CommandText = $Statement.ddl
        $cmd.CommandTimeout = $TimeoutSeconds

        # Execute with timeout using a cancellation token approach
        $task = $cmd.ExecuteNonQueryAsync()
        $completed = $task.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))

        if (-not $completed) {
            try { $cmd.Cancel() } catch { }
            $result.status = "fail-syntax"
            $result.errorMessage = "Statement execution exceeded timeout of $TimeoutSeconds seconds"
            $result.errorLineNumber = $null
        }
        elseif ($task.IsFaulted) {
            $ex = $task.Exception.InnerException
            $result.status = "fail-syntax"
            $result.errorMessage = $ex.Message
            $result.errorLineNumber = Get-PgErrorLineNumber -Exception $ex
        }
    }
    catch [System.TimeoutException] {
        $result.status = "fail-syntax"
        $result.errorMessage = "Statement execution exceeded timeout of $TimeoutSeconds seconds"
        $result.errorLineNumber = $null
    }
    catch {
        $result.status = "fail-syntax"
        $result.errorMessage = $_.Exception.Message
        $result.errorLineNumber = Get-PgErrorLineNumber -Exception $_.Exception
    }
    finally {
        if ($cmd) { try { $cmd.Dispose() } catch { } }
        if ($transaction) {
            try { $transaction.Rollback() } catch { }
            try { $transaction.Dispose() } catch { }
        }
    }

    return $result
}

function Invoke-SyntaxOnlyValidation {
    <#
    .SYNOPSIS
        Performs basic syntax validation when no PostgreSQL instance is available.
        Uses pattern-based checks to catch common syntax issues.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Statement
    )

    $objectName = $Statement.objectName
    $ddl = $Statement.ddl

    $result = [PSCustomObject]@{
        objectName      = $objectName
        status          = "pass"
        errorMessage    = $null
        errorLineNumber = $null
        validationMode  = "syntax-only"
    }

    # Basic validation: check if DDL is null or empty
    if ([string]::IsNullOrWhiteSpace($ddl)) {
        $result.status = "fail-syntax"
        $result.errorMessage = "DDL statement is empty or null"
        $result.errorLineNumber = 1
        return $result
    }

    # Check for balanced parentheses
    $parenCheck = Test-BalancedParentheses -Text $ddl
    if (-not $parenCheck.Balanced) {
        $result.status = "fail-syntax"
        $result.errorMessage = "Unbalanced parentheses detected"
        $result.errorLineNumber = $parenCheck.LineNumber
        return $result
    }

    # Check for unclosed string literals (single quotes)
    $quoteCheck = Test-BalancedQuotes -Text $ddl
    if (-not $quoteCheck.Balanced) {
        $result.status = "fail-syntax"
        $result.errorMessage = "Unclosed string literal detected"
        $result.errorLineNumber = $quoteCheck.LineNumber
        return $result
    }

    # Check for valid PostgreSQL statement structure
    $structureCheck = Test-StatementStructure -Text $ddl -ObjectType $Statement.objectType
    if (-not $structureCheck.Valid) {
        $result.status = "fail-syntax"
        $result.errorMessage = $structureCheck.ErrorMessage
        $result.errorLineNumber = $structureCheck.LineNumber
        return $result
    }

    # Check for common T-SQL remnants that are invalid in PostgreSQL
    $tsqlCheck = Test-NoTSqlRemnants -Text $ddl
    if (-not $tsqlCheck.Valid) {
        $result.status = "fail-syntax"
        $result.errorMessage = $tsqlCheck.ErrorMessage
        $result.errorLineNumber = $tsqlCheck.LineNumber
        return $result
    }

    return $result
}

function Test-BalancedParentheses {
    [CmdletBinding()]
    param([string]$Text)

    $depth = 0
    $lineNumber = 1
    $inString = $false

    for ($i = 0; $i -lt $Text.Length; $i++) {
        $char = $Text[$i]

        if ($char -eq "`n") { $lineNumber++ }

        if ($char -eq "'" -and -not $inString) {
            $inString = $true
            continue
        }
        if ($char -eq "'" -and $inString) {
            # Check for escaped quote
            if ($i + 1 -lt $Text.Length -and $Text[$i + 1] -eq "'") {
                $i++
                continue
            }
            $inString = $false
            continue
        }

        if (-not $inString) {
            if ($char -eq '(') { $depth++ }
            if ($char -eq ')') {
                $depth--
                if ($depth -lt 0) {
                    return @{ Balanced = $false; LineNumber = $lineNumber }
                }
            }
        }
    }

    if ($depth -ne 0) {
        return @{ Balanced = $false; LineNumber = $lineNumber }
    }

    return @{ Balanced = $true; LineNumber = $null }
}

function Test-BalancedQuotes {
    [CmdletBinding()]
    param([string]$Text)

    $inString = $false
    $lineNumber = 1
    $stringStartLine = 0

    for ($i = 0; $i -lt $Text.Length; $i++) {
        $char = $Text[$i]

        if ($char -eq "`n") { $lineNumber++ }

        if ($char -eq "'") {
            if (-not $inString) {
                $inString = $true
                $stringStartLine = $lineNumber
            }
            else {
                # Check for escaped quote ('')
                if ($i + 1 -lt $Text.Length -and $Text[$i + 1] -eq "'") {
                    $i++
                    continue
                }
                $inString = $false
            }
        }
    }

    if ($inString) {
        return @{ Balanced = $false; LineNumber = $stringStartLine }
    }

    return @{ Balanced = $true; LineNumber = $null }
}

function Test-StatementStructure {
    [CmdletBinding()]
    param(
        [string]$Text,
        [string]$ObjectType
    )

    $trimmed = $Text.Trim()

    # Check for valid PostgreSQL DDL start keywords
    $validStarts = @(
        '^CREATE\s+(OR\s+REPLACE\s+)?(TABLE|VIEW|FUNCTION|PROCEDURE|TRIGGER|INDEX|SCHEMA|TYPE|SEQUENCE|EXTENSION)',
        '^ALTER\s+(TABLE|VIEW|FUNCTION|PROCEDURE|TRIGGER|SCHEMA|TYPE|SEQUENCE)',
        '^DROP\s+(TABLE|VIEW|FUNCTION|PROCEDURE|TRIGGER|INDEX|SCHEMA|TYPE|SEQUENCE)',
        '^DO\s+\$',
        '^BEGIN\s*;',
        '^COMMENT\s+ON',
        '^GRANT\s+',
        '^REVOKE\s+',
        '^SET\s+',
        '^INSERT\s+INTO',
        '^CREATE\s+(UNIQUE\s+)?INDEX'
    )

    $foundValidStart = $false
    foreach ($pattern in $validStarts) {
        if ($trimmed -match $pattern) {
            $foundValidStart = $true
            break
        }
    }

    if (-not $foundValidStart) {
        # Determine line number (always 1 for structure issues at start)
        return @{ Valid = $false; ErrorMessage = "DDL does not start with a valid PostgreSQL statement keyword"; LineNumber = 1 }
    }

    return @{ Valid = $true; ErrorMessage = $null; LineNumber = $null }
}

function Test-NoTSqlRemnants {
    <#
    .SYNOPSIS
        Checks for common T-SQL syntax that is not valid in PostgreSQL.
    #>
    [CmdletBinding()]
    param([string]$Text)

    $patterns = @(
        @{ Pattern = '\bGO\b\s*$';           Message = "T-SQL batch separator 'GO' detected" },
        @{ Pattern = '\bSET\s+NOCOUNT\b';    Message = "T-SQL 'SET NOCOUNT' detected" },
        @{ Pattern = '\bDECLARE\s+@';        Message = "T-SQL variable declaration with '@' prefix detected" },
        @{ Pattern = '\bEXEC\s+sp_';         Message = "T-SQL system stored procedure call detected" },
        @{ Pattern = '\[\w+\]\.\[';          Message = "T-SQL bracket-quoted identifier detected" },
        @{ Pattern = '\bNVARCHAR\b';         Message = "T-SQL data type 'NVARCHAR' detected (use VARCHAR in PostgreSQL)" },
        @{ Pattern = '\bDATETIME\b';         Message = "T-SQL data type 'DATETIME' detected (use TIMESTAMP in PostgreSQL)" },
        @{ Pattern = '\bUNIQUEIDENTIFIER\b'; Message = "T-SQL data type 'UNIQUEIDENTIFIER' detected (use UUID in PostgreSQL)" },
        @{ Pattern = '\bIDENTITY\s*\(';      Message = "T-SQL IDENTITY column detected (use SERIAL or GENERATED in PostgreSQL)" }
    )

    $lines = $Text -split "`n"
    foreach ($entry in $patterns) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $entry.Pattern) {
                return @{ Valid = $false; ErrorMessage = $entry.Message; LineNumber = ($i + 1) }
            }
        }
    }

    return @{ Valid = $true; ErrorMessage = $null; LineNumber = $null }
}

function Get-PgErrorLineNumber {
    <#
    .SYNOPSIS
        Extracts line number from a PostgreSQL error, if available.
    #>
    [CmdletBinding()]
    param($Exception)

    if ($null -eq $Exception) { return $null }

    # Npgsql PostgresException has a Line property
    if ($Exception.PSObject.Properties['Line']) {
        return [int]$Exception.Line
    }

    # Try to extract from error message
    $msg = $Exception.Message
    if ($msg -match 'at line (\d+)') {
        return [int]$Matches[1]
    }
    if ($msg -match 'LINE (\d+)') {
        return [int]$Matches[1]
    }

    return $null
}

# Export module functions when loaded as a module
if ($MyInvocation.MyCommand.ScriptBlock.Module) {
    Export-ModuleMember -Function Invoke-PgValidation
}
