namespace Rlcd

open System
open System.Threading
open System.Threading.Tasks

/// One owned CPU inference session for the pinned local bundle.
/// One call at a time; excess calls fail immediately. No background queue or model acquisition.
type DecisionModel internal (owner: InferenceOwner) =
    /// Verify the manifest, artifact hashes and sidecars, then load and validate one CPU session.
    /// The caller must keep the bundle directory immutable during loading.
    static member Load
        (modelDirectory: string, manifestPath: string)
        : Result<DecisionModel, ModelLoadError> =
        Inference.load modelDirectory manifestPath
        |> Result.map (fun owner -> new DecisionModel(owner))

    /// Prepare the entire batch before native Run, then execute chunks of at most eight questions.
    /// At most 64 requests and 128 total questions are admitted. Any failure discards every answer.
    /// Cancellation terminates native Run and returns a typed error, not a cancelled Task.
    /// Cancellation during synchronous tokenization is observed when preparation returns.
    member _.PredictBatch
        (requests: DecisionRequest list, cancellationToken: CancellationToken)
        : Task<Result<DecisionResult list, PredictionError>> =
        owner.PredictBatch(requests, cancellationToken)

    /// Predict one validated request with PredictBatch's limits, diagnostics and cancellation.
    member _.Predict
        (request: DecisionRequest, cancellationToken: CancellationToken)
        : Task<Result<DecisionResult, PredictionError>> = task {
        let! result = owner.PredictBatch([ request ], cancellationToken)
        return result |> Result.map List.head
    }

    /// Reject new calls and request termination. If idle, release resources before returning.
    /// Otherwise the active prediction owns cleanup. Use DisposeAsync to await resource release.
    /// A cleanup failure propagates unchanged to the releasing call and later async joiners.
    interface IDisposable with
        member _.Dispose() = (owner :> IDisposable).Dispose()

    /// Reject/terminate as Dispose does, then await release of the one owned session.
    /// Every async join preserves the cleanup exception on failure; cleanup is not retried.
    interface IAsyncDisposable with
        member _.DisposeAsync() = (owner :> IAsyncDisposable).DisposeAsync()
