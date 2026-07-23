BeforeAll {
    # Dot-source the module under test
    . "$PSScriptRoot/../../scripts/lib/Invoke-DiagnosticsClassification.ps1"
}

Describe 'Invoke-DiagnosticsClassification' {

    Context 'Type mapping gap classification' {
        It 'classifies "type does not exist" as type mapping gap' {
            $failures = @(
                @{
                    objectName      = 'dbo.MyTable'
                    objectType      = 'Table'
                    status          = 'fail-syntax'
                    errorMessage    = 'type "hierarchyid" does not exist'
                    errorLineNumber = 3
                    generatedDdl    = 'CREATE TABLE dbo.MyTable (id INT, col hierarchyid);'
                }
            )

            $result = Invoke-DiagnosticsClassification -FailedObjects $failures

            $result | Should -HaveCount 1
            $result[0].category | Should -Be 'type mapping gap'
            $result[0].count | Should -Be 1
            $result[0].objects | Should -Contain 'dbo.MyTable'
        }
    }

    Context 'Function mapping gap classification' {
        It 'classifies "function does not exist" as function mapping gap' {
            $failures = @(
                @{
                    objectName      = 'dbo.sp_Calculate'
                    objectType      = 'StoredProcedure'
                    status          = 'fail-syntax'
                    errorMessage    = 'function "ISNULL" does not exist'
                    errorLineNumber = 10
                    generatedDdl    = 'CREATE FUNCTION dbo.sp_Calculate() RETURNS INT AS $$ SELECT ISNULL(1, 0); $$ LANGUAGE sql;'
                }
            )

            $result = Invoke-DiagnosticsClassification -FailedObjects $failures

            $result | Should -HaveCount 1
            $result[0].category | Should -Be 'function mapping gap'
            $result[0].count | Should -Be 1
            $result[0].objects | Should -Contain 'dbo.sp_Calculate'
        }
    }

    Context 'Procedural pattern not handled classification' {
        It 'classifies DECLARE syntax error with PL/pgSQL DDL as procedural pattern' {
            $failures = @(
                @{
                    objectName      = 'dbo.sp_ProcessOrder'
                    objectType      = 'StoredProcedure'
                    status          = 'fail-syntax'
                    errorMessage    = 'syntax error at or near "DECLARE"'
                    errorLineNumber = 7
                    generatedDdl    = 'CREATE OR REPLACE FUNCTION dbo.sp_ProcessOrder() RETURNS void AS $$ BEGIN DECLARE v_count INT; END; $$ LANGUAGE plpgsql;'
                }
            )

            $result = Invoke-DiagnosticsClassification -FailedObjects $failures

            $result | Should -HaveCount 1
            $result[0].category | Should -Be 'procedural pattern not handled'
            $result[0].count | Should -Be 1
            $result[0].objects | Should -Contain 'dbo.sp_ProcessOrder'
        }
    }

    Context 'AI prompt deficiency classification' {
        It 'classifies empty DDL as AI prompt deficiency' {
            $failures = @(
                @{
                    objectName      = 'dbo.sp_Missing'
                    objectType      = 'StoredProcedure'
                    status          = 'fail-convert'
                    errorMessage    = 'empty output from conversion'
                    errorLineNumber = $null
                    generatedDdl    = ''
                }
            )

            $result = Invoke-DiagnosticsClassification -FailedObjects $failures

            $result | Should -HaveCount 1
            $result[0].category | Should -Be 'AI prompt deficiency'
            $result[0].count | Should -Be 1
            $result[0].objects | Should -Contain 'dbo.sp_Missing'
        }
    }

    Context 'Dependency resolution failure classification' {
        It 'classifies "relation does not exist" as dependency resolution failure' {
            $failures = @(
                @{
                    objectName      = 'dbo.vw_OrderSummary'
                    objectType      = 'View'
                    status          = 'fail-syntax'
                    errorMessage    = 'relation "dbo.Orders" does not exist'
                    errorLineNumber = 2
                    generatedDdl    = 'CREATE VIEW dbo.vw_OrderSummary AS SELECT * FROM dbo.Orders;'
                }
            )

            $result = Invoke-DiagnosticsClassification -FailedObjects $failures

            $result | Should -HaveCount 1
            $result[0].category | Should -Be 'dependency resolution failure'
            $result[0].count | Should -Be 1
            $result[0].objects | Should -Contain 'dbo.vw_OrderSummary'
        }
    }

    Context 'Ranking by failure count' {
        It 'ranks categories with more failures first' {
            $failures = @(
                # 3 type mapping gaps
                @{ objectName = 'dbo.T1'; objectType = 'Table'; status = 'fail-syntax'; errorMessage = 'type "geography" does not exist'; errorLineNumber = 1; generatedDdl = 'CREATE TABLE t1 (col geography);' }
                @{ objectName = 'dbo.T2'; objectType = 'Table'; status = 'fail-syntax'; errorMessage = 'type "hierarchyid" does not exist'; errorLineNumber = 1; generatedDdl = 'CREATE TABLE t2 (col hierarchyid);' }
                @{ objectName = 'dbo.T3'; objectType = 'Table'; status = 'fail-syntax'; errorMessage = 'type "xml" does not exist'; errorLineNumber = 1; generatedDdl = 'CREATE TABLE t3 (col xml);' }
                # 1 function mapping gap
                @{ objectName = 'dbo.F1'; objectType = 'Function'; status = 'fail-syntax'; errorMessage = 'function "ISNULL" does not exist'; errorLineNumber = 1; generatedDdl = 'SELECT ISNULL(1,0);' }
                # 2 dependency resolution failures
                @{ objectName = 'dbo.V1'; objectType = 'View'; status = 'fail-syntax'; errorMessage = 'relation "dbo.Missing1" does not exist'; errorLineNumber = 1; generatedDdl = 'SELECT * FROM dbo.Missing1;' }
                @{ objectName = 'dbo.V2'; objectType = 'View'; status = 'fail-syntax'; errorMessage = 'relation "dbo.Missing2" does not exist'; errorLineNumber = 1; generatedDdl = 'SELECT * FROM dbo.Missing2;' }
            )

            $result = Invoke-DiagnosticsClassification -FailedObjects $failures

            # Should be sorted by count descending: type mapping gap (3), dependency (2), function (1)
            $result[0].category | Should -Be 'type mapping gap'
            $result[0].count | Should -Be 3
            $result[1].category | Should -Be 'dependency resolution failure'
            $result[1].count | Should -Be 2
            $result[2].category | Should -Be 'function mapping gap'
            $result[2].count | Should -Be 1
        }
    }

    Context 'Object names listed per category' {
        It 'correctly lists affected object names for each category' {
            $failures = @(
                @{ objectName = 'dbo.TableA'; objectType = 'Table'; status = 'fail-syntax'; errorMessage = 'type "geometry" does not exist'; errorLineNumber = 1; generatedDdl = 'CREATE TABLE dbo.TableA (col geometry);' }
                @{ objectName = 'dbo.TableB'; objectType = 'Table'; status = 'fail-syntax'; errorMessage = 'type "sysname" does not exist'; errorLineNumber = 2; generatedDdl = 'CREATE TABLE dbo.TableB (col sysname);' }
                @{ objectName = 'dbo.ProcC'; objectType = 'StoredProcedure'; status = 'fail-syntax'; errorMessage = 'function "GETDATE" does not exist'; errorLineNumber = 5; generatedDdl = 'SELECT GETDATE();' }
            )

            $result = Invoke-DiagnosticsClassification -FailedObjects $failures

            $typeCategory = $result | Where-Object { $_.category -eq 'type mapping gap' }
            $typeCategory.objects | Should -HaveCount 2
            $typeCategory.objects | Should -Contain 'dbo.TableA'
            $typeCategory.objects | Should -Contain 'dbo.TableB'

            $funcCategory = $result | Where-Object { $_.category -eq 'function mapping gap' }
            $funcCategory.objects | Should -HaveCount 1
            $funcCategory.objects | Should -Contain 'dbo.ProcC'
        }
    }

    Context 'Empty input' {
        It 'returns empty array for empty input' {
            $result = Invoke-DiagnosticsClassification -FailedObjects @()

            $result | Should -HaveCount 0
        }
    }
}
