namespace PgPassthrough.SqlParser.Lexer;

/// <summary>
/// Every distinct token type the T-SQL lexer can produce.
/// The order of keyword values is not significant; switch statements use the enum name.
/// </summary>
public enum TokenKind
{
    // -------------------------------------------------------------------------
    // Meta / control
    // -------------------------------------------------------------------------
    EndOfFile,
    Unknown,

    // -------------------------------------------------------------------------
    // Literals
    // -------------------------------------------------------------------------
    IntegerLiteral,     // 42
    DecimalLiteral,     // 3.14
    FloatLiteral,       // 1.5E2
    StringLiteral,      // 'hello' or N'hello'
    HexLiteral,         // 0x1A2B
    MoneyLiteral,       // $1.00

    // -------------------------------------------------------------------------
    // Identifiers and parameters
    // -------------------------------------------------------------------------
    Identifier,         // plain identifier or [bracketed] or "quoted"
    Parameter,          // @name
    GlobalVariable,     // @@name  (@@ROWCOUNT, @@IDENTITY, etc.)

    // -------------------------------------------------------------------------
    // Operators
    // -------------------------------------------------------------------------
    Plus,               // +
    Minus,              // -
    Star,               // *
    Slash,              // /
    Percent,            // %
    Ampersand,          // &
    Pipe,               // |
    Caret,              // ^
    Tilde,              // ~
    Equal,              // =
    NotEqual,           // <> or !=
    LessThan,           // <
    GreaterThan,        // >
    LessThanOrEqual,    // <=
    GreaterThanOrEqual, // >=
    NotLessThan,        // !<
    NotGreaterThan,     // !>
    PlusEqual,          // +=
    MinusEqual,         // -=
    StarEqual,          // *=
    SlashEqual,         // /=
    PercentEqual,       // %=
    AmpersandEqual,     // &=
    PipeEqual,          // |=
    CaretEqual,         // ^=

    // -------------------------------------------------------------------------
    // Punctuation
    // -------------------------------------------------------------------------
    Dot,                // .
    Comma,              // ,
    Semicolon,          // ;
    Colon,              // :
    DoubleColon,        // ::  (scope resolution)
    OpenParen,          // (
    CloseParen,         // )
    OpenBracket,        // [  (used in identifiers — consumed internally)
    CloseBracket,       // ]
    At,                 // @ (standalone, before identifier is scanned)

    // -------------------------------------------------------------------------
    // T-SQL Keywords — DDL/DML
    // -------------------------------------------------------------------------
    KwSelect,
    KwFrom,
    KwWhere,
    KwAnd,
    KwOr,
    KwNot,
    KwIn,
    KwLike,
    KwBetween,
    KwIs,
    KwNull,
    KwAs,
    KwOn,
    KwJoin,
    KwInner,
    KwLeft,
    KwRight,
    KwFull,
    KwOuter,
    KwCross,
    KwApply,
    KwUnion,
    KwAll,
    KwDistinct,
    KwTop,
    KwPercent,
    KwWithTies,
    KwOrderBy,       // ORDER  (BY is separate)
    KwBy,
    KwGroupBy,       // GROUP
    KwHaving,
    KwAsc,
    KwDesc,
    KwInsert,
    KwInto,
    KwValues,
    KwUpdate,
    KwSet,
    KwDelete,
    KwTruncate,
    KwTable,
    KwCreate,
    KwAlter,
    KwDrop,
    KwIndex,
    KwView,
    KwProcedure,
    KwProc,
    KwFunction,
    KwTrigger,
    KwDatabase,
    KwSchema,
    KwWith,
    KwNolock,
    KwRowlock,
    KwUpdlock,
    KwXlock,
    KwReadpast,
    KwForceseek,
    KwForcescan,
    KwNoexpand,
    KwReadcommitted,
    KwReaduncommitted,
    KwRepeatableread,
    KwSerializable,
    KwSnapshot,
    KwPaglock,
    KwTablock,
    KwTablock_x,
    KwHoldlock,
    KwNolock2,

    // -------------------------------------------------------------------------
    // T-SQL Keywords — transactions and flow
    // -------------------------------------------------------------------------
    KwBegin,
    KwEnd,
    KwCommit,
    KwRollback,
    KwTransaction,
    KwTran,
    KwSave,
    KwSavepoint,
    KwTry,
    KwCatch,
    KwThrow,
    KwRaiserror,
    KwIf,
    KwElse,
    KwWhile,
    KwBreak,
    KwContinue,
    KwReturn,
    KwGoto,
    KwWaitfor,
    KwDelay,
    KwTime,
    KwPrint,

    // -------------------------------------------------------------------------
    // T-SQL Keywords — type-related
    // -------------------------------------------------------------------------
    KwCast,
    KwConvert,
    KwTry_Cast,
    KwTry_Convert,
    KwCase,
    KwWhen,
    KwThen,
    KwElse2,         // ELSE in CASE (same token as KwElse)
    KwEnd2,          // END in CASE (same token as KwEnd)
    KwCoalesce,
    KwNullif,
    KwIsnull,
    KwExists,
    KwAny,
    KwSome,

    // -------------------------------------------------------------------------
    // T-SQL Keywords — data types
    // -------------------------------------------------------------------------
    KwInt,
    KwBigint,
    KwSmallint,
    KwTinyint,
    KwBit,
    KwFloat,
    KwReal,
    KwDecimal,
    KwNumeric,
    KwMoney,
    KwSmallmoney,
    KwChar,
    KwVarchar,
    KwNchar,
    KwNvarchar,
    KwText,
    KwNtext,
    KwBinary,
    KwVarbinary,
    KwImage,
    KwDatetime,
    KwDatetime2,
    KwDate,
    KwTime2,         // TIME (keyword)
    KwDatetimeoffset,
    KwSmalldatetime,
    KwUniqueidentifier,
    KwXml,
    KwSql_Variant,
    KwRowversion,
    KwTimestamp,
    KwHierarchyid,
    KwGeography,
    KwGeometry,

    // -------------------------------------------------------------------------
    // T-SQL Keywords — misc
    // -------------------------------------------------------------------------
    KwUse,
    KwExec,
    KwExecute,
    KwOutput,
    KwOut,
    KwDefault,
    KwIdentity,
    KwPrimary,
    KwForeign,
    KwKey,
    KwConstraint,
    KwUnique,
    KwCheck,
    KwReferences,
    KwNull2,         // NULL (same as KwNull conceptually)
    KwColumn,
    KwAdd,
    KwFor,
    KwNocount,
    KwAnsi_nulls,
    KwAnsi_padding,
    KwAnsi_warnings,
    KwQuoted_identifier,
    KwConcatNullYieldsNull,
    KwArith_abort,
    KwXact_abort,
    KwRowcount,
    KwDateformat,
    KwLanguage,
    KwOf,
    KwOffsets,
    KwAt2,           // AT (e.g. AT TIME ZONE)
    KwZone,
    KwOver,
    KwPartition,
    KwRows,
    KwRange,
    KwPreceding,
    KwFollowing,
    KwCurrent,
    KwRow,
    KwUnbounded,
    KwPivot,
    KwUnpivot,
    KwTablesample,
    KwPercent2,      // PERCENT in TABLESAMPLE
    KwRows2,         // ROWS in TABLESAMPLE
    KwSystem,
    KwReadonly,
    KwVarying,
    KwRecompile,
    KwEncryption,
    KwNative_compilation,
    KwSchemabinding,
    KwMerge,
    KwMatched,
    KwTarget,
    KwSource,
    KwUsing,
    KwWhen2,
    KwInsert2,
    KwUpdate2,
    KwDelete2,
    KwThen2,
    KwOutput2,
    KwInto2,
    KwBulk,
    KwOpenquery,
    KwOpendatasource,
    KwOpenrowset,
    KwOpenxml,
    KwContains,
    KwFreetext,
}
