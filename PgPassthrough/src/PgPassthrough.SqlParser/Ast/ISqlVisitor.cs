namespace PgPassthrough.SqlParser.Ast;

/// <summary>
/// Visitor interface over the complete T-SQL AST.
/// Every concrete node type has a corresponding Visit method.
/// 
/// Type parameter <typeparamref name="TResult"/> is the return type of each visit.
/// For the translator it will be <c>string</c> (emitted PostgreSQL text).
/// For diagnostic walkers it may be <c>bool</c> or a collector type.
/// </summary>
public interface ISqlVisitor<TResult>
{
    // Batch
    TResult VisitBatch(SqlBatch node);

    // Statements
    TResult VisitSelect(SelectStatement node);
    TResult VisitInsert(InsertStatement node);
    TResult VisitUpdate(UpdateStatement node);
    TResult VisitDelete(DeleteStatement node);
    TResult VisitTruncateTable(TruncateTableStatement node);
    TResult VisitCreateTable(CreateTableStatement node);
    TResult VisitDropTable(DropTableStatement node);
    TResult VisitBeginTransaction(BeginTransactionStatement node);
    TResult VisitCommitTransaction(CommitTransactionStatement node);
    TResult VisitRollbackTransaction(RollbackTransactionStatement node);
    TResult VisitSaveTransaction(SaveTransactionStatement node);
    TResult VisitSetOption(SetOptionStatement node);
    TResult VisitUseDatabase(UseDatabaseStatement node);
    TResult VisitExecute(ExecuteStatement node);
    TResult VisitIf(IfStatement node);
    TResult VisitWhile(WhileStatement node);
    TResult VisitBeginEnd(BeginEndBlock node);
    TResult VisitPrint(PrintStatement node);
    TResult VisitReturn(ReturnStatement node);
    TResult VisitDeclare(DeclareStatement node);
    TResult VisitUnparsed(UnparsedStatement node);

    // Clause nodes
    TResult VisitTop(TopClause node);
    TResult VisitSelectItem(SelectItem node);
    TResult VisitIntoClause(IntoClause node);
    TResult VisitOrderByItem(OrderByItem node);
    TResult VisitOffsetFetch(OffsetFetchClause node);
    TResult VisitSetOperator(SetOperator node);
    TResult VisitValuesClause(ValuesClause node);
    TResult VisitSetClause(SetClause node);
    TResult VisitOutputClause(OutputClause node);
    TResult VisitProcedureArgument(ProcedureArgument node);
    TResult VisitVariableDeclaration(VariableDeclaration node);
    TResult VisitColumnDefinition(ColumnDefinition node);

    // Expressions
    TResult VisitIntegerLiteral(IntegerLiteralExpression node);
    TResult VisitDecimalLiteral(DecimalLiteralExpression node);
    TResult VisitFloatLiteral(FloatLiteralExpression node);
    TResult VisitStringLiteral(StringLiteralExpression node);
    TResult VisitNullLiteral(NullLiteralExpression node);
    TResult VisitBooleanLiteral(BooleanLiteralExpression node);
    TResult VisitObjectName(ObjectName node);
    TResult VisitColumnReference(ColumnReferenceExpression node);
    TResult VisitParameter(ParameterExpression node);
    TResult VisitGlobalVariable(GlobalVariableExpression node);
    TResult VisitBinary(BinaryExpression node);
    TResult VisitUnary(UnaryExpression node);
    TResult VisitBetween(BetweenExpression node);
    TResult VisitInList(InListExpression node);
    TResult VisitInSubquery(InSubqueryExpression node);
    TResult VisitLike(LikeExpression node);
    TResult VisitIsNull(IsNullExpression node);
    TResult VisitExists(ExistsExpression node);
    TResult VisitFunctionCall(FunctionCallExpression node);
    TResult VisitOverClause(OverClause node);
    TResult VisitWindowFrame(WindowFrame node);
    TResult VisitWindowFrameBound(WindowFrameBound node);
    TResult VisitCast(CastExpression node);
    TResult VisitConvert(ConvertExpression node);
    TResult VisitCase(CaseExpression node);
    TResult VisitWhenClause(WhenClause node);
    TResult VisitSubquery(SubqueryExpression node);
    TResult VisitDataType(DataTypeNode node);

    // Table sources
    TResult VisitTableReference(TableReferenceSource node);
    TResult VisitSubquerySource(SubquerySource node);
    TResult VisitJoin(JoinedSource node);
    TResult VisitTableHint(TableHint node);
}
