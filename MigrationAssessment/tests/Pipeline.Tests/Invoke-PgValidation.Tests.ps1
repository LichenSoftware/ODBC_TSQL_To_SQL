<#
.SYNOPSIS
    Pester unit tests for the PostgreSQL Validator module (Invoke-PgValidation).

.DESCRIPTION
    Tests dependency ordering (topological sort), circular dependency detection,
    timeout behavior, fallback mode selection, syntax-only validation, and per-object isolation.

.NOTES
    Requirements validated: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6
#>

# Dot-source the module under test
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. "$here/../../scripts/lib/Invoke-PgValidation.ps1"

Describe "Invoke-PgValidation - Dependency Ordering" {

    It "Orders objects so dependencies come before dependents" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "TableA"; objectType = "Table"; ddl = "CREATE TABLE TableA (id INTEGER PRIMARY KEY);"; dependencies = @() }
            [PSCustomObject]@{ objectName = "TableB"; objectType = "Table"; ddl = "CREATE TABLE TableB (id INTEGER PRIMARY KEY, a_id INTEGER);"; dependencies = @("TableA") }
            [PSCustomObject]@{ objectName = "ViewC"; objectType = "View"; ddl = "CREATE VIEW ViewC AS SELECT 1 AS col;"; dependencies = @("TableB") }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results | ForEach-Object { $_.status | Should Be "pass" }

        $indexA = [array]::IndexOf($results.objectName, "TableA")
        $indexB = [array]::IndexOf($results.objectName, "TableB")
        $indexC = [array]::IndexOf($results.objectName, "ViewC")

        $indexA | Should BeLessThan $indexB
        $indexB | Should BeLessThan $indexC
    }

    It "Handles objects with no dependencies" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "IndependentA"; objectType = "Table"; ddl = "CREATE TABLE IndependentA (id INTEGER PRIMARY KEY);"; dependencies = @() }
            [PSCustomObject]@{ objectName = "IndependentB"; objectType = "Table"; ddl = "CREATE TABLE IndependentB (name VARCHAR(50));"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results.Count | Should Be 2
        $results | ForEach-Object { $_.status | Should Be "pass" }
    }

    It "Handles diamond dependency graph correctly" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "A"; objectType = "Table"; ddl = "CREATE TABLE A (id INTEGER PRIMARY KEY);"; dependencies = @() }
            [PSCustomObject]@{ objectName = "B"; objectType = "Table"; ddl = "CREATE TABLE B (id INTEGER PRIMARY KEY);"; dependencies = @("A") }
            [PSCustomObject]@{ objectName = "C"; objectType = "Table"; ddl = "CREATE TABLE C (id INTEGER PRIMARY KEY);"; dependencies = @("A") }
            [PSCustomObject]@{ objectName = "D"; objectType = "View"; ddl = "CREATE VIEW D AS SELECT 1 AS col;"; dependencies = @("B", "C") }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $indexA = [array]::IndexOf($results.objectName, "A")
        $indexB = [array]::IndexOf($results.objectName, "B")
        $indexC = [array]::IndexOf($results.objectName, "C")
        $indexD = [array]::IndexOf($results.objectName, "D")

        $indexA | Should BeLessThan $indexB
        $indexA | Should BeLessThan $indexC
        $indexB | Should BeLessThan $indexD
        $indexC | Should BeLessThan $indexD
    }
}

Describe "Invoke-PgValidation - Circular Dependency Detection" {

    It "Marks all objects in A->B->C->A cycle as fail-syntax with circular dependency message" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "ObjA"; objectType = "View"; ddl = "CREATE VIEW ObjA AS SELECT 1 AS col;"; dependencies = @("ObjC") }
            [PSCustomObject]@{ objectName = "ObjB"; objectType = "View"; ddl = "CREATE VIEW ObjB AS SELECT 1 AS col;"; dependencies = @("ObjA") }
            [PSCustomObject]@{ objectName = "ObjC"; objectType = "View"; ddl = "CREATE VIEW ObjC AS SELECT 1 AS col;"; dependencies = @("ObjB") }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results.Count | Should Be 3
        foreach ($r in $results) {
            $r.status | Should Be "fail-syntax"
            $r.errorMessage | Should Match "circular dependency"
        }
    }

    It "Marks only cycle members as fail-syntax, non-cycle objects pass normally" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "Independent"; objectType = "Table"; ddl = "CREATE TABLE Independent (id INTEGER PRIMARY KEY);"; dependencies = @() }
            [PSCustomObject]@{ objectName = "CycleX"; objectType = "View"; ddl = "CREATE VIEW CycleX AS SELECT 1 AS col;"; dependencies = @("CycleY") }
            [PSCustomObject]@{ objectName = "CycleY"; objectType = "View"; ddl = "CREATE VIEW CycleY AS SELECT 1 AS col;"; dependencies = @("CycleX") }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results.Count | Should Be 3

        $independentResult = $results | Where-Object { $_.objectName -eq "Independent" }
        $independentResult.status | Should Be "pass"

        $cycleXResult = $results | Where-Object { $_.objectName -eq "CycleX" }
        $cycleXResult.status | Should Be "fail-syntax"
        $cycleXResult.errorMessage | Should Match "circular dependency"

        $cycleYResult = $results | Where-Object { $_.objectName -eq "CycleY" }
        $cycleYResult.status | Should Be "fail-syntax"
        $cycleYResult.errorMessage | Should Match "circular dependency"
    }

    It "Non-cycle objects in a graph with cycles are still validated normally" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "Independent"; objectType = "Table"; ddl = "CREATE TABLE Independent (id INTEGER PRIMARY KEY);"; dependencies = @() }
            [PSCustomObject]@{ objectName = "DependsOnInd"; objectType = "View"; ddl = "CREATE VIEW DependsOnInd AS SELECT 1 AS col;"; dependencies = @("Independent") }
            [PSCustomObject]@{ objectName = "CycleA"; objectType = "View"; ddl = "CREATE VIEW CycleA AS SELECT 1 AS col;"; dependencies = @("CycleB") }
            [PSCustomObject]@{ objectName = "CycleB"; objectType = "View"; ddl = "CREATE VIEW CycleB AS SELECT 1 AS col;"; dependencies = @("CycleA") }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $indResult = $results | Where-Object { $_.objectName -eq "Independent" }
        $indResult.status | Should Be "pass"

        $depResult = $results | Where-Object { $_.objectName -eq "DependsOnInd" }
        $depResult.status | Should Be "pass"

        $cycleAResult = $results | Where-Object { $_.objectName -eq "CycleA" }
        $cycleAResult.status | Should Be "fail-syntax"

        $cycleBResult = $results | Where-Object { $_.objectName -eq "CycleB" }
        $cycleBResult.status | Should Be "fail-syntax"
    }
}

Describe "Invoke-PgValidation - Syntax-Only Fallback Mode" {

    It "Uses syntax-only mode when no PgConnectionString is provided" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "TestTable"; objectType = "Table"; ddl = "CREATE TABLE TestTable (id INTEGER PRIMARY KEY);"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results[0].validationMode | Should Be "syntax-only"
    }

    It "Uses syntax-only mode when PgConnectionString is empty" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "TestTable"; objectType = "Table"; ddl = "CREATE TABLE TestTable (id INTEGER PRIMARY KEY);"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl -PgConnectionString ""

        $results[0].validationMode | Should Be "syntax-only"
    }
}

Describe "Invoke-PgValidation - Valid DDL Passes Syntax-Only" {

    It "Passes valid CREATE TABLE DDL" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "ValidTable"; objectType = "Table"; ddl = "CREATE TABLE test (id INTEGER PRIMARY KEY);"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results[0].status | Should Be "pass"
        $results[0].errorMessage | Should BeNullOrEmpty
    }

    It "Passes valid CREATE VIEW DDL" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "ValidView"; objectType = "View"; ddl = "CREATE VIEW my_view AS SELECT 1 AS col;"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results[0].status | Should Be "pass"
    }

    It "Passes valid CREATE FUNCTION DDL" {
        $funcDdl = @"
CREATE OR REPLACE FUNCTION my_func() RETURNS INTEGER AS `$`$
BEGIN
    RETURN 1;
END;
`$`$ LANGUAGE plpgsql;
"@
        $ddl = @(
            [PSCustomObject]@{ objectName = "ValidFunc"; objectType = "Function"; ddl = $funcDdl; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results[0].status | Should Be "pass"
    }
}

Describe "Invoke-PgValidation - Invalid DDL Fails" {

    It "Fails empty DDL" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "EmptyObj"; objectType = "Table"; ddl = ""; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results[0].status | Should Be "fail-syntax"
        $results[0].errorMessage | Should Match "empty"
    }

    It "Fails whitespace-only DDL" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "WhitespaceObj"; objectType = "Table"; ddl = "   "; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results[0].status | Should Be "fail-syntax"
        $results[0].errorMessage | Should Match "empty"
    }

    It "Fails DDL containing T-SQL NVARCHAR data type" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "NvarcharObj"; objectType = "Table"; ddl = "CREATE TABLE bad_table (name NVARCHAR(100));"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results[0].status | Should Be "fail-syntax"
        $results[0].errorMessage | Should Match "NVARCHAR"
    }

    It "Fails DDL containing T-SQL IDENTITY column" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "IdentityObj"; objectType = "Table"; ddl = "CREATE TABLE bad_table (id INT IDENTITY(1,1) PRIMARY KEY);"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results[0].status | Should Be "fail-syntax"
        $results[0].errorMessage | Should Match "IDENTITY"
    }

    It "Fails DDL with unbalanced parentheses" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "UnbalancedObj"; objectType = "Table"; ddl = "CREATE TABLE bad_table (id INTEGER PRIMARY KEY;"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results[0].status | Should Be "fail-syntax"
        $results[0].errorMessage | Should Match "parenthes"
    }
}

Describe "Invoke-PgValidation - Per-Object Isolation" {

    It "One object failure does not affect another object result" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "GoodTable"; objectType = "Table"; ddl = "CREATE TABLE good_table (id INTEGER PRIMARY KEY);"; dependencies = @() }
            [PSCustomObject]@{ objectName = "BadTable"; objectType = "Table"; ddl = "CREATE TABLE bad_table (name NVARCHAR(100));"; dependencies = @() }
            [PSCustomObject]@{ objectName = "AnotherGood"; objectType = "View"; ddl = "CREATE VIEW another_good AS SELECT 1 AS val;"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results.Count | Should Be 3

        $goodResult = $results | Where-Object { $_.objectName -eq "GoodTable" }
        $goodResult.status | Should Be "pass"

        $badResult = $results | Where-Object { $_.objectName -eq "BadTable" }
        $badResult.status | Should Be "fail-syntax"

        $anotherGoodResult = $results | Where-Object { $_.objectName -eq "AnotherGood" }
        $anotherGoodResult.status | Should Be "pass"
    }

    It "Returns a result for every input object" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "Obj1"; objectType = "Table"; ddl = "CREATE TABLE obj1 (id INTEGER);"; dependencies = @() }
            [PSCustomObject]@{ objectName = "Obj2"; objectType = "Table"; ddl = ""; dependencies = @() }
            [PSCustomObject]@{ objectName = "Obj3"; objectType = "View"; ddl = "CREATE VIEW obj3 AS SELECT 1 AS col;"; dependencies = @() }
            [PSCustomObject]@{ objectName = "Obj4"; objectType = "Table"; ddl = "CREATE TABLE obj4 (val NVARCHAR(50));"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl

        $results.Count | Should Be 4
        ($results | Where-Object { $_.objectName -eq "Obj1" }).status | Should Be "pass"
        ($results | Where-Object { $_.objectName -eq "Obj2" }).status | Should Be "fail-syntax"
        ($results | Where-Object { $_.objectName -eq "Obj3" }).status | Should Be "pass"
        ($results | Where-Object { $_.objectName -eq "Obj4" }).status | Should Be "fail-syntax"
    }
}

Describe "Invoke-PgValidation - Timeout Behavior" {

    It "Accepts custom TimeoutSeconds parameter without error" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "TimeoutTest"; objectType = "Table"; ddl = "CREATE TABLE timeout_test (id INTEGER PRIMARY KEY);"; dependencies = @() }
        )

        $results = Invoke-PgValidation -DdlStatements $ddl -TimeoutSeconds 5

        $results[0].status | Should Be "pass"
        $results[0].objectName | Should Be "TimeoutTest"
    }

    It "Defaults TimeoutSeconds to 30 when not specified" {
        $ddl = @(
            [PSCustomObject]@{ objectName = "DefaultTimeout"; objectType = "Table"; ddl = "CREATE TABLE default_timeout (id INTEGER);"; dependencies = @() }
        )

        { Invoke-PgValidation -DdlStatements $ddl } | Should Not Throw
    }
}
