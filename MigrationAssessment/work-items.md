# Migration Work Items Report

**Generated:** 2026-06-15T20:19:39.9458244+00:00
**Source:** ./test-assessment.json
**Total Work Items:** 11
**Estimated Effort:** 63.97-316.8 hours

## Risk Distribution

| Priority | Count |
|----------|-------|
| Critical | 2 |
| High | 2 |
| Medium | 4 |
| Low | 3 |

## Table of Contents

- [Critical Priority](#critical-priority)
- [High Priority](#high-priority)
- [Medium Priority](#medium-priority)
- [Low Priority](#low-priority)

## Critical Priority

### WI-009: [Risk 4] Convert GLOBAL_TEMP_TABLE in Ad Hoc Queries

**Description:** The SQL Server feature 'GLOBAL_TEMP_TABLE' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 2 occurrences across the analyzed codebase with a combined total of 2 executions recorded.
This represents a low business impact based on execution frequency.
Risk Level: 4 (requires design pattern changes for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
INSERT INTO ##GlobalOrderSummary
    SELECT
        CAST(OrderDate AS DATE),
        COUNT(*),
        SUM(TotalAmount)
    FROM dbo.Orders
    GROUP BY CAST(OrderDate AS DATE)
```

**PostgreSQL Equivalent:**
```sql
Unlogged tables or application-managed shared state
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 2 statements

**Acceptance Criteria:**
1. All instances of GLOBAL_TEMP_TABLE usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

### WI-008: [Risk 3] Convert TEMP_TABLE in Ad Hoc Queries

**Description:** The SQL Server feature 'TEMP_TABLE' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 2 occurrences across the analyzed codebase with a combined total of 2 executions recorded.
This represents a low business impact based on execution frequency.
Risk Level: 3 (requires procedural logic changes for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
(@Year int,@Month int)INSERT INTO #MonthlyStats
    SELECT
        o.CustomerID,
        COUNT(*),
        SUM(o.TotalAmount)
    FROM dbo.Orders o
    WHERE YEAR(o.OrderDate) = @Year AND MONTH(o.OrderDate) = @Month
    GROUP BY o.CustomerID
```

**PostgreSQL Equivalent:**
```sql
CREATE TEMPORARY TABLE (session-scoped by default)
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 2 statements

**Acceptance Criteria:**
1. All instances of TEMP_TABLE usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.

## High Priority

### WI-001: [Risk 5] Convert XML_METHOD in Ad Hoc Queries

**Description:** The SQL Server feature 'XML_METHOD' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 5 (requires architectural redesign or alternative technology for PostgreSQL migration).

**SQL Server Pattern:**
```sql
(@OrderID int)SELECT
        o.OrderID,
        m.MetadataXml.value('(/order/shipping/address)[1]', 'NVARCHAR(500)') AS ShippingAddress,
        m.MetadataXml.value('(/order/shipping/method)[1]', 'NVARCHAR(100)') AS ShippingMethod,
        m.MetadataXml.query('/order/items') AS ItemsXml
    FROM dbo.Orders o
    INNER JOIN dbo.OrderMetadata m ON o.OrderID = m.OrderID
    WHERE o.OrderID = @OrderID
```

**PostgreSQL Equivalent:**
```sql
xpath(), xmltable(), xmlparse()
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of XML_METHOD usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The alternative architecture has been reviewed and approved by the team.
5. Integration tests confirm the replacement solution interoperates correctly with dependent systems.

### WI-002: [Risk 4] Convert UPDLOCK in Ad Hoc Queries

**Description:** The SQL Server feature 'UPDLOCK' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 4 (requires design pattern changes for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
(@ProductID int,@NewQuantity int)UPDATE p
    SET p.StockQuantity = @NewQuantity
    FROM dbo.Products p WITH (UPDLOCK, ROWLOCK)
    WHERE p.ProductID = @ProductID
```

**PostgreSQL Equivalent:**
```sql
SELECT ... FOR UPDATE
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of UPDLOCK usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

## Medium Priority

### WI-003: [Risk 4] Convert ROWLOCK in Ad Hoc Queries

**Description:** The SQL Server feature 'ROWLOCK' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 4 (requires design pattern changes for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
(@ProductID int,@NewQuantity int)UPDATE p
    SET p.StockQuantity = @NewQuantity
    FROM dbo.Products p WITH (UPDLOCK, ROWLOCK)
    WHERE p.ProductID = @ProductID
```

**PostgreSQL Equivalent:**
```sql
PostgreSQL MVCC (row-level locking is default)
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of ROWLOCK usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

### WI-010: [Risk 4] Convert MERGE in Ad Hoc Queries

**Description:** The SQL Server feature 'MERGE' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 4 (requires design pattern changes for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
MERGE dbo.Products AS target
    USING dbo.ProductImportStaging AS source
    ON target.SKU = source.SKU
    WHEN MATCHED THEN
        UPDATE SET
            ProductName = source.ProductName,
            Price = source.Price,
            StockQuantity = source.StockQuantity
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ProductName, SKU, Price, StockQuantity)
        VALUES (source.ProductName, source.SKU, source.Price, source.StockQuantity)
    WHEN NOT MATCHED BY SOURCE THEN
 
```

**PostgreSQL Equivalent:**
```sql
INSERT ... ON CONFLICT (UPSERT) or MERGE (PostgreSQL 15+)
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of MERGE usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

### WI-011: [Risk 4] Convert NOLOCK in Ad Hoc Queries

**Description:** The SQL Server feature 'NOLOCK' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 4 (requires design pattern changes for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
SELECT
        p.ProductID,
        p.ProductName,
        p.StockQuantity,
        p.Price
    FROM dbo.Products p WITH (NOLOCK)
    WHERE p.StockQuantity > 0
    ORDER BY p.StockQuantity ASC
```

**PostgreSQL Equivalent:**
```sql
PostgreSQL MVCC (no locking hints needed)
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of NOLOCK usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

### WI-004: [Risk 2] Convert TOP in Ad Hoc Queries

**Description:** The SQL Server feature 'TOP' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 2 (simple syntax substitution for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
(@TopN int)SELECT TOP (@TopN)
        c.CustomerID,
        c.FirstName + ' ' + c.LastName AS FullName,
        COUNT(o.OrderID) AS OrderCount,
        ISNULL(SUM(o.TotalAmount), 0) AS TotalSpent,
        DATEDIFF(DAY, MAX(o.OrderDate), GETDATE()) AS DaysSinceLastOrder
    FROM dbo.Customers c
    LEFT JOIN dbo.Orders o ON c.CustomerID = o.CustomerID
    GROUP BY c.CustomerID, c.FirstName, c.LastName
    ORDER BY TotalSpent DESC
```

**PostgreSQL Equivalent:**
```sql
LIMIT / OFFSET
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of TOP usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.

## Low Priority

### WI-005: [Risk 2] Convert ISNULL in Ad Hoc Queries

**Description:** The SQL Server feature 'ISNULL' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 2 (simple syntax substitution for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
(@TopN int)SELECT TOP (@TopN)
        c.CustomerID,
        c.FirstName + ' ' + c.LastName AS FullName,
        COUNT(o.OrderID) AS OrderCount,
        ISNULL(SUM(o.TotalAmount), 0) AS TotalSpent,
        DATEDIFF(DAY, MAX(o.OrderDate), GETDATE()) AS DaysSinceLastOrder
    FROM dbo.Customers c
    LEFT JOIN dbo.Orders o ON c.CustomerID = o.CustomerID
    GROUP BY c.CustomerID, c.FirstName, c.LastName
    ORDER BY TotalSpent DESC
```

**PostgreSQL Equivalent:**
```sql
COALESCE
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of ISNULL usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.

### WI-006: [Risk 2] Convert DATEDIFF in Ad Hoc Queries

**Description:** The SQL Server feature 'DATEDIFF' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 2 (simple syntax substitution for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
(@TopN int)SELECT TOP (@TopN)
        c.CustomerID,
        c.FirstName + ' ' + c.LastName AS FullName,
        COUNT(o.OrderID) AS OrderCount,
        ISNULL(SUM(o.TotalAmount), 0) AS TotalSpent,
        DATEDIFF(DAY, MAX(o.OrderDate), GETDATE()) AS DaysSinceLastOrder
    FROM dbo.Customers c
    LEFT JOIN dbo.Orders o ON c.CustomerID = o.CustomerID
    GROUP BY c.CustomerID, c.FirstName, c.LastName
    ORDER BY TotalSpent DESC
```

**PostgreSQL Equivalent:**
```sql
EXTRACT(EPOCH FROM ...) / AGE / date subtraction
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of DATEDIFF usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.

### WI-007: [Risk 2] Convert GETDATE in Ad Hoc Queries

**Description:** The SQL Server feature 'GETDATE' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 2 (simple syntax substitution for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
(@TopN int)SELECT TOP (@TopN)
        c.CustomerID,
        c.FirstName + ' ' + c.LastName AS FullName,
        COUNT(o.OrderID) AS OrderCount,
        ISNULL(SUM(o.TotalAmount), 0) AS TotalSpent,
        DATEDIFF(DAY, MAX(o.OrderDate), GETDATE()) AS DaysSinceLastOrder
    FROM dbo.Customers c
    LEFT JOIN dbo.Orders o ON c.CustomerID = o.CustomerID
    GROUP BY c.CustomerID, c.FirstName, c.LastName
    ORDER BY TotalSpent DESC
```

**PostgreSQL Equivalent:**
```sql
NOW() / CURRENT_TIMESTAMP
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of GETDATE usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.

