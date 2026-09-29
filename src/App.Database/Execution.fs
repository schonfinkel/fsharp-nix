namespace App.Database

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Polly
open Polly.Retry

[<RequireQualifiedAccess>]
module ExecutionSql =
    let setLocalTimeouts = Sql.load "Execution/set-local-timeouts"

/// <summary>
/// The three PostgreSQL execution policies (PLAN "Polly and PostgreSQL integration"), all over
/// the one shared <see cref="NpgsqlDataSource"/>:
/// <list type="bullet">
/// <item><c>executeRead</c> retries transient read failures;</item>
/// <item><c>executeIdempotentTransaction</c> retries the <b>whole</b> transaction on
/// serialization failure, deadlock, or a transient connection failure. The work must be safe to
/// re-run from the start, which the action-receipt protocol guarantees: a retried transaction
/// either finds its own committed receipt (duplicate) or starts clean;</item>
/// <item><c>executeOnce</c> never retries, for ambiguous non-idempotent writes.</item>
/// </list>
/// A single statement is never retried from the middle of a transaction, and no gateway or
/// email call may run inside any of these callbacks.
/// </summary>
[<RequireQualifiedAccess>]
module Execution =
    type Options =
        { MaxRetryAttempts: int
          BaseDelay: TimeSpan
          LockTimeout: TimeSpan
          StatementTimeout: TimeSpan }

    let defaults =
        { MaxRetryAttempts = 3
          BaseDelay = TimeSpan.FromMilliseconds 25.
          LockTimeout = TimeSpan.FromSeconds 5.
          StatementTimeout = TimeSpan.FromSeconds 30. }

    /// <summary>40001 serialization_failure and 40P01 deadlock_detected: the transaction was
    /// rolled back as a whole and may be re-run.</summary>
    let isTransactionConflict (error: exn) =
        match error with
        | :? PostgresException as postgres ->
            postgres.SqlState = PostgresErrorCodes.SerializationFailure
            || postgres.SqlState = PostgresErrorCodes.DeadlockDetected
        | _ -> false

    /// <summary>Connection-level failures Npgsql classifies as transient (network, pool
    /// exhaustion timeouts). Server-side errors other than the conflicts above are not.</summary>
    let isTransientConnectionFailure (error: exn) =
        match error with
        | :? PostgresException -> false
        | :? NpgsqlException as npgsql -> npgsql.IsTransient
        | :? TimeoutException -> true
        | _ -> false

    let private retryPipeline (options: Options) (shouldRetry: exn -> bool) =
        ResiliencePipelineBuilder()
            .AddRetry(
                RetryStrategyOptions(
                    MaxRetryAttempts = options.MaxRetryAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = options.BaseDelay,
                    ShouldHandle =
                        Func<RetryPredicateArguments<obj>, ValueTask<bool>>(fun arguments ->
                            match arguments.Outcome.Exception with
                            | null -> ValueTask<bool>(false)
                            | error -> ValueTask<bool>(shouldRetry error))
                )
            )
            .Build()

    let private readPipeline = retryPipeline defaults isTransientConnectionFailure

    let private transactionPipeline =
        retryPipeline defaults (fun error -> isTransactionConflict error || isTransientConnectionFailure error)

    let private run (pipeline: ResiliencePipeline) (ct: CancellationToken) (work: CancellationToken -> Task<'T>) =
        pipeline
            .ExecuteAsync<'T>(Func<CancellationToken, ValueTask<'T>>(fun token -> ValueTask<'T>(work token)), ct)
            .AsTask()

    let private setLocalTimeouts (connection: NpgsqlConnection) (tx: NpgsqlTransaction) (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(ExecutionSql.setLocalTimeouts, connection, tx)

            command.Parameters.AddWithValue("lock_timeout", string (int64 defaults.LockTimeout.TotalMilliseconds))
            |> ignore

            command.Parameters.AddWithValue(
                "statement_timeout",
                string (int64 defaults.StatementTimeout.TotalMilliseconds)
            )
            |> ignore

            let! _ = command.ExecuteNonQueryAsync ct
            ()
        }

    /// <summary>One transaction attempt: commits on <c>Ok</c>, rolls back on <c>Error</c> or an
    /// exception. The connection is returned to the pool before this completes.</summary>
    let private transactOnce
        (dataSource: NpgsqlDataSource)
        (work: NpgsqlConnection -> NpgsqlTransaction -> CancellationToken -> Task<Result<'T, 'E>>)
        (ct: CancellationToken)
        =
        task {
            use connection = dataSource.CreateConnection()
            do! connection.OpenAsync ct
            use! tx = connection.BeginTransactionAsync ct
            do! setLocalTimeouts connection tx ct
            let! result = work connection tx ct

            match result with
            | Ok _ -> do! tx.CommitAsync ct
            | Error _ -> do! tx.RollbackAsync ct

            return result
        }

    let executeRead
        (dataSource: NpgsqlDataSource)
        (ct: CancellationToken)
        (work: NpgsqlConnection -> CancellationToken -> Task<'T>)
        =
        run readPipeline ct (fun token ->
            task {
                use connection = dataSource.CreateConnection()
                do! connection.OpenAsync token
                return! work connection token
            })

    let executeIdempotentTransaction
        (dataSource: NpgsqlDataSource)
        (ct: CancellationToken)
        (work: NpgsqlConnection -> NpgsqlTransaction -> CancellationToken -> Task<Result<'T, 'E>>)
        : Task<Result<'T, 'E>> =
        run transactionPipeline ct (transactOnce dataSource work)

    let executeOnce
        (dataSource: NpgsqlDataSource)
        (ct: CancellationToken)
        (work: NpgsqlConnection -> NpgsqlTransaction -> CancellationToken -> Task<Result<'T, 'E>>)
        : Task<Result<'T, 'E>> =
        transactOnce dataSource work ct
