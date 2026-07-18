namespace MigrationValidation.Runner;

/// <summary>
/// Defines a single validation test to execute against the database.
/// </summary>
public class ValidationTest
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Sql { get; init; }
    public bool IsStoredProcedure { get; init; }
    public bool ExpectsResults { get; init; } = true;
    public int? MinExpectedRows { get; init; }
    public List<TestParameter> Parameters { get; init; } = [];
}

/// <summary>
/// A parameter to pass to a stored procedure or parameterized query.
/// </summary>
public class TestParameter
{
    public required string Key { get; init; }
    public object? Value { get; init; }
    public bool IsOutput { get; init; }
    public System.Data.DbType DbType { get; init; } = System.Data.DbType.Int32;
    public int Size { get; init; }
}

/// <summary>
/// The result of executing a single validation test.
/// </summary>
public class TestResult
{
    public required string TestName { get; init; }
    public bool Passed { get; init; }
    public long ElapsedMs { get; init; }
    public int? RowCount { get; init; }
    public string? ErrorMessage { get; init; }
}
