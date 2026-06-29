# Migration Work Items Report

**Generated:** 2026-06-29T18:28:29.7013816+00:00
**Source:** ./test-assessment.json
**Total Work Items:** 10
**Estimated Effort:** 90.18-418.6 hours

## Risk Distribution

| Priority | Count |
|----------|-------|
| Critical | 1 |
| High | 2 |
| Medium | 4 |
| Low | 3 |

## Table of Contents

- [Critical Priority](#critical-priority)
- [High Priority](#high-priority)
- [Medium Priority](#medium-priority)
- [Low Priority](#low-priority)

## Critical Priority

### WI-008: [Risk 4] Convert NOLOCK in Ad Hoc Queries

**Description:** The SQL Server feature 'NOLOCK' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 3 occurrences across the analyzed codebase with a combined total of 3 executions recorded.
This represents a low business impact based on execution frequency.
Risk Level: 4 (requires design pattern changes for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
SELECT
      db_id() AS database_id,
      c.system_type_id,
      c.user_type_id,
      c.is_sparse,
      c.is_column_set,
      c.is_filestream,
      c.encryption_type,
      CASE WHEN o.object_id IS NOT NULL THEN 1 ELSE 0 END AS is_user,
      COUNT_BIG(*) AS [ColCount],
      CASE WHEN c.collation_name IS NULL THEN CONVERT(VARCHAR(128), SERVERPROPERTY('Collation')) ELSE c.collation_name END AS collation_name,
      AVG(c.max_length) AS avg_max_length
      FROM sys.columns c WITH (NOLOCK)

```

**PostgreSQL Equivalent:**
```sql
-- TODO: verify locking strategy; NOLOCK removed (PostgreSQL MVCC provides non-blocking reads)
SELECT
      db_id() AS database_id,
      c.system_type_id,
      c.user_type_id,
      c.is_sparse,
      c.is_column_set,
      c.is_filestream,
      c.encryption_type,
      CASE WHEN o.object_id IS NOT NULL THEN 1 ELSE 0 END AS is_user,
      COUNT_BIG(*) AS [ColCount],
      CASE WHEN c.collation_name IS NULL THEN CONVERT(VARCHAR(128), SERVERPROPERTY('Collation')) ELSE c.collation_name END AS collation_name,
      AVG(c.max_length) AS avg_max_length
      FROM sys.columns c

```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 3 statements

**Acceptance Criteria:**
1. All instances of NOLOCK usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

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
-- TODO: verify XPath expressions and namespace handling for PostgreSQL
(@OrderID int)SELECT
        o.OrderID,
        (xpath('(/order/shipping/address)[1]', m.MetadataXml))[1]::text::text AS ShippingAddress,
        (xpath('(/order/shipping/method)[1]', m.MetadataXml))[1]::text::text AS ShippingMethod,
        xpath('/order/items', m.MetadataXml) AS ItemsXml
    FROM dbo.Orders o
    INNER JOIN dbo.OrderMetadata m ON o.OrderID = m.OrderID
    WHERE o.OrderID = @OrderID
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of XML_METHOD usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The alternative architecture has been reviewed and approved by the team.
5. Integration tests confirm the replacement solution interoperates correctly with dependent systems.

### WI-002: [Risk 4] Convert 2 features in Ad Hoc Queries

**Description:** The SQL Server features 'UPDLOCK', 'ROWLOCK' are used in Ad Hoc Queries and are not directly supported in PostgreSQL.
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
-- TODO: UPDLOCK removed. Row-level locking on UPDATE/DELETE is implicit in PostgreSQL's MVCC.
-- If explicit pessimistic locking is required, use SELECT ... FOR UPDATE in a preceding
-- statement within the same transaction, then perform the UPDATE/DELETE.
(@ProductID int,@NewQuantity int)UPDATE p
    SET p.StockQuantity = @NewQuantity
    FROM dbo.Products p
    WHERE p.ProductID = @ProductID
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of ROWLOCK usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

## Medium Priority

### WI-005: [Risk 4] Convert GLOBAL_TEMP_TABLE in Ad Hoc Queries

**Description:** The SQL Server feature 'GLOBAL_TEMP_TABLE' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
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
-- TODO: global temp table reference converted to regular table name.
-- Ensure the table exists as a permanent/unlogged table with appropriate lifecycle management.
INSERT INTO GlobalOrderSummary
    SELECT
        CAST(OrderDate AS DATE),
        COUNT(*),
        SUM(TotalAmount)
    FROM dbo.Orders
    GROUP BY CAST(OrderDate AS DATE)
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of GLOBAL_TEMP_TABLE usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

### WI-006: [Risk 4] Convert MERGE in Ad Hoc Queries

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
-- TODO: verify conflict target column and update columns match your schema
-- Original MERGE converted to INSERT ... ON CONFLICT (upsert pattern)
INSERT INTO dbo.Products (ProductName, SKU, Price, StockQuantity)
SELECT ProductName, SKU, Price, StockQuantity
FROM dbo.ProductImportStaging AS source
ON CONFLICT (SKU) DO UPDATE SET
    ProductName = EXCLUDED.ProductName,
            Price = EXCLUDED.Price,
            StockQuantity = EXCLUDED.StockQuantity;
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of MERGE usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

### WI-007: [Risk 4] Convert GLOBAL_TEMP_TABLE in Ad Hoc Queries

**Description:** The SQL Server feature 'GLOBAL_TEMP_TABLE' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 4 (requires design pattern changes for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
SELECT * FROM ##GlobalOrderSummary ORDER BY OrderDate DESC
```

**PostgreSQL Equivalent:**
```sql
-- TODO: global temp table reference converted to regular table name.
-- Ensure the table exists as a permanent/unlogged table with appropriate lifecycle management.
SELECT * FROM GlobalOrderSummary ORDER BY OrderDate DESC
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of GLOBAL_TEMP_TABLE usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

### WI-010: [Risk 4] Convert NOLOCK in Ad Hoc Queries

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
-- TODO: verify locking strategy; NOLOCK removed (PostgreSQL MVCC provides non-blocking reads)
SELECT
        p.ProductID,
        p.ProductName,
        p.StockQuantity,
        p.Price
    FROM dbo.Products p
    WHERE p.StockQuantity > 0
    ORDER BY p.StockQuantity ASC
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of NOLOCK usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.
4. The redesigned PostgreSQL pattern handles concurrency scenarios correctly.

## Low Priority

### WI-004: [Risk 3] Convert TEMP_TABLE in Ad Hoc Queries

**Description:** The SQL Server feature 'TEMP_TABLE' is used in Ad Hoc Queries and is not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
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
(@Year int,@Month int)INSERT INTO MonthlyStats
    SELECT
        o.CustomerID,
        COUNT(*),
        SUM(o.TotalAmount)
    FROM dbo.Orders o
    WHERE YEAR(o.OrderDate) = @Year AND MONTH(o.OrderDate) = @Month
    GROUP BY o.CustomerID
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of TEMP_TABLE usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.

### WI-009: [Risk 3] Convert 2 features in Ad Hoc Queries

**Description:** The SQL Server features 'STRING_CONCAT_PLUS', 'TEMP_TABLE' are used in Ad Hoc Queries and are not directly supported in PostgreSQL.
Found 1 occurrence across the analyzed codebase with a combined total of 1 execution recorded.
This represents a low business impact based on execution frequency.
Risk Level: 3 (requires procedural logic changes for PostgreSQL compatibility).

**SQL Server Pattern:**
```sql
SELECT
        c.FirstName + ' ' + c.LastName AS CustomerName,
        ms.OrderCount,
        ms.TotalRevenue
    FROM #MonthlyStats ms
    INNER JOIN dbo.Customers c ON ms.CustomerID = c.CustomerID
    ORDER BY ms.TotalRevenue DESC
```

**PostgreSQL Equivalent:**
```sql
SELECT
        c.FirstName || ' ' || c.LastName AS CustomerName,
        ms.OrderCount,
        ms.TotalRevenue
    FROM MonthlyStats ms
    INNER JOIN dbo.Customers c ON ms.CustomerID = c.CustomerID
    ORDER BY ms.TotalRevenue DESC
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of TEMP_TABLE usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.
3. Unit tests verify the converted logic handles edge cases correctly.

### WI-003: [Risk 2] Convert 5 features in Ad Hoc Queries

**Description:** The SQL Server features 'TOP', 'STRING_CONCAT_PLUS', 'ISNULL', 'DATEDIFF', 'GETDATE' are used in Ad Hoc Queries and are not directly supported in PostgreSQL.
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
(@TopN int)SELECT TOP (@TopN)
        c.CustomerID,
        c.FirstName || ' ' || c.LastName AS FullName,
        COUNT(o.OrderID) AS OrderCount,
        COALESCE(SUM(o.TotalAmount), 0) AS TotalSpent,
        (NOW()::date - MAX(o.OrderDate)::date) AS DaysSinceLastOrder
    FROM dbo.Customers c
    LEFT JOIN dbo.Orders o ON c.CustomerID = o.CustomerID
    GROUP BY c.CustomerID, c.FirstName, c.LastName
    ORDER BY TotalSpent DESC
```

**Affected Objects:**
- Ad Hoc Queries (AdHoc) — 1 statement

**Acceptance Criteria:**
1. All instances of DATEDIFF usage have been replaced in Ad Hoc Queries.
2. The PostgreSQL equivalent produces correct results matching the original SQL Server behavior.

