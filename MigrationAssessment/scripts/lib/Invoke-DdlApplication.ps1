<#
.SYNOPSIS
    PostgreSQL DDL Application module for the Migration Validation Pipeline.

.DESCRIPTION
    Applies generated PostgreSQL DDL statements to a live PostgreSQL destination database.
    Implements dependency resolution via topological sort (tables → views → functions → procedures → triggers),
    database isolation via drop/recreate, and graceful error handling.

.NOTES
    Requirements: 1.1, 1.2, 1.3, 1.4, 1.5
#>

function Invoke-DdlApplication {
    <#
    .SYNOPSIS
        Applies PostgreSQL DDL statements to a destination database with dependency ordering.

    .PARAMETER DdlStatements
        Array of objects with properties: objectName, objectType, ddl, dependencies

    .PARAMETER PgConnectionString
        PostgreSQL connection string for the destination database where DDL will be applied.

    .PARAMETER MaintenanceConnectionString
        PostgreSQL connection string to the 'postgres' database, used for DROP/CREATE DATABASE operations.

    .PARAMETER DatabaseName
        Name of the destination database to drop and recreate before applying DDL.

    .OUTPUTS
        Array of objects with: objectName, status (applied/failed), errorMessage, elapsedMs
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [array]$DdlStatements,

        [Parameter(Mandatory)]
        [string]$PgConnectionString,

        [Parameter(Mandatory)]
        [string]$MaintenanceConnectionString,

        [Parameter(Mandatory)]
        [string]$DatabaseName
    )

    # Attempt to drop and recreate the destination database for isolation
    $dbSetupResult = Initialize-DestinationDatabase -MaintenanceConnectionString $MaintenanceConnectionString -DatabaseName $DatabaseName
    if (-not $dbSetupResult.Success) {
        # Connection/setup failed — return error result for all objects without crashing
        $results = @()
        foreach ($stmt in $DdlStatements) {
            $results += [PSCustomObject]@{
                objectName   = $stmt.objectName
                status       = "failed"
                errorMessage = "Database setup failed: $($dbSetupResult.ErrorMessage)"
                elapsedMs    = 0
            }
        }
        return $results
    }

    # Connect to the destination database for DDL application
    $connection = Connect-PostgreSQL -ConnectionString $PgConnectionString
    if (-not $connection) {
        # Connection unavailable — return error result for all objects without crashing
        $results = @()
        foreach ($stmt in $DdlStatements) {
            $results += [PSCustomObject]@{
                objectName   = $stmt.objectName
                status       = "failed"
                errorMessage = "Failed to connect to destination database"
                elapsedMs    = 0
            }
        }
        return $results
    }

    try {
        # Sort DDL statements in dependency order (tables → views → functions → procedures → triggers)
        $sortedStatements = Get-DdlApplicationOrder -DdlStatements $DdlStatements

        # Apply each DDL statement and record results
        $results = @()
        foreach ($stmt in $sortedStatements) {
            $result = Invoke-SingleDdlStatement -Statement $stmt -Connection $connection
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

function Initialize-DestinationDatabase {
    <#
    .SYNOPSIS
        Drops and recreates the destination database for pipeline isolation.
    .OUTPUTS
        Object with: Success (bool), ErrorMessage (string or $null)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$MaintenanceConnectionString,

        [Parameter(Mandatory)]
        [string]$DatabaseName
    )

    $connection = $null
    try {
        $connection = Connect-PostgreSQL -ConnectionString $MaintenanceConnectionString
        if (-not $connection) {
            return @{ Success = $false; ErrorMessage = "Failed to connect to maintenance database (postgres)" }
        }

        # Terminate existing connections to the target database
        $terminateCmd = $connection.CreateCommand()
        $terminateCmd.CommandText = @"
SELECT pg_terminate_backend(pid)
FROM pg_stat_activity
WHERE datname = '$DatabaseName' AND pid <> pg_backend_pid();
"@
        try {
            [void]$terminateCmd.ExecuteNonQuery()
        }
        catch {
            # Non-fatal: database may not exist yet
            Write-Verbose "Could not terminate existing connections: $($_.Exception.Message)"
        }
        finally {
            if ($terminateCmd) { $terminateCmd.Dispose() }
        }

        # Drop the database if it exists
        $dropCmd = $connection.CreateCommand()
        $dropCmd.CommandText = "DROP DATABASE IF EXISTS `"$DatabaseName`";"
        try {
            [void]$dropCmd.ExecuteNonQuery()
        }
        catch {
            return @{ Success = $false; ErrorMessage = "Failed to drop database '$DatabaseName': $($_.Exception.Message)" }
        }
        finally {
            if ($dropCmd) { $dropCmd.Dispose() }
        }

        # Create a fresh database
        $createCmd = $connection.CreateCommand()
        $createCmd.CommandText = "CREATE DATABASE `"$DatabaseName`";"
        try {
            [void]$createCmd.ExecuteNonQuery()
        }
        catch {
            return @{ Success = $false; ErrorMessage = "Failed to create database '$DatabaseName': $($_.Exception.Message)" }
        }
        finally {
            if ($createCmd) { $createCmd.Dispose() }
        }

        return @{ Success = $true; ErrorMessage = $null }
    }
    catch {
        return @{ Success = $false; ErrorMessage = "Database initialization error: $($_.Exception.Message)" }
    }
    finally {
        if ($connection) {
            try { $connection.Close() } catch { }
            try { $connection.Dispose() } catch { }
        }
    }
}

function Get-DdlApplicationOrder {
    <#
    .SYNOPSIS
        Returns DDL statements sorted in topological dependency order.
        Primary sort: type priority (tables → views → functions → procedures → triggers).
        Secondary sort: dependency graph within each type group.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [array]$DdlStatements
    )

    # Define type priority ordering (case-insensitive hashtable)
    $typePriority = @{
        'Table'           = 1
        'View'            = 2
        'Function'        = 3
        'StoredProcedure' = 4
        'Procedure'       = 4
        'Trigger'         = 5
    }

    # Build dependency graph
    $graph = @{}
    $knownObjects = @($DdlStatements | ForEach-Object { $_.objectName })

    foreach ($stmt in $DdlStatements) {
        $name = $stmt.objectName
        if (-not $graph.ContainsKey($name)) {
            $graph[$name] = @()
        }

        if ($stmt.dependencies) {
            $validDeps = @($stmt.dependencies | Where-Object { $_ -in $knownObjects })
            $graph[$name] = $validDeps
        }
    }

    # Kahn's algorithm for topological sort
    $inDegree = @{}
    foreach ($node in $graph.Keys) {
        if (-not $inDegree.ContainsKey($node)) {
            $inDegree[$node] = 0
        }
    }

    foreach ($node in $graph.Keys) {
        foreach ($dep in $graph[$node]) {
            if ($graph.ContainsKey($dep)) {
                $inDegree[$node] = $inDegree[$node] + 1
            }
        }
    }

    # Start with nodes that have no unresolved dependencies (in-degree 0)
    $queue = [System.Collections.Generic.Queue[string]]::new()

    # Sort zero-degree nodes by type priority for deterministic ordering
    $zeroDegreeNodes = @($inDegree.Keys | Where-Object { $inDegree[$_] -eq 0 })
    $zeroDegreeNodes = $zeroDegreeNodes | Sort-Object {
        $stmt = $DdlStatements | Where-Object { $_.objectName -eq $_ }
        $type = if ($stmt) { $stmt.objectType } else { '' }
        if ($typePriority.ContainsKey($type)) { $typePriority[$type] } else { 99 }
    }

    foreach ($node in $zeroDegreeNodes) {
        $queue.Enqueue($node)
    }

    $sorted = @()
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        $sorted += $current

        # Find all nodes that depend on $current and reduce their in-degree
        foreach ($node in $graph.Keys) {
            if ($graph[$node] -contains $current) {
                $inDegree[$node] = $inDegree[$node] - 1
                if ($inDegree[$node] -eq 0) {
                    $queue.Enqueue($node)
                }
            }
        }
    }

    # Any remaining nodes (cycles) are appended at end sorted by type priority
    $remaining = @($graph.Keys | Where-Object { $_ -notin $sorted })
    if ($remaining.Count -gt 0) {
        $remaining = $remaining | Sort-Object {
            $stmt = $DdlStatements | Where-Object { $_.objectName -eq $_ }
            $type = if ($stmt) { $stmt.objectType } else { '' }
            if ($typePriority.ContainsKey($type)) { $typePriority[$type] } else { 99 }
        }
        $sorted += $remaining
    }

    # Map sorted names back to statement objects
    $orderedStatements = @()
    foreach ($name in $sorted) {
        $stmt = $DdlStatements | Where-Object { $_.objectName -eq $name }
        if ($stmt) {
            $orderedStatements += $stmt
        }
    }

    return $orderedStatements
}

function Invoke-SingleDdlStatement {
    <#
    .SYNOPSIS
        Executes a single DDL statement against the PostgreSQL connection.
    .OUTPUTS
        Object with: objectName, status (applied/failed), errorMessage, elapsedMs
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        $Statement,

        [Parameter(Mandatory)]
        $Connection
    )

    $objectName = $Statement.objectName
    $ddl = $Statement.ddl

    $result = [PSCustomObject]@{
        objectName   = $objectName
        status       = "applied"
        errorMessage = $null
        elapsedMs    = 0
    }

    if ([string]::IsNullOrWhiteSpace($ddl)) {
        $result.status = "failed"
        $result.errorMessage = "DDL statement is empty or null"
        return $result
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $cmd = $null

    try {
        $cmd = $Connection.CreateCommand()
        $cmd.CommandText = $ddl
        $cmd.CommandTimeout = 60

        [void]$cmd.ExecuteNonQuery()

        $stopwatch.Stop()
        $result.elapsedMs = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
    }
    catch {
        $stopwatch.Stop()
        $result.status = "failed"
        $result.errorMessage = $_.Exception.Message
        $result.elapsedMs = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
    }
    finally {
        if ($cmd) {
            try { $cmd.Dispose() } catch { }
        }
    }

    return $result
}

# Reuse Connect-PostgreSQL from Invoke-PgValidation.ps1 if not already loaded
if (-not (Get-Command -Name 'Connect-PostgreSQL' -ErrorAction SilentlyContinue)) {
    function Connect-PostgreSQL {
        <#
        .SYNOPSIS
            Attempts to connect to a PostgreSQL instance using Npgsql.
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
                Write-Verbose "Npgsql assembly not available. Cannot connect to PostgreSQL."
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
}

# Export module functions when loaded as a module
if ($MyInvocation.MyCommand.ScriptBlock.Module) {
    Export-ModuleMember -Function Invoke-DdlApplication
}
