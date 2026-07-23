BeforeAll {
    . "$PSScriptRoot/../../scripts/lib/Invoke-Scoring.ps1"
}

Describe 'Invoke-Scoring' {

    Context 'Basic scoring formula' {
        It 'computes 70.0% when 7 pass, 2 fail-syntax, 1 fail-convert' {
            $objects = @()
            1..7 | ForEach-Object { $objects += [PSCustomObject]@{ objectName = "obj$_"; objectType = 'Table'; databaseName = 'TestDB'; status = 'pass' } }
            $objects += [PSCustomObject]@{ objectName = 'obj8'; objectType = 'Table'; databaseName = 'TestDB'; status = 'fail-syntax' }
            $objects += [PSCustomObject]@{ objectName = 'obj9'; objectType = 'Table'; databaseName = 'TestDB'; status = 'fail-syntax' }
            $objects += [PSCustomObject]@{ objectName = 'obj10'; objectType = 'Table'; databaseName = 'TestDB'; status = 'fail-convert' }

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores @{}

            $result.databases['TestDB'].compatibilityScore | Should -Be 70.0
        }

        It 'computes 100% when all objects pass' {
            $objects = @(1..5 | ForEach-Object {
                [PSCustomObject]@{ objectName = "obj$_"; objectType = 'View'; databaseName = 'AllPassDB'; status = 'pass' }
            })

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores @{}

            $result.databases['AllPassDB'].compatibilityScore | Should -Be 100.0
        }

        It 'computes 0% when all objects fail' {
            $objects = @(
                [PSCustomObject]@{ objectName = 'obj1'; objectType = 'StoredProcedure'; databaseName = 'AllFailDB'; status = 'fail-syntax' }
                [PSCustomObject]@{ objectName = 'obj2'; objectType = 'StoredProcedure'; databaseName = 'AllFailDB'; status = 'fail-convert' }
                [PSCustomObject]@{ objectName = 'obj3'; objectType = 'Function'; databaseName = 'AllFailDB'; status = 'fail-syntax' }
            )

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores @{}

            $result.databases['AllFailDB'].compatibilityScore | Should -Be 0.0
        }
    }

    Context 'N/A handling for all-skip databases' {
        It 'reports N/A when all objects are skip' {
            $objects = @(
                [PSCustomObject]@{ objectName = 'obj1'; objectType = 'Table'; databaseName = 'SkipDB'; status = 'skip' }
                [PSCustomObject]@{ objectName = 'obj2'; objectType = 'View'; databaseName = 'SkipDB'; status = 'skip' }
                [PSCustomObject]@{ objectName = 'obj3'; objectType = 'Function'; databaseName = 'SkipDB'; status = 'skip' }
            )

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores @{}

            $result.databases['SkipDB'].compatibilityScore | Should -Be 'N/A'
        }

        It 'excludes N/A databases from aggregate score' {
            $objects = @(
                # SkipDB: all skip
                [PSCustomObject]@{ objectName = 'obj1'; objectType = 'Table'; databaseName = 'SkipDB'; status = 'skip' }
                [PSCustomObject]@{ objectName = 'obj2'; objectType = 'View'; databaseName = 'SkipDB'; status = 'skip' }
                # RealDB: 4 pass, 1 fail
                [PSCustomObject]@{ objectName = 'obj3'; objectType = 'Table'; databaseName = 'RealDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'obj4'; objectType = 'Table'; databaseName = 'RealDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'obj5'; objectType = 'View'; databaseName = 'RealDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'obj6'; objectType = 'View'; databaseName = 'RealDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'obj7'; objectType = 'Function'; databaseName = 'RealDB'; status = 'fail-syntax' }
            )

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores @{}

            $result.databases['SkipDB'].compatibilityScore | Should -Be 'N/A'
            $result.aggregate.compatibilityScore | Should -Be 80.0
        }
    }

    Context 'Per-type breakdown correctness' {
        It 'computes correct per-type pass/fail counts and scores' {
            $objects = @(
                # Tables: 3 pass, 1 fail = 75%
                [PSCustomObject]@{ objectName = 't1'; objectType = 'Table'; databaseName = 'TypeDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 't2'; objectType = 'Table'; databaseName = 'TypeDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 't3'; objectType = 'Table'; databaseName = 'TypeDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 't4'; objectType = 'Table'; databaseName = 'TypeDB'; status = 'fail-syntax' }
                # Views: 1 pass, 1 fail = 50%
                [PSCustomObject]@{ objectName = 'v1'; objectType = 'View'; databaseName = 'TypeDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'v2'; objectType = 'View'; databaseName = 'TypeDB'; status = 'fail-convert' }
                # StoredProcedure: 2 pass, 0 fail = 100%
                [PSCustomObject]@{ objectName = 'sp1'; objectType = 'StoredProcedure'; databaseName = 'TypeDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'sp2'; objectType = 'StoredProcedure'; databaseName = 'TypeDB'; status = 'pass' }
            )

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores @{}
            $byType = $result.databases['TypeDB'].byType

            $byType['Table'].pass | Should -Be 3
            $byType['Table'].fail | Should -Be 1
            $byType['Table'].score | Should -Be 75.0

            $byType['View'].pass | Should -Be 1
            $byType['View'].fail | Should -Be 1
            $byType['View'].score | Should -Be 50.0

            $byType['StoredProcedure'].pass | Should -Be 2
            $byType['StoredProcedure'].fail | Should -Be 0
            $byType['StoredProcedure'].score | Should -Be 100.0
        }
    }

    Context 'Aggregate score across multiple databases' {
        It 'computes aggregate from all non-N/A databases combined' {
            $objects = @(
                # DB1: 3 pass, 2 fail => 60%
                [PSCustomObject]@{ objectName = 'a1'; objectType = 'Table'; databaseName = 'DB1'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'a2'; objectType = 'Table'; databaseName = 'DB1'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'a3'; objectType = 'View'; databaseName = 'DB1'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'a4'; objectType = 'View'; databaseName = 'DB1'; status = 'fail-syntax' }
                [PSCustomObject]@{ objectName = 'a5'; objectType = 'Function'; databaseName = 'DB1'; status = 'fail-convert' }
                # DB2: 7 pass, 3 fail => 70%
                [PSCustomObject]@{ objectName = 'b1'; objectType = 'Table'; databaseName = 'DB2'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'b2'; objectType = 'Table'; databaseName = 'DB2'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'b3'; objectType = 'Table'; databaseName = 'DB2'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'b4'; objectType = 'View'; databaseName = 'DB2'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'b5'; objectType = 'View'; databaseName = 'DB2'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'b6'; objectType = 'StoredProcedure'; databaseName = 'DB2'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'b7'; objectType = 'StoredProcedure'; databaseName = 'DB2'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'b8'; objectType = 'Function'; databaseName = 'DB2'; status = 'fail-syntax' }
                [PSCustomObject]@{ objectName = 'b9'; objectType = 'Trigger'; databaseName = 'DB2'; status = 'fail-syntax' }
                [PSCustomObject]@{ objectName = 'b10'; objectType = 'Trigger'; databaseName = 'DB2'; status = 'fail-convert' }
            )

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores @{}

            # Aggregate: 10 pass out of 15 convertible = 66.7%
            $result.aggregate.compatibilityScore | Should -Be 66.7
            $result.aggregate.totalPass | Should -Be 10
            $result.aggregate.totalFailSyntax | Should -Be 3
            $result.aggregate.totalFailConvert | Should -Be 2
        }
    }

    Context 'Delta computation' {
        It 'computes delta as current minus previous when both are numeric' {
            $objects = @(
                [PSCustomObject]@{ objectName = 'obj1'; objectType = 'Table'; databaseName = 'DeltaDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'obj2'; objectType = 'Table'; databaseName = 'DeltaDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'obj3'; objectType = 'Table'; databaseName = 'DeltaDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'obj4'; objectType = 'Table'; databaseName = 'DeltaDB'; status = 'fail-syntax' }
            )
            # Current score will be 75.0, previous was 60.0 => delta = 15.0
            $previous = @{ 'DeltaDB' = 60.0 }

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores $previous

            $result.databases['DeltaDB'].compatibilityScore | Should -Be 75.0
            $result.databases['DeltaDB'].delta | Should -Be 15.0
        }

        It 'returns null delta when no previous score exists' {
            $objects = @(
                [PSCustomObject]@{ objectName = 'obj1'; objectType = 'Table'; databaseName = 'NewDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'obj2'; objectType = 'Table'; databaseName = 'NewDB'; status = 'fail-syntax' }
            )

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores @{}

            $result.databases['NewDB'].delta | Should -BeNullOrEmpty
        }

        It 'returns negative delta when score decreases' {
            $objects = @(
                [PSCustomObject]@{ objectName = 'obj1'; objectType = 'Table'; databaseName = 'RegDB'; status = 'pass' }
                [PSCustomObject]@{ objectName = 'obj2'; objectType = 'Table'; databaseName = 'RegDB'; status = 'fail-syntax' }
                [PSCustomObject]@{ objectName = 'obj3'; objectType = 'Table'; databaseName = 'RegDB'; status = 'fail-syntax' }
                [PSCustomObject]@{ objectName = 'obj4'; objectType = 'Table'; databaseName = 'RegDB'; status = 'fail-syntax' }
            )
            # Current score will be 25.0, previous was 80.5 => delta = -55.5
            $previous = @{ 'RegDB' = 80.5 }

            $result = Invoke-Scoring -ObjectResults $objects -PreviousScores $previous

            $result.databases['RegDB'].compatibilityScore | Should -Be 25.0
            $result.databases['RegDB'].delta | Should -Be -55.5
        }
    }
}
