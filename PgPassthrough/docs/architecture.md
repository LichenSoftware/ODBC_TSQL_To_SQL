# PgPassthrough — Architecture Document

## 1. Product Summary

PgPassthrough is a Windows middleware service that allows legacy Microsoft SQL Server
client applications to connect through their existing SQL Server ODBC drivers and run
T-SQL queries against a PostgreSQL backend — without modifying the application.

## 2. Protocol Choice: TDS over TCP

### Why TDS and not a custom ODBC driver

A native ODBC driver requires a C/C++ DLL that implements the ODBC ABI
(`SQLConnect`, `SQLExecDirect`, `SQLFetch`, …). That approach creates:

- Two codebases (C shim + .NET service)
- 32-bit and 64-bit build variants
- ODBC Driver Manager registration requirements per machine

The Tabular Data Stream (TDS) protocol is the wire protocol used by SQL Server.
Applications that use the SQL Server ODBC driver, ADO.NET `SqlClient`, JDBC's
`mssql-jdbc`, or OLE DB all speak TDS over TCP. By listening on a TCP port and
implementing TDS, PgPassthrough works with every SQL Server client without any
native code, additional installation on the client machine, or driver registration.

The client configures a DSN pointing to `<host>:1433` (or any port) and connects
using their existing SQL Server driver. This is the same approach taken by AWS
Babelfish and the open source `mssql-pg` projects.

### TDS reference

Microsoft's open specification: [MS-TDS](https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-tds)

## 3. Layer Architecture

```
Client App (SQL Server ODBC driver)
        │
        │ TDS / TCP
        ▼
┌───────────────────────────────────────────────────────────┐
│ PgPassthrough.Tds                                         │
│  TdsListener ─► TdsSession ─► TdsPacketReader/Writer      │
│  Handles: Login7, SQLBatch, RPC, Attention, Transactions  │
└───────────────────────────────────┬───────────────────────┘
                                    │ ClientRequest
┌───────────────────────────────────▼───────────────────────┐
│ PgPassthrough.Translation                                 │
│  TSqlParser ─► TSqlAst ─► AstTranslator ─► PgSqlAst      │
│  TranslationCache (LRU, thread-safe)                      │
└───────────────────────────────────┬───────────────────────┘
                                    │ string (PgSQL)
┌───────────────────────────────────▼───────────────────────┐
│ PgPassthrough.Execution                                   │
│  PostgresExecutor ─► Npgsql connection pool               │
│  TransactionManager                                       │
└───────────────────────────────────┬───────────────────────┘
                                    │ IResultSet
┌───────────────────────────────────▼───────────────────────┐
│ PgPassthrough.Results                                     │
│  TypeMapper ─► TdsResultEncoder ─► TDS token stream       │
└───────────────────────────────────────────────────────────┘
```

## 4. Project Structure and Responsibilities

| Project | Responsibility |
|---|---|
| `PgPassthrough.Core` | All shared abstractions, models, no framework dependencies |
| `PgPassthrough.Tds` | TDS protocol: packet framing, session lifecycle, message parsing |
| `PgPassthrough.SqlParser` | T-SQL lexer, recursive-descent parser, AST node definitions |
| `PgPassthrough.Translation` | AST-to-AST transformation T-SQL→PostgreSQL, translation cache |
| `PgPassthrough.Execution` | Npgsql wrapper, connection pooling, transaction lifecycle |
| `PgPassthrough.Results` | SQL Server ↔ PostgreSQL type mapping, TDS result encoding |
| `PgPassthrough.Server` | Composition root, `IHostedService`, DI wiring, config |

## 5. Key Design Decisions

### 5.1 AST-based translation (not string replacement)

String replacement breaks on quoted identifiers, comments, string literals,
and multi-statement batches. A proper AST allows:

- Structural transforms (e.g., rewriting `SELECT TOP N` to `SELECT … LIMIT N`)
- Context-aware decisions (e.g., `#temp` tables inside a stored procedure)
- Round-trip safety (translator only emits valid PostgreSQL)
- Testable in complete isolation

### 5.2 Translation cache

The same SQL statement is typically sent repeatedly from connection pools and
prepared statements. An LRU cache keyed on the normalised T-SQL text (after
stripping parameter values) avoids re-parsing on every call. The cache key
must include session SET-option flags (ANSI_NULLS, QUOTED_IDENTIFIER) because
these can change token interpretation.

Cache invalidation: entries are never explicitly invalidated (schema changes
do not change SQL text). TTL is optional (default: indefinite). The cache is
bounded by `MaxEntries`; eviction is LRU.

### 5.3 Extensibility for other backends

`IBackendProvider` is the seam for adding non-PostgreSQL backends. Each
provider supplies its own `ISqlTranslator` and `IExecutionEngine`. The TDS
protocol layer and request dispatch layer are backend-agnostic.

### 5.4 Connection pooling

Npgsql has a mature built-in connection pool. PgPassthrough configures it via
the connection string (`Minimum Pool Size`, `Maximum Pool Size`) rather than
implementing its own pool, which would duplicate well-tested behaviour.

One important nuance: each TDS session does not permanently hold a PostgreSQL
connection. Connections are acquired from the pool at query time and returned
after the result set is consumed. Sessions with open transactions hold their
connection for the duration of the transaction.

### 5.5 Concurrent sessions

Each TDS session runs on its own `Task` dispatched by `TdsListener`. Session
state (`SessionContext`) is owned exclusively by that task. Shared state
(the translation cache, the connection pool) is designed for concurrent access
(`ConcurrentDictionary`, Npgsql's thread-safe pool).

## 6. Risks and Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| TDS spec complexity | High | Implement incrementally; start with Login7 + SQLBatch only |
| T-SQL surface area | High | Track a compatibility matrix; fail fast on unsupported syntax |
| Parameter type mapping | Medium | Maintain an explicit type map; log unknown types as warnings |
| Stored procedure semantics | High | Phase 6+ feature; document limitations clearly |
| SQL Server snapshot isolation | Medium | Map to PG REPEATABLE READ; document caveat |
| `@@IDENTITY` / `SCOPE_IDENTITY()` | Medium | Capture `RETURNING` value; store in SessionContext |
| `#temp` table scoping | Medium | Map to `pg_temp` schema; document cross-session limits |

## 7. Technical Debt Register (Phase 1)

- TLS/SSL on the TDS listener is deferred. Required for production.
- Windows Authentication (Kerberos/NTLM) is not implemented; SQL login only.
- `sa`-equivalent superuser mapping is not implemented.
- `USE <database>` command requires runtime database switching in Npgsql;
  this involves tearing down and recreating the connection with a new database
  name, or using `SET search_path`.
- Encryption of the password field in `appsettings.json` is deferred;
  in production, use environment variables or a secrets manager.

## 8. Phase Roadmap

| Phase | Focus |
|---|---|
| 1 ✅ | Architecture, core abstractions, project scaffold |
| 2 | TDS protocol layer: Login7, SQLBatch, DONE/ERROR tokens |
| 3 | T-SQL lexer and parser |
| 4 | AST node definitions |
| 5 | Translation engine (T-SQL AST → PostgreSQL AST → SQL text) |
| 6 | PostgreSQL execution engine (Npgsql, transactions, pooling) |
| 7 | Result-set translation (type mapping, TDS encoding) |
| 8 | Compatibility test suite against real SQL Server applications |
