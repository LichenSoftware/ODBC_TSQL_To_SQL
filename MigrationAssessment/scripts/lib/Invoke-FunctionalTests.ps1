<#
.SYNOPSIS
    Functional Testing module for the Migration Validation Pipeline.

.DESCRIPTION
    Starts PgPassthrough as a background TDS-to-PostgreSQL proxy, executes T-SQL
    test scripts against it via SqlClient, parses assertion directives, and captures
    per-test pass/fail results.

.NOTES
    Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7
#>

function Invoke-FunctionalTests {
    <#
    .SYNOPSIS
        Executes T-SQL functional test scripts through PgPassthrough against the destination PostgreSQL database.

    .PARAMETER TestScriptDirectory
        Path to the directory containing *.sql test script files.

    .PARAMETER PgPassthroughProjectPath
        Path to the PgPassthrough.Server .NET project directory.

    .PARAMETER PgPassthroughPort
        Port on which PgPassthrough will listen for TDS connections. Default: 11433.

    .PARAMETER DestPgConnectionString
        PostgreSQL connection string for the destination database that PgPassthrough will proxy to.

    .PARAMETER TimeoutPerScript
        Maximum time in seconds to wait for each test script to execute. Default: 30.

    .OUTPUTS
        Hashtable with: total, passed, failed, results[], status
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TestScriptDirectory,

        [Parameter(Mandatory)]
        [string]$PgPassthroughProjectPath,

        [int]$PgPassthroughPort = 11433,

        [Parameter(Mandatory)]
        [string]$DestPgConnectionString,

        [int]$TimeoutPerScript = 30
    )

    # Check if test scripts exist
    if (-not (Test-Path $TestScriptDirectory)) {
        Write-PipelineLog -Level "INFO" -Step "functional-tests" -Message "Test script directory '$TestScriptDirectory' does not exist. Skipping functional tests."
        return @{
            total   = 0
            passed  = 0
            failed  = 0
            results = @()
            status  = "skipped"
        }
    }

    $testScripts = Get-ChildItem -Path $TestScriptDirectory -Filter "*.sql" -ErrorAction SilentlyContinue
    if ($null -eq $testScripts -or $testScripts.Count -eq 0) {
        Write-PipelineLog -Level "INFO" -Step "functional-tests" -Message "No test scripts (*.sql) found in '$TestScriptDirectory'. Skipping functional tests."
        return @{
            total   = 0
            passed  = 0
            failed  = 0
            results = @()
            status  = "skipped"
        }
    }

    Write-PipelineLog -Level "INFO" -Step "functional-tests" -Message "Discovered $($testScripts.Count) test script(s) in '$TestScriptDirectory'"

    # Start PgPassthrough as a background process
    $pgProcess = $null
    try {
        $pgProcess = Start-PgPassthrough `
            -ProjectPath $PgPassthroughProjectPath `
            -Port $PgPassthroughPort `
            -PgConnectionString $DestPgConnectionString

        if ($null -eq $pgProcess) {
            Write-PipelineLog -Level "ERROR" -Step "functional-tests" -Message "Failed to start PgPassthrough process"
            return @{
                total        = 0
                passed       = 0
                failed       = 0
                results      = @()
                status       = "failed"
                errorMessage = "Failed to start PgPassthrough process"
            }
        }

        # Poll TCP port until PgPassthrough is accepting connections
        $ready = Wait-ForTcpPort -Port $PgPassthroughPort -TimeoutSeconds 15
        if (-not $ready) {
            Write-PipelineLog -Level "ERROR" -Step "functional-tests" -Message "PgPassthrough did not become ready on port $PgPassthroughPort within 15 seconds"
            return @{
                total        = 0
                passed       = 0
                failed       = 0
                results      = @()
                status       = "failed"
                errorMessage = "PgPassthrough did not become ready within timeout"
            }
        }

        Write-PipelineLog -Level "INFO" -Step "functional-tests" -Message "PgPassthrough is ready on port $PgPassthroughPort"

        # Execute test scripts
        $allResults = @()
        foreach ($script in $testScripts) {
            $scriptResults = Invoke-TestScript `
                -ScriptPath $script.FullName `
                -Port $PgPassthroughPort `
                -TimeoutSeconds $TimeoutPerScript

            $allResults += $scriptResults
        }

        # Compute totals
        $totalTests = $allResults.Count
        $passedTests = ($allResults | Where-Object { $_.status -eq "pass" }).Count
        $failedTests = ($allResults | Where-Object { $_.status -eq "fail" }).Count

        Write-PipelineLog -Level "INFO" -Step "functional-tests" -Message "Functional tests completed: $passedTests passed, $failedTests failed out of $totalTests total"

        return @{
            total   = $totalTests
            passed  = $passedTests
            failed  = $failedTests
            results = $allResults
            status  = "completed"
        }
    }
    catch {
        $errMsg = "Exception during functional tests: $($_.Exception.Message)"
        Write-PipelineLog -Level "ERROR" -Step "functional-tests" -Message $errMsg
        return @{
            total        = 0
            passed       = 0
            failed       = 0
            results      = @()
            status       = "failed"
            errorMessage = $errMsg
        }
    }
    finally {
        # Always stop PgPassthrough process
        if ($null -ne $pgProcess -and -not $pgProcess.HasExited) {
            Stop-PgPassthrough -Process $pgProcess
        }
    }
}

function Start-PgPassthrough {
    <#
    .SYNOPSIS
        Starts PgPassthrough.Server as a background process.
    .OUTPUTS
        The started Process object, or $null on failure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [int]$Port,

        [Parameter(Mandatory)]
        [string]$PgConnectionString
    )

    $dotnetArgs = @(
        "run", "--project", $ProjectPath, "--",
        "--port", $Port.ToString(),
        "--pg-connection", $PgConnectionString
    )

    $processInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processInfo.FileName = "dotnet"
    $processInfo.Arguments = ($dotnetArgs | ForEach-Object {
        if ($_ -match '\s') { "`"$_`"" } else { $_ }
    }) -join ' '
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true

    try {
        $process = [System.Diagnostics.Process]::Start($processInfo)
        Write-PipelineLog -Level "INFO" -Step "functional-tests" -Message "Started PgPassthrough (PID: $($process.Id)) on port $Port"
        return $process
    }
    catch {
        Write-PipelineLog -Level "ERROR" -Step "functional-tests" -Message "Failed to start PgPassthrough: $($_.Exception.Message)"
        return $null
    }
}

function Wait-ForTcpPort {
    <#
    .SYNOPSIS
        Polls a TCP port until it accepts connections or timeout is reached.
    .OUTPUTS
        Boolean indicating whether the port is accepting connections.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$Port,

        [int]$TimeoutSeconds = 15
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $pollIntervalMs = 500

    while ((Get-Date) -lt $deadline) {
        try {
            $tcpClient = New-Object System.Net.Sockets.TcpClient
            $tcpClient.Connect("127.0.0.1", $Port)
            $tcpClient.Close()
            return $true
        }
        catch {
            # Port not yet accepting connections — wait and retry
            Start-Sleep -Milliseconds $pollIntervalMs
        }
    }

    return $false
}

function Stop-PgPassthrough {
    <#
    .SYNOPSIS
        Stops the PgPassthrough background process gracefully.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    try {
        if (-not $Process.HasExited) {
            $Process.Kill()
            $Process.WaitForExit(5000)
            Write-PipelineLog -Level "INFO" -Step "functional-tests" -Message "Stopped PgPassthrough (PID: $($Process.Id))"
        }
    }
    catch {
        Write-PipelineLog -Level "WARN" -Step "functional-tests" -Message "Error stopping PgPassthrough: $($_.Exception.Message)"
    }
}

function Invoke-TestScript {
    <#
    .SYNOPSIS
        Parses and executes a single test script file against PgPassthrough.
    .DESCRIPTION
        Splits the script by '-- test:' directives, extracts assertions, and
        executes each test block via SqlClient against the PgPassthrough TDS port.
    .OUTPUTS
        Array of result hashtables: scriptName, testName, status, errorMessage, elapsed
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ScriptPath,

        [Parameter(Mandatory)]
        [int]$Port,

        [int]$TimeoutSeconds = 30
    )

    $scriptName = [System.IO.Path]::GetFileName($ScriptPath)
    $content = Get-Content -Path $ScriptPath -Raw

    # Parse test blocks from the script
    $testBlocks = Split-TestScript -Content $content

    if ($testBlocks.Count -eq 0) {
        # No test directives found — treat entire script as a single unnamed test
        $testBlocks = @(@{
            testName    = $scriptName
            sql         = $content
            assertions  = @(@{ type = "expect-no-error" })
        })
    }

    $results = @()
    foreach ($block in $testBlocks) {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

        $testResult = @{
            scriptName   = $scriptName
            testName     = $block.testName
            status       = "pass"
            errorMessage = ""
            elapsed      = 0
        }

        try {
            $queryResult = Invoke-TdsQuery `
                -Port $Port `
                -Sql $block.sql `
                -TimeoutSeconds $TimeoutSeconds

            # Evaluate assertions
            foreach ($assertion in $block.assertions) {
                $assertionResult = Test-Assertion -Assertion $assertion -QueryResult $queryResult
                if (-not $assertionResult.passed) {
                    $testResult.status = "fail"
                    $testResult.errorMessage = $assertionResult.message
                    break
                }
            }
        }
        catch {
            # Check if any assertion is expect-no-error — if so, this is a failure
            $testResult.status = "fail"
            $testResult.errorMessage = $_.Exception.Message
        }

        $stopwatch.Stop()
        $testResult.elapsed = [math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        $results += $testResult
    }

    return $results
}

function Split-TestScript {
    <#
    .SYNOPSIS
        Splits a test script into individual test blocks based on '-- test:' directives.
    .OUTPUTS
        Array of hashtables: testName, sql, assertions[]
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Content
    )

    $blocks = @()
    $lines = $Content -split "`n"

    $currentTestName = $null
    $currentSql = @()
    $currentAssertions = @()

    foreach ($line in $lines) {
        $trimmedLine = $line.Trim()

        # Check for test directive
        if ($trimmedLine -match '^--\s*test:\s*(.+)$') {
            # Save previous block if exists
            if ($null -ne $currentTestName) {
                $blocks += @{
                    testName   = $currentTestName
                    sql        = ($currentSql -join "`n").Trim()
                    assertions = $currentAssertions
                }
            }

            # Start new block
            $currentTestName = $Matches[1].Trim()
            $currentSql = @()
            $currentAssertions = @()
            continue
        }

        # Check for assertion directives
        if ($trimmedLine -match '^--\s*expect-rows:\s*(.+)$') {
            $currentAssertions += @{
                type  = "expect-rows"
                value = $Matches[1].Trim()
            }
            continue
        }

        if ($trimmedLine -match '^--\s*expect-value:\s*(.+)$') {
            $currentAssertions += @{
                type  = "expect-value"
                value = $Matches[1].Trim()
            }
            continue
        }

        if ($trimmedLine -match '^--\s*expect-no-error\s*$') {
            $currentAssertions += @{
                type = "expect-no-error"
            }
            continue
        }

        # Regular SQL line (only collect if inside a test block)
        if ($null -ne $currentTestName) {
            $currentSql += $line
        }
    }

    # Save last block
    if ($null -ne $currentTestName) {
        $blocks += @{
            testName   = $currentTestName
            sql        = ($currentSql -join "`n").Trim()
            assertions = $currentAssertions
        }
    }

    # Ensure each block has at least one assertion (default: expect-no-error)
    foreach ($block in $blocks) {
        if ($block.assertions.Count -eq 0) {
            $block.assertions = @(@{ type = "expect-no-error" })
        }
    }

    return $blocks
}

function Invoke-TdsQuery {
    <#
    .SYNOPSIS
        Executes a T-SQL batch against PgPassthrough via SqlClient (TDS protocol).
    .OUTPUTS
        Hashtable with: rowCount, scalarValue, hasError, errorMessage
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$Port,

        [Parameter(Mandatory)]
        [string]$Sql,

        [int]$TimeoutSeconds = 30
    )

    $connectionString = "Server=127.0.0.1,$Port;Integrated Security=false;User ID=sa;Password=dummy;TrustServerCertificate=true"

    $result = @{
        rowCount     = 0
        scalarValue  = $null
        hasError     = $false
        errorMessage = ""
    }

    $connection = $null
    try {
        $connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
        $connection.Open()

        $command = $connection.CreateCommand()
        $command.CommandText = $Sql
        $command.CommandTimeout = $TimeoutSeconds

        $reader = $command.ExecuteReader()

        # Count rows and capture first scalar value
        $rowCount = 0
        $firstValue = $null
        while ($reader.Read()) {
            if ($rowCount -eq 0 -and $reader.FieldCount -gt 0) {
                $firstValue = $reader.GetValue(0)
            }
            $rowCount++
        }
        $reader.Close()

        $result.rowCount = $rowCount
        $result.scalarValue = $firstValue
    }
    catch {
        $result.hasError = $true
        $result.errorMessage = $_.Exception.Message
        throw
    }
    finally {
        if ($null -ne $connection -and $connection.State -eq 'Open') {
            $connection.Close()
        }
    }

    return $result
}

function Test-Assertion {
    <#
    .SYNOPSIS
        Evaluates a single assertion against a query result.
    .OUTPUTS
        Hashtable with: passed (bool), message (string)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Assertion,

        [Parameter(Mandatory)]
        [hashtable]$QueryResult
    )

    switch ($Assertion.type) {
        "expect-no-error" {
            if ($QueryResult.hasError) {
                return @{ passed = $false; message = "Expected no error but got: $($QueryResult.errorMessage)" }
            }
            return @{ passed = $true; message = "" }
        }

        "expect-rows" {
            $spec = $Assertion.value

            # Parse comparison: "> 0", "= 5", "5", ">= 3", "< 10"
            if ($spec -match '^\s*(>|>=|<|<=|=)\s*(\d+)\s*$') {
                $op = $Matches[1]
                $expected = [int]$Matches[2]
                $actual = $QueryResult.rowCount

                $passed = switch ($op) {
                    ">"  { $actual -gt $expected }
                    ">=" { $actual -ge $expected }
                    "<"  { $actual -lt $expected }
                    "<=" { $actual -le $expected }
                    "="  { $actual -eq $expected }
                }

                if (-not $passed) {
                    return @{ passed = $false; message = "Expected rows $op $expected but got $actual" }
                }
                return @{ passed = $true; message = "" }
            }
            elseif ($spec -match '^\s*(\d+)\s*$') {
                # Exact row count
                $expected = [int]$Matches[1]
                $actual = $QueryResult.rowCount
                if ($actual -ne $expected) {
                    return @{ passed = $false; message = "Expected $expected rows but got $actual" }
                }
                return @{ passed = $true; message = "" }
            }
            else {
                return @{ passed = $false; message = "Invalid expect-rows format: '$spec'" }
            }
        }

        "expect-value" {
            $expected = $Assertion.value
            $actual = if ($null -ne $QueryResult.scalarValue) { $QueryResult.scalarValue.ToString() } else { "" }

            if ($actual -ne $expected) {
                return @{ passed = $false; message = "Expected value '$expected' but got '$actual'" }
            }
            return @{ passed = $true; message = "" }
        }

        default {
            return @{ passed = $false; message = "Unknown assertion type: $($Assertion.type)" }
        }
    }
}

# Export module functions when loaded as a module
if ($MyInvocation.MyCommand.ScriptBlock.Module) {
    Export-ModuleMember -Function Invoke-FunctionalTests
}
