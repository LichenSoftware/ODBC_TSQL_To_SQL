namespace PgPassthrough.SqlParser.Ast;

/// <summary>
/// Default base implementation of <see cref="ISqlVisitor{TResult}"/>.
/// Every method calls <see cref="DefaultVisit"/> which returns <see cref="DefaultResult"/>.
/// Override only the node types you care about.
/// 
/// Used by diagnostic walkers and as a base for the translator.
/// </summary>
public abstract class SqlVisitorBase<TResult> : ISqlVisitor<TResult>
{
    protected abstract TResult DefaultResult { get; }

    protected virtual TResult DefaultVisit(SqlNode node) => DefaultResult;

    public virtual TResult VisitBatch(SqlBatch node) => DefaultVisit(node);
    public virtual TResult VisitSelect(SelectStatement node) => DefaultVisit(node);
    public virtual TResult VisitInsert(InsertStatement node) => DefaultVisit(node);
    public virtual TResult VisitUpdate(UpdateStatement node) => DefaultVisit(node);
    public virtual TResult VisitDelete(DeleteStatement node) => DefaultVisit(node);
    public virtual TResult VisitTruncateTable(TruncateTableStatement node) => DefaultVisit(node);
    public virtual TResult VisitCreateTable(CreateTableStatement node) => DefaultVisit(node);
    public virtual TResult VisitDropTable(DropTableStatement node) => DefaultVisit(node);
    public virtual TResult VisitBeginTransaction(BeginTransactionStatement node) => DefaultVisit(node);
    public virtual TResult VisitCommitTransaction(CommitTransactionStatement node) => DefaultVisit(node);
    public virtual TResult VisitRollbackTransaction(RollbackTransactionStatement node) => DefaultVisit(node);
    public virtual TResult VisitSaveTransaction(SaveTransactionStatement node) => DefaultVisit(node);
    public virtual TResult VisitSetOption(SetOptionStatement node) => DefaultVisit(node);
    public virtual TResult VisitUseDatabase(UseDatabaseStatement node) => DefaultVisit(node);
    public virtual TResult VisitExecute(ExecuteStatement node) => DefaultVisit(node);
    public virtual TResult VisitIf(IfStatement node) => DefaultVisit(node);
    public virtual TResult VisitWhile(WhileStatement node) => DefaultVisit(node);
    public virtual TResult VisitBeginEnd(BeginEndBlock node) => DefaultVisit(node);
    public virtual TResult VisitPrint(PrintStatement node) => DefaultVisit(node);
    public virtual TResult VisitReturn(ReturnStatement node) => DefaultVisit(node);
    public virtual TResult VisitDeclare(DeclareStatement node) => DefaultVisit(node);
    public virtual TResult VisitUnparsed(UnparsedStatement node) => DefaultVisit(node);
    public virtual TResult VisitTop(TopClause node) => DefaultVisit(node);
    public virtual TResult VisitSelectItem(SelectItem node) => DefaultVisit(node);
    public virtual TResult VisitIntoClause(IntoClause node) => DefaultVisit(node);
    public virtual TResult VisitOrderByItem(OrderByItem node) => DefaultVisit(node);
    public virtual TResult VisitOffsetFetch(OffsetFetchClause node) => DefaultVisit(node);
    public virtual TResult VisitSetOperator(SetOperator node) => DefaultVisit(node);
    public virtual TResult VisitValuesClause(ValuesClause node) => DefaultVisit(node);
    public virtual TResult VisitSetClause(SetClause node) => DefaultVisit(node);
    public virtual TResult VisitOutputClause(OutputClause node) => DefaultVisit(node);
    public virtual TResult VisitProcedureArgument(ProcedureArgument node) => DefaultVisit(node);
    public virtual TResult VisitVariableDeclaration(VariableDeclaration node) => DefaultVisit(node);
    public virtual TResult VisitColumnDefinition(ColumnDefinition node) => DefaultVisit(node);
    public virtual TResult VisitIntegerLiteral(IntegerLiteralExpression node) => DefaultVisit(node);
    public virtual TResult VisitDecimalLiteral(DecimalLiteralExpression node) => DefaultVisit(node);
    public virtual TResult VisitFloatLiteral(FloatLiteralExpression node) => DefaultVisit(node);
    public virtual TResult VisitStringLiteral(StringLiteralExpression node) => DefaultVisit(node);
    public virtual TResult VisitNullLiteral(NullLiteralExpression node) => DefaultVisit(node);
    public virtual TResult VisitBooleanLiteral(BooleanLiteralExpression node) => DefaultVisit(node);
    public virtual TResult VisitObjectName(ObjectName node) => DefaultVisit(node);
    public virtual TResult VisitColumnReference(ColumnReferenceExpression node) => DefaultVisit(node);
    public virtual TResult VisitParameter(ParameterExpression node) => DefaultVisit(node);
    public virtual TResult VisitGlobalVariable(GlobalVariableExpression node) => DefaultVisit(node);
    public virtual TResult VisitBinary(BinaryExpression node) => DefaultVisit(node);
    public virtual TResult VisitUnary(UnaryExpression node) => DefaultVisit(node);
    public virtual TResult VisitBetween(BetweenExpression node) => DefaultVisit(node);
    public virtual TResult VisitInList(InListExpression node) => DefaultVisit(node);
    public virtual TResult VisitInSubquery(InSubqueryExpression node) => DefaultVisit(node);
    public virtual TResult VisitLike(LikeExpression node) => DefaultVisit(node);
    public virtual TResult VisitIsNull(IsNullExpression node) => DefaultVisit(node);
    public virtual TResult VisitExists(ExistsExpression node) => DefaultVisit(node);
    public virtual TResult VisitFunctionCall(FunctionCallExpression node) => DefaultVisit(node);
    public virtual TResult VisitOverClause(OverClause node) => DefaultVisit(node);
    public virtual TResult VisitWindowFrame(WindowFrame node) => DefaultVisit(node);
    public virtual TResult VisitWindowFrameBound(WindowFrameBound node) => DefaultVisit(node);
    public virtual TResult VisitCast(CastExpression node) => DefaultVisit(node);
    public virtual TResult VisitConvert(ConvertExpression node) => DefaultVisit(node);
    public virtual TResult VisitCase(CaseExpression node) => DefaultVisit(node);
    public virtual TResult VisitWhenClause(WhenClause node) => DefaultVisit(node);
    public virtual TResult VisitSubquery(SubqueryExpression node) => DefaultVisit(node);
    public virtual TResult VisitDataType(DataTypeNode node) => DefaultVisit(node);
    public virtual TResult VisitTableReference(TableReferenceSource node) => DefaultVisit(node);
    public virtual TResult VisitSubquerySource(SubquerySource node) => DefaultVisit(node);
    public virtual TResult VisitJoin(JoinedSource node) => DefaultVisit(node);
    public virtual TResult VisitTableHint(TableHint node) => DefaultVisit(node);
}
