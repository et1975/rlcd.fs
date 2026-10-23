namespace Rlcd.Router

open System
open System.Threading
open System.Threading.Tasks

open Rlcd

type internal IRouterInference =
    abstract Predict :
        request: DecisionRequest * cancellationToken: CancellationToken ->
            Task<Result<RoutingPrediction, PredictionError>>

[<RequireQualifiedAccess>]
module internal RouterInference =
    let answer (answer: Answer) : RoutingAnswer =
        {
            QuestionId = Answer.questionId answer
            Value = Answer.value answer
            Probabilities = Answer.probabilities answer
            CalibrationDiagnostics = Answer.diagnostics answer
        }

    let private prediction (result: DecisionResult) : RoutingPrediction =
        {
            Answers = result |> DecisionResult.answers |> List.map answer
            Diagnostics = DecisionResult.diagnostics result
            Usage = DecisionResult.usage result
            Provenance = DecisionResult.provenance result
        }

    let borrow (model: DecisionModel) : IRouterInference =
        if isNull (box model) then nullArg (nameof model)
        { new IRouterInference with
            member _.Predict(request, cancellationToken) = task {
                let! result = model.Predict(request, cancellationToken)
                return result |> Result.map prediction
            } }

/// Asynchronous recommendation over one borrowed, already-loaded generic DecisionModel.
/// Does not load, dispose, queue, retry, dispatch, or mutate the catalog.
type Router internal (inference: IRouterInference, thresholds: Thresholds, catalog: Catalog) =
    /// Borrow the caller's loaded model with a fixed catalog and conservative policy configuration.
    new (model: DecisionModel, thresholds: Thresholds, catalog: Catalog) =
        Router(RouterInference.borrow model, thresholds, catalog)

    /// Preserve fixed choices and unsupported-scope abstentions without inference.
    /// Automatic supported calls forward cancellation and retain core admission/error precedence.
    /// Unexpected task faults propagate; they are never converted into ordinary abstentions.
    member _.Recommend
        (request: RoutingRequest, cancellationToken: CancellationToken)
        : Task<Result<RoutingResult, RoutingError>> = task {
        match RoutingResult.bypass thresholds request catalog with
        | Some result -> return Ok result
        | None ->
            match Rubric.request request.Task with
            | Error errors -> return Error(RoutingError.RequestConstruction errors)
            | Ok input ->
                let! prediction = inference.Predict(input, cancellationToken)
                return
                    match prediction with
                    | Ok value -> value |> RoutingResult.fromPrediction thresholds request catalog
                    | Error error -> RoutingResult.failure thresholds catalog error
    }
