# TDS Protocol Implementation Notes

## What is TDS?

Tabular Data Stream (TDS) is the binary wire protocol used by Microsoft SQL Server.
It runs over TCP/IP. All SQL Server clients — ODBC, ADO.NET SqlClient, JDBC mssql-jdbc,
OLEDB — speak TDS. By implementing a TDS server, PgPassthrough is compatible with every
SQL Server client library without any client-side changes.

Reference: [MS-TDS Open Specification](https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-tds)

## Phase 2 Implementation Coverage

### Implemented

| Feature | Class | Notes |
|---|---|---|
| TCP listener + session dispatch | `TdsListener` | Enforces MaxConcurrentSessions |
| Packet framing (read) | `TdsPacketReader` | Multi-packet reassembly |
| Packet framing (write) | `TdsPacketWriter` | Auto-splits large payloads |
| PRELOGIN handshake | `PreLoginMessage` | Negotiates TDS version; encryption = NOT SUPPORTED |
| LOGIN7 parsing + password de-obfuscation | `Login7Message` | SQL login only (no Windows Auth) |
| LOGINACK + ENVCHANGE response | `TdsTokenWriter.WriteLoginAck` | Reports version 15.0 (SQL Server 2019) |
| SQLBatch message parsing | `SqlBatchMessage` | Strips ALL_HEADERS block |
| RPC request parsing | `RpcRequestMessage` | Handles named procs + well-known proc IDs |
| COLMETADATA token | `TdsTokenWriter.WriteColMetadata` | All common SQL Server types |
| ROW token | `TdsTokenWriter.WriteRow` | Fixed + variable length types |
| DONE/DONEINPROC/DONEPROC tokens | `TdsTokenWriter.WriteDone*` | Row count, status flags |
| ERROR token | `TdsTokenWriter.WriteError` | Severity, state, server name, line number |
| INFO token | `TdsTokenWriter.WriteInfo` | Informational messages |
| ENVCHANGE tokens | `TdsTokenWriter.WriteEnvChange*` | Database, language, packet size, transaction |
| Attention signal | `TdsSession` | ACKs with DONE(Attention) |
| Transaction Manager Request | `TdsSession` | Maps to TransactionRequest |
| Session state machine | `TdsSession` | PreLogin → Login → Active → Closed |
| Credential validation | `ConfiguredCredentialValidator` | Static config list |
| `IHostedService` integration | `TdsServerService` | Plugs into .NET Generic Host |

### Not Implemented (Tech Debt)

| Feature | Risk | Notes |
|---|---|---|
| TLS/SSL on the listener | **High** for production | Required before any external exposure |
| Windows Authentication (NTLM/Kerberos) | Medium | Needed for AD-integrated apps |
| NBCROW (null-bitmap rows) | Low | More compact than ROW; optional optimisation |
| MARS (Multiple Active Result Sets) | Low | Negotiate off for now |
| Bulk Load (BULKINSERT) | Medium | Deferred to a later phase |
| Federated Authentication | Low | Azure AD / Entra ID |
| Encrypted password change | Low | `OptionFlags3.ChangePassword` |
| sp_prepare / sp_execute handles | Medium | Phase 6 |
| Cursor operations | Low | `sp_cursor*` proc IDs |
| Routing / redirect | Low | Not needed for on-premise use |
| PLP (Partially Length-Prefixed) MAX types | Low | nvarchar(max) chunked encoding |

## Packet Format Reference

```
TDS Packet Header (8 bytes):
  [0] Type        - message type (0x01=SqlBatch, 0x04=TabularResult, etc.)
  [1] Status      - flags (0x01=EOM, 0x02=Ignore, 0x08=ResetConn)
  [2-3] Length    - total packet length including header, big-endian
  [4-5] SPID      - server process ID (set to 0 for our responses)
  [6]  PacketId   - monotonically increasing per message
  [7]  Window     - always 0
```

## Authentication Flow

```
Client → Server: PRELOGIN (version negotiation, encryption request)
Server → Client: PRELOGIN response (echo version, EncryptionNotSupported)
Client → Server: LOGIN7 (username, obfuscated password, database, app name)
Server → Client: LOGINACK + ENVCHANGE(database) + ENVCHANGE(packetSize) + DONE(Final)
```

## Password Obfuscation Algorithm

TDS LOGIN7 passwords are not encrypted — they are obfuscated with a simple
swap-nibbles-then-XOR-0xA5 operation per byte:

```
byte Encode(byte b) {
    b = (b & 0x0F) << 4 | (b & 0xF0) >> 4;  // swap nibbles
    b ^= 0xA5;                                 // XOR with 0xA5
    return b;
}

byte Decode(byte b) {
    b ^= 0xA5;                                 // XOR first (reverse of encode)
    b = (b & 0x0F) << 4 | (b & 0xF0) >> 4;  // then swap nibbles
    return b;
}
```

**This is not security.** Without TLS, passwords traverse the network in near-plaintext.
TLS must be enabled before production deployment.

## Phase 7: Result-Set Token Encoding

### Key Insight: Nullable Type Variants

ODBC Driver 17 requires nullable column types to use the **N-variant** type codes
in COLMETADATA, not the fixed-length codes. Each value in the ROW token must be
preceded by a 1-byte length prefix (0 = NULL).

| SQL Type | Fixed Code | Nullable Wire Code | Wire Name |
|---|---|---|---|
| tinyint | 0x30 | 0x26 (len=1) | INTN |
| smallint | 0x34 | 0x26 (len=2) | INTN |
| int | 0x38 | 0x26 (len=4) | INTN |
| bigint | 0x7F | 0x26 (len=8) | INTN |
| bit | 0x32 | 0x68 (len=1) | BITN |
| real | 0x3B | 0x6D (len=4) | FLTN |
| float | 0x3E | 0x6D (len=8) | FLTN |
| money | 0x3C | 0x6E (len=8) | MONEYN |
| smallmoney | 0x7A | 0x6E (len=4) | MONEYN |
| datetime | 0x3D | 0x6F (len=8) | DATETIMN |
| smalldatetime | 0x3A | 0x6F (len=4) | DATETIMN |

Variable-length types (nvarchar, varchar, varbinary) continue to use their
native type codes with 2-byte data length prefix per row value (0xFFFF = NULL).

### PRELOGIN Response Packet Type

The server PRELOGIN response must be sent as packet type **0x04** (TabularResult),
NOT 0x12. The client sends 0x12; the server responds with 0x04.

### Encryption Negotiation

Responding with `ENCRYPT_NOT_SUP` (0x02) causes ODBC Driver 17+ to skip the
TLS ClientHello entirely. This avoids needing to implement TLS termination
for development/testing.

### Configuration Loading Fix

The server uses `UseContentRoot(AppContext.BaseDirectory)` to ensure
`appsettings.json` is found regardless of the working directory when launched
via `dotnet run --project`.

## Error Handling Strategy

- Protocol violations (wrong packet type in a state) → `TdsProtocolException` → 
  send ERROR(severity=20) + DONE(Error) → close session.
- Application errors (query failed, translation error) → send ERROR(severity=16) + 
  DONE(Error) → keep session open. Client can retry.
- Network errors (stream closed, timeout) → log → close session silently.
