# AI Prompt: Build SQL Server to PostgreSQL Migration Assessment Engine

You are a principal database architect responsible for designing and implementing a SQL Server to PostgreSQL Migration Assessment Engine.

Your goal is to build a system that analyzes a Microsoft SQL Server environment and produces an objective migration readiness score.

The system should identify all database objects, query patterns, and SQL Server-specific features that would impact a migration to PostgreSQL.

---

# Primary Objective

Determine:

1. How difficult it would be to migrate the database to PostgreSQL.
2. Which database features create migration risk.
3. Which SQL statements require manual conversion.
4. Which SQL statements can be converted automatically.
5. Which objects present the greatest migration challenge.
6. An overall migration readiness score.

---

# Data Collection Requirements

The assessment engine shall collect information from:

## SQL Server Query Store

Capture:

* Executed SQL statements
* Execution counts
* Average duration
* CPU consumption
* Logical reads
* Execution plans

---

## Extended Events

Capture:

* Ad hoc SQL statements
* Stored procedure execution
* Dynamic SQL execution
* Temp table creation
* Error handling patterns

---

## Database Metadata

Capture:

* Tables
* Columns
* Indexes
* Constraints
* Foreign keys
* Views
* Triggers
* Functions
* Stored procedures
* Synonyms

---

## SQL Server Specific Features

Detect:

* SQL CLR assemblies
* Service Broker
* SQL Agent jobs
* CDC
* Change Tracking
* Replication
* Linked Servers
* Full Text Search
* FileStream
* XML indexes
* Temporal Tables
* Memory Optimized Tables
* Partitioning

---

# Statement Analysis Engine

Analyze every captured SQL statement.

Parse statements into an Abstract Syntax Tree (AST).

Do not use regular expressions as the primary analysis mechanism.

Use a T-SQL parser.

Examples:

* Microsoft.SqlServer.TransactSql.ScriptDom
* ANTLR T-SQL Grammar

---

# Risk Scoring Framework

Each statement receives a risk score.

## Risk 1 - Trivial

Fully compatible with PostgreSQL.

Examples:

* Basic SELECT
* Simple INSERT
* Simple UPDATE
* Simple DELETE

Estimated conversion effort:

0-5 minutes

---

## Risk 2 - Low

Requires straightforward syntax translation.

Examples:

* TOP
* ISNULL
* GETDATE
* LEN
* String concatenation

Estimated conversion effort:

5-30 minutes

---

## Risk 3 - Moderate

Requires procedural changes.

Examples:

* TRY/CATCH
* Dynamic SQL
* Common table expressions with SQL Server specific syntax
* Identity handling

Estimated conversion effort:

30 minutes to 4 hours

---

## Risk 4 - High

Requires significant redesign.

Examples:

* MERGE
* Table-valued parameters
* Multi-statement table-valued functions
* Global temporary tables
* SQL Server locking hints

Estimated conversion effort:

4-40 hours

---

## Risk 5 - Critical

Requires architectural replacement.

Examples:

* SQL CLR
* Service Broker
* Linked Servers
* Replication
* FileStream
* Memory Optimized Tables

Estimated conversion effort:

40+ hours

---

# Feature Detection Matrix

The system must identify occurrences of:

## Query Features

* TOP
* OFFSET FETCH
* MERGE
* OUTPUT clause
* CROSS APPLY
* OUTER APPLY
* PIVOT
* UNPIVOT
* Dynamic SQL
* EXEC()
* OPENQUERY
* OPENROWSET

---

## Function Usage

* GETDATE
* DATEADD
* DATEDIFF
* DATEPART
* ISNULL
* CHARINDEX
* PATINDEX
* STUFF
* XML methods
* JSON methods

---

## Temporary Objects

* #temp tables
* ##global temp tables
* table variables
* table-valued parameters

---

## Transaction Features

* TRY/CATCH
* Explicit transactions
* Savepoints
* Locking hints
* NOLOCK
* ROWLOCK
* UPDLOCK

---

# Complexity Weighting

Calculate weighted risk using:

Risk Score × Frequency × Business Importance

Example:

A MERGE statement executed once per month:

Low business risk

A MERGE statement executed 50,000 times per day:

High business risk

The assessment should prioritize heavily used workloads.

---

# Deliverables

Generate:

## Executive Summary

Migration Readiness Score

Scale:

0-100

Example:

92 = Excellent Candidate
75 = Good Candidate
50 = Significant Work Required
25 = High Risk
0 = Not Recommended

---

## Risk Breakdown

| Risk Level | Count  |
| ---------- | ------ |
| 1          | 14,522 |
| 2          | 2,113  |
| 3          | 242    |
| 4          | 31     |
| 5          | 4      |

---

## Top Migration Challenges

Rank:

* Most complex stored procedures
* Most complex functions
* Most complex views
* Unsupported SQL Server features

---

## Estimated Migration Effort

Provide estimates for:

* Schema Conversion
* Code Conversion
* Testing
* Data Migration
* Performance Tuning

Output:

Small
Medium
Large
Enterprise

and estimated engineering hours.

---

## Migration Recommendations

Recommend one of:

1. Direct PostgreSQL Migration
2. PostgreSQL Migration with Compatibility Middleware
3. Partial Migration
4. Remain on SQL Server

Provide detailed reasoning.

---

# Future Integration

Generate machine-readable output.

JSON format must include:

* Object inventory
* Feature inventory
* Risk scores
* Translation candidates
* Migration recommendations

The JSON output will later be consumed by an automated schema conversion engine and SQL translation middleware.
