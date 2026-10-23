namespace Rlcd.Tests

open System
open System.IO
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

open Xunit

open Rlcd

[<Trait("Category", "Unit")>]
module InferenceTests =
    let private ok = function
        | Ok value -> value
        | Error error -> failwithf "Expected success, got %A" error

    let private metadata name elementType dimensions : TensorMetadata =
        { Name = name; ElementType = elementType; Dimensions = dimensions; IsTensor = true }

    let private inputs =
        [ metadata "input_ids" typeof<int64> [ -1; -1 ]
          metadata "attention_mask" typeof<int64> [ -1; -1 ]
          metadata "marker_pos" typeof<int64> [ -1; -1 ]
          metadata "marker_mask" typeof<bool> [ -1; -1 ]
          metadata "qtype" typeof<int64> [ -1 ] ]

    let private outputs =
        [ metadata "logits" typeof<float32> [ -1; -1 ]
          metadata "act_logits" typeof<float32> [ -1; 2 ] ]

    let private output name dimensions values : TensorOutput =
        { Name = name; ElementType = typeof<float32>; Dimensions = dimensions; Values = values }

    let private validOutput batch =
        let rows = SequenceBatch.questionTypes batch |> List.length
        let width = SequenceBatch.markerMask batch |> List.head |> List.length
        [ output "logits" [ int64 rows; int64 width ]
              (Array.init (rows * width) (fun index -> float32 (index % width)))
          output "act_logits" [ int64 rows; 2L ] (Array.zeroCreate (rows * 2)) ]

    type private Runtime(run: SequenceBatch -> CancellationToken -> TensorOutput list,
                         ?inputMetadata: TensorMetadata list,
                         ?outputMetadata: TensorMetadata list,
                         ?cleanup: unit -> unit) =
        let mutable calls = 0
        let mutable disposals = 0
        member _.Calls = Volatile.Read(&calls)
        member _.Disposals = Volatile.Read(&disposals)
        interface IInferenceRuntime with
            member _.Inputs = defaultArg inputMetadata inputs
            member _.Outputs = defaultArg outputMetadata outputs
            member _.Version = "controlled"
            member _.Run(batch, cancellationToken) =
                Interlocked.Increment(&calls) |> ignore
                run batch cancellationToken
            member _.Dispose() =
                Interlocked.Increment(&disposals) |> ignore
                cleanup |> Option.iter (fun dispose -> dispose ())

    let private configuration () =
        let path = Path.GetTempFileName()
        try
            File.WriteAllText(path,
                """{"max_len":512,"head_max_len":192,"temperature":[1,0.1,1],
                    "temperature_by_options":{}}""")
            AgentConfiguration.read path
        finally
            File.Delete path

    let private tokens = SpecialTokens.create 0 1 2 3 "[MASK]" |> ok

    let private encoder =
        { new ITextEncoder with
            member _.Encode text =
                if text = "bad" then Error(TextEncodingError.Failed "controlled encoding failure")
                else Ok [ 10 + text.Length; 10 + (text |> Seq.sumBy int) ] }

    let private provenance =
        { BundleId = "controlled"; GraphSha256 = "controlled"
          ArtifactHashes = [ "controlled", "controlled" ]
          RuntimeVersion = "controlled"; ExecutionProvider = "controlled" }

    let private create runtime =
        Inference.create tokens encoder (configuration ()) provenance runtime

    let private model runtime = create runtime |> ok

    let private option id = ChoiceOption.create id id None |> ok
    let private boolean id = Question.boolean id "Decide" None |> ok
    let private request state questions options =
        DecisionRequest.create { State = state; Questions = questions; Options = options } |> ok
    let private simple state = request state [ boolean "same" ] RequestOptions.defaults
    let private signal () = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let private throwCleanupFailure (error: InvalidOperationException) : unit =
        raise error

    let private observeCleanupFailure (operation: unit -> Task) = task {
        try
            do! operation ()
            return None
        with :? InvalidOperationException as error ->
            return Some error
    }

    let private assertOriginalCleanupFailure expected actual =
        match actual with
        | Some (error: InvalidOperationException) ->
            Assert.Same(expected, error)
            Assert.Contains("throwCleanupFailure", error.StackTrace)
        | None -> failwith "Cleanup fault was incorrectly reported as successful disposal."

    [<Fact>]
    let ``whole batch preparation rejects a bad second item before native execution`` () = task {
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        use owner = model runtime
        let! result = owner.PredictBatch([ simple "good"; simple "bad" ], CancellationToken.None)
        match result with
        | Error(PredictionError.SequenceFailure [ SequenceError.InvalidBatchItem(1, _) ]) -> ()
        | other -> failwithf "Expected second-item error, got %A" other
        Assert.Equal(0, runtime.Calls)
    }

    [<Fact>]
    let ``orders request question and option identities even when question IDs are reused`` () = task {
        let choice = Question.choice "choice" "Choose" [ option "z"; option "a"; option "b" ] |> ok
        let score = Question.score "score" "Rate" [ option "low"; option "high" ] |> ok
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        use owner = model runtime
        let! result =
            owner.PredictBatch(
                [ request "one" [ boolean "same"; choice; score ] RequestOptions.defaults
                  simple "two" ], CancellationToken.None)
        let results = ok result
        Assert.Equal<string list list>(
            [ [ "same"; "choice"; "score" ]; [ "same" ] ],
            results |> List.map (DecisionResult.answers >> List.map (Answer.questionId >> QuestionId.value)))
        let answers = DecisionResult.answers results[0]
        Assert.Equal<string list>(
            [ "z"; "a"; "b" ], Answer.probabilities answers[1] |> List.map (fst >> OptionId.value))
        match Answer.value answers[0], Answer.value answers[1], Answer.value answers[2] with
        | AnswerValue.Boolean p, AnswerValue.Choice id, AnswerValue.Score level ->
            Assert.True(p > 0.5)
            Assert.Equal("b", OptionId.value id)
            Assert.True(level > 0.5)
        | other -> failwithf "Unexpected answer semantics %A" other
        Assert.NotEmpty(Answer.diagnostics answers[2])
        Assert.Equal(provenance, DecisionResult.provenance results[0])
        Assert.Equal(3, (DecisionResult.usage results[0]).QuestionCount)
        Assert.Equal(1, runtime.Calls)
    }

    [<Fact>]
    let ``sequence observations and actual token usage are retained`` () = task {
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        use owner = model runtime
        let! result = owner.PredictBatch([ simple "a [MASK] b" ], CancellationToken.None)
        let result = ok result |> List.head
        Assert.Contains(SequenceDiagnostic.MaskTextReplaced(SequencePart.State, 1),
                        DecisionResult.diagnostics result |> List.concat)
        let usage = DecisionResult.usage result
        Assert.True(usage.InputTokens > 0)
        Assert.Equal(usage.InputTokens, usage.PaddedTokens)
        Assert.Equal(1, usage.NativeRuns)
    }

    [<Fact>]
    let ``chunks are bounded and later native failure returns no partial success`` () = task {
        let mutable calls = 0
        let runtime = new Runtime(fun batch _ ->
            Assert.InRange(SequenceBatch.questionTypes batch |> List.length, 1, 8)
            calls <- calls + 1
            if calls = 2 then
                // ORT exceptions have no public constructor; obtain one from the real IO boundary.
                use invalid = new Microsoft.ML.OnnxRuntime.InferenceSession([| 0uy |])
                failwith "Invalid model bytes unexpectedly loaded"
            validOutput batch)
        use owner = model runtime
        let! result =
            owner.PredictBatch(
                [ request "many" [ for index in 0 .. 8 -> boolean $"q{index}" ] RequestOptions.defaults ],
                CancellationToken.None)
        match result with
        | Error(PredictionError.NativeFailure _) -> ()
        | other -> failwithf "Expected whole-call error, got %A" other
        Assert.Equal(2, runtime.Calls)
        let! recovered = owner.PredictBatch([ simple "recovered" ], CancellationToken.None)
        ok recovered |> ignore
    }

    [<Fact>]
    let ``cancel before run is typed and makes zero native calls`` () = task {
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        use owner = model runtime
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        let! result = owner.PredictBatch([ simple "state" ], cancellation.Token)
        Assert.Equal(Error PredictionError.Cancelled, result)
        Assert.Equal(0, runtime.Calls)
    }

    [<Fact>]
    let ``cancel during run reaches runtime and admission is released`` () = task {
        let entered = signal ()
        let runtime = new Runtime(fun batch cancellation ->
            entered.TrySetResult() |> ignore
            cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds 10.) |> ignore
            cancellation.ThrowIfCancellationRequested()
            validOutput batch)
        use owner = model runtime
        use cancellation = new CancellationTokenSource()
        let pending = owner.PredictBatch([ simple "state" ], cancellation.Token)
        do! entered.Task.WaitAsync(TimeSpan.FromSeconds 10.)
        cancellation.Cancel()
        let! result = pending.WaitAsync(TimeSpan.FromSeconds 10.)
        Assert.Equal(Error PredictionError.Cancelled, result)
        Assert.Equal(0, runtime.Disposals)
    }

    [<Fact>]
    let ``cancel during synchronous encoding returns cancelled before any run and permits reuse`` () = task {
        let entered = signal ()
        use release = new ManualResetEventSlim(false)
        let gatedEncoder =
            { new ITextEncoder with
                member _.Encode text =
                    if text = "paused state" then
                        entered.TrySetResult() |> ignore
                        Assert.True(release.Wait(TimeSpan.FromSeconds 10.))
                    encoder.Encode text }
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        use owner = Inference.create tokens gatedEncoder (configuration ()) provenance runtime |> ok
        use cancellation = new CancellationTokenSource()
        let pending = owner.PredictBatch([ simple "paused state" ], cancellation.Token)
        try
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 10.)
            cancellation.Cancel()
            Assert.False(pending.IsCompleted)
            Assert.Equal(0, runtime.Calls)
        finally
            release.Set()
        let! result = pending.WaitAsync(TimeSpan.FromSeconds 10.)
        Assert.Equal(Error PredictionError.Cancelled, result)
        Assert.Equal(0, runtime.Calls)
        let! recovered = owner.PredictBatch([ simple "reused" ], CancellationToken.None)
        ok recovered |> ignore
        Assert.Equal(1, runtime.Calls)
        Assert.Equal(0, runtime.Disposals)
    }

    [<Fact>]
    let ``active cleanup fault reaches every disposal joiner with original exception and stack`` () = task {
        let entered = signal ()
        use release = new ManualResetEventSlim(false)
        let failure = InvalidOperationException "controlled cleanup failure"
        let runtime = new Runtime(
            (fun batch cancellation ->
                entered.TrySetResult() |> ignore
                Assert.True(release.Wait(TimeSpan.FromSeconds 10.))
                cancellation.ThrowIfCancellationRequested()
                validOutput batch),
            cleanup = (fun () -> throwCleanupFailure failure))
        let owner = model runtime
        let pending = owner.PredictBatch([ simple "active" ], CancellationToken.None)
        try
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 10.)
            let first = (owner :> IAsyncDisposable).DisposeAsync().AsTask()
            let second = (owner :> IAsyncDisposable).DisposeAsync().AsTask()
            Assert.False(first.IsCompleted)
            Assert.False(second.IsCompleted)
            Assert.Equal(0, runtime.Disposals)
            release.Set()
            let! predictionFault =
                observeCleanupFailure (fun () -> pending.WaitAsync(TimeSpan.FromSeconds 10.) :> Task)
            let! firstFault =
                observeCleanupFailure (fun () -> first.WaitAsync(TimeSpan.FromSeconds 10.))
            let! secondFault =
                observeCleanupFailure (fun () -> second.WaitAsync(TimeSpan.FromSeconds 10.))
            let! laterFault =
                observeCleanupFailure (fun () -> (owner :> IAsyncDisposable).DisposeAsync().AsTask())
            for fault in [ predictionFault; firstFault; secondFault; laterFault ] do
                assertOriginalCleanupFailure failure fault
            Assert.Equal(1, runtime.Disposals)
            let! rejected = owner.PredictBatch([ simple "after" ], CancellationToken.None)
            Assert.Equal(Error PredictionError.Disposed, rejected)
        finally
            release.Set()
            (owner :> IDisposable).Dispose()
    }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``idle cleanup fault is retained for later async joiners without repeating cleanup`` asyncFirst = task {
        let failure = InvalidOperationException "idle cleanup failure"
        let runtime = new Runtime(
            (fun batch _ -> validOutput batch),
            cleanup = (fun () -> throwCleanupFailure failure))
        let owner = model runtime
        let! firstFault =
            observeCleanupFailure (fun () ->
                if asyncFirst then
                    (owner :> IAsyncDisposable).DisposeAsync().AsTask()
                else
                    (owner :> IDisposable).Dispose()
                    Task.CompletedTask)
        let! secondFault =
            observeCleanupFailure (fun () -> (owner :> IAsyncDisposable).DisposeAsync().AsTask())
        let! thirdFault =
            observeCleanupFailure (fun () -> (owner :> IAsyncDisposable).DisposeAsync().AsTask())
        for fault in [ firstFault; secondFault; thirdFault ] do
            assertOriginalCleanupFailure failure fault
        (owner :> IDisposable).Dispose()
        Assert.Equal(1, runtime.Disposals)
        Assert.Equal(0, runtime.Calls)
        let! rejected = owner.PredictBatch([ simple "after" ], CancellationToken.None)
        Assert.Equal(Error PredictionError.Disposed, rejected)
    }

    [<Fact>]
    let ``concurrent calls reject capacity without queueing and dispose joins active work`` () = task {
        let entered = signal ()
        let release = new ManualResetEventSlim(false)
        let runtime = new Runtime(fun batch cancellation ->
            entered.TrySetResult() |> ignore
            cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds 10.) |> ignore
            release.Wait(TimeSpan.FromSeconds 10.) |> ignore
            cancellation.ThrowIfCancellationRequested()
            validOutput batch)
        let owner = model runtime
        try
            let pending = owner.PredictBatch([ simple "state" ], CancellationToken.None)
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 10.)
            let! rejected = owner.PredictBatch([ simple "other" ], CancellationToken.None)
            Assert.Equal(Error(PredictionError.CapacityExceeded("activeCalls", 1)), rejected)
            (owner :> IDisposable).Dispose()
            let closing = (owner :> IAsyncDisposable).DisposeAsync().AsTask()
            Assert.False(closing.IsCompleted)
            Assert.Equal(0, runtime.Disposals)
            let! disposed = owner.PredictBatch([ simple "later" ], CancellationToken.None)
            Assert.Equal(Error PredictionError.Disposed, disposed)
            release.Set()
            let! result = pending.WaitAsync(TimeSpan.FromSeconds 10.)
            Assert.Equal(Error PredictionError.Disposed, result)
            do! closing.WaitAsync(TimeSpan.FromSeconds 10.)
            Assert.Equal(1, runtime.Disposals)
            (owner :> IDisposable).Dispose()
            Assert.Equal(1, runtime.Disposals)
        finally
            release.Set()
            (owner :> IDisposable).Dispose()
            release.Dispose()
    }

    [<Fact>]
    let ``idle disposal is immediate idempotent and postdispose calls are rejected`` () = task {
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        let owner = model runtime
        (owner :> IDisposable).Dispose()
        (owner :> IDisposable).Dispose()
        do! (owner :> IAsyncDisposable).DisposeAsync()
        Assert.Equal(1, runtime.Disposals)
        let! result = owner.PredictBatch([ simple "state" ], CancellationToken.None)
        Assert.Equal(Error PredictionError.Disposed, result)
    }

    [<Theory>]
    [<InlineData("name")>]
    [<InlineData("type")>]
    [<InlineData("rank")>]
    [<InlineData("dimension")>]
    [<InlineData("tensor")>]
    let ``incompatible metadata rejects load and releases owned runtime`` problem =
        let first = inputs.Head
        let changed =
            match problem with
            | "name" -> { first with Name = "wrong" }
            | "type" -> { first with ElementType = typeof<int> }
            | "rank" -> { first with Dimensions = [ -1 ] }
            | "dimension" -> { first with Dimensions = [ 1; 512 ] }
            | _ -> { first with IsTensor = false }
        let runtime = new Runtime((fun batch _ -> validOutput batch), inputMetadata = changed :: inputs.Tail)
        match create runtime with
        | Error(ModelLoadError.IncompatibleModel _) -> ()
        | other -> failwithf "Expected incompatible model, got %A" other
        Assert.Equal(1, runtime.Disposals)

    [<Theory>]
    [<InlineData("shape")>]
    [<InlineData("action")>]
    [<InlineData("length")>]
    [<InlineData("nonfinite-padding")>]
    [<InlineData("type")>]
    [<InlineData("missing")>]
    [<InlineData("duplicate")>]
    let ``native output guards reject malformed or nonfinite padded outputs`` problem = task {
        let runtime = new Runtime(fun batch _ ->
            let values = validOutput batch
            let first = values.Head
            match problem with
            | "shape" -> { first with Dimensions = [ 1L; 3L ] } :: values.Tail
            | "action" -> [ first; { values[1] with Dimensions = [ 2L; 3L ] } ]
            | "length" -> { first with Values = [||] } :: values.Tail
            | "nonfinite-padding" ->
                let data = Array.copy first.Values
                data[2] <- Single.NaN
                { first with Values = data } :: values.Tail
            | "type" -> { first with ElementType = typeof<double> } :: values.Tail
            | "missing" -> values.Tail
            | _ -> first :: values)
        use owner = model runtime
        let large = Question.choice "large" "Choose" [ option "a"; option "b"; option "c" ] |> ok
        let! result =
            owner.PredictBatch(
                [ request "state" [ boolean "small"; large ] RequestOptions.defaults ],
                CancellationToken.None)
        match result with
        | Error(PredictionError.InvalidOutput _) -> ()
        | other -> failwithf "Expected output rejection, got %A" other
    }

    [<Fact>]
    let ``finite float32 extremes and masked padding decode safely`` () = task {
        let runtime = new Runtime(fun batch _ ->
            let values = validOutput batch
            { values.Head with Values = [| Single.MinValue; Single.MaxValue; Single.MaxValue;
                                          Single.MaxValue; Single.MinValue; 0.0f |] } :: values.Tail)
        use owner = model runtime
        let large = Question.choice "large" "Choose" [ option "a"; option "b"; option "c" ] |> ok
        let! result =
            owner.PredictBatch(
                [ request "state" [ boolean "small"; large ] RequestOptions.defaults ],
                CancellationToken.None)
        let answers = result |> ok |> List.head |> DecisionResult.answers
        Assert.Equal(2, Answer.probabilities answers.Head |> List.length)
        Assert.Equal(AnswerValue.Boolean 1., Answer.value answers.Head)
    }

    [<Fact>]
    let ``model budgets and whole call capacity are checked before running`` () = task {
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        use owner = model runtime
        let options =
            RequestOptions.create (TokenBudget.create 513 192 48 |> ok) TruncationPolicy.Reject |> ok
        let! excessive =
            owner.PredictBatch(
                [ simple "good"; request "bad" [ boolean "q" ] options ], CancellationToken.None)
        Assert.Equal(Error(PredictionError.ModelLimitExceeded(1, "sequenceLength", 513, 512)), excessive)
        let! capacity =
            owner.PredictBatch(List.replicate 65 (simple "state"), CancellationToken.None)
        Assert.Equal(Error(PredictionError.CapacityExceeded("requests", 64)), capacity)
        let! questions =
            owner.PredictBatch(
                [ request "state" [ for index in 0 .. 128 -> boolean $"q{index}" ] RequestOptions.defaults ],
                CancellationToken.None)
        Assert.Equal(Error(PredictionError.CapacityExceeded("questions", 128)), questions)
        Assert.Equal(0, runtime.Calls)
    }

    [<Fact>]
    let ``native tensors preserve int64 bool qtype and existing padding`` () =
        let large = Question.choice "large" "Choose" [ option "a"; option "b"; option "c" ] |> ok
        let batch =
            Sequence.buildBatch tokens encoder
                [ request "state" [ boolean "small"; large ] RequestOptions.defaults ] |> ok
        use tensors = InferenceTensors.create batch
        let values = tensors.Values
        Assert.Equal<string list>(
            [ "input_ids"; "attention_mask"; "marker_pos"; "marker_mask"; "qtype" ], tensors.Names |> Seq.toList)
        Assert.Equal<int64 array>(
            SequenceBatch.inputIds batch |> List.concat |> List.map (TokenId.value >> int64) |> List.toArray,
            values[0].GetTensorDataAsSpan<int64>().ToArray())
        Assert.Equal<int64 array>(
            SequenceBatch.attentionMask batch |> List.concat |> List.map int64 |> List.toArray,
            values[1].GetTensorDataAsSpan<int64>().ToArray())
        Assert.Equal<int64 array>(
            SequenceBatch.markerPositions batch |> List.concat |> List.map (TokenPosition.value >> int64) |> List.toArray,
            values[2].GetTensorDataAsSpan<int64>().ToArray())
        Assert.Equal<bool array>([| true; true; false; true; true; true |],
                                 values[3].GetTensorDataAsSpan<bool>().ToArray())
        Assert.Equal<int64 array>([| 2L; 0L |], values[4].GetTensorDataAsSpan<int64>().ToArray())

    [<Fact>]
    let ``successful multiple chunks retain grouping and accurate per request usage`` () = task {
        let mutable calls = 0
        let runtime = new Runtime(fun batch _ ->
            calls <- calls + 1
            let values = validOutput batch
            let first = values.Head
            let data =
                Array.init first.Values.Length (fun index ->
                    if calls = 1 then float32 (index % 2) else float32 (1 - index % 2))
            { first with Values = data } :: values.Tail)
        use owner = model runtime
        let! result =
            owner.PredictBatch(
                [ request "many" [ for index in 0 .. 8 -> boolean $"q{index}" ] RequestOptions.defaults
                  simple "second" ], CancellationToken.None)
        let results = ok result
        Assert.Equal(2, results.Length)
        Assert.Equal(9, (DecisionResult.answers results[0]).Length)
        Assert.Equal(1, (DecisionResult.answers results[1]).Length)
        Assert.Equal(2, (DecisionResult.usage results[0]).NativeRuns)
        Assert.Equal(1, (DecisionResult.usage results[1]).NativeRuns)
        let probabilities =
            results |> List.collect DecisionResult.answers
            |> List.map (fun answer ->
                match Answer.value answer with
                | AnswerValue.Boolean probability -> probability
                | _ -> failwith "Expected boolean")
        Assert.All(probabilities |> List.take 8, fun probability -> Assert.True(probability > 0.5))
        Assert.All(probabilities |> List.skip 8, fun probability -> Assert.True(probability < 0.5))
        Assert.Equal(2, calls)
    }

    [<Fact>]
    let ``facade single and batch operations use the same owner and cleanup`` () = task {
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        use facade = new DecisionModel(model runtime)
        let! single = facade.Predict(simple "one", CancellationToken.None)
        let! batch = facade.PredictBatch([ simple "one" ], CancellationToken.None)
        Assert.Equal(ok single, ok batch |> List.head)
        do! (facade :> IAsyncDisposable).DisposeAsync()
        Assert.Equal(2, runtime.Calls)
        Assert.Equal(1, runtime.Disposals)
        let! after = facade.Predict(simple "after", CancellationToken.None)
        Assert.Equal(Error PredictionError.Disposed, after)
    }

    [<Fact>]
    let ``empty null and fabricated null requests are typed failures with no runs`` () = task {
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        use owner = model runtime
        for requests in [ []; Unchecked.defaultof<DecisionRequest list>; [ Unchecked.defaultof<_> ] ] do
            let! result = owner.PredictBatch(requests, CancellationToken.None)
            match result with
            | Error(PredictionError.InvalidRequests _) -> ()
            | other -> failwithf "Expected request rejection, got %A" other
        Assert.Equal(0, runtime.Calls)
    }

    [<Theory>]
    [<InlineData(512, 193, 48, "headLength", 193, 192)>]
    [<InlineData(512, 192, 49, "optionTokens", 49, 48)>]
    let ``head and option model limits are enforced`` sequence head option field requested maximum = task {
        let runtime = new Runtime(fun batch _ -> validOutput batch)
        use owner = model runtime
        let options =
            RequestOptions.create (TokenBudget.create sequence head option |> ok) TruncationPolicy.Reject |> ok
        let! result =
            owner.PredictBatch([ request "state" [ boolean "q" ] options ], CancellationToken.None)
        Assert.Equal(Error(PredictionError.ModelLimitExceeded(0, field, requested, maximum)), result)
        Assert.Equal(0, runtime.Calls)
    }

    [<Fact>]
    let ``metadata output incompatibility releases runtime without running`` () =
        let runtime = new Runtime(
            (fun batch _ -> validOutput batch),
            outputMetadata = [ outputs.Head; { outputs[1] with Dimensions = [ -1; 3 ] } ])
        match create runtime with
        | Error(ModelLoadError.IncompatibleModel _) -> ()
        | other -> failwithf "Expected incompatible model, got %A" other
        Assert.Equal(0, runtime.Calls)
        Assert.Equal(1, runtime.Disposals)

[<Trait("Category", "RealInference")>]
module RealInferenceTests =
    let private ok = function
        | Ok value -> value
        | Error error -> failwithf "Real inference failed: %A" error

    [<Fact>]
    let ``verified cached bundle loads and predicts all question kinds through real CPU runtime`` () = task {
        let directory =
            Environment.GetEnvironmentVariable "RLCD_MODELS_DIR"
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                             ".cache", "rlcd", "laya-english"))
        let manifest =
            Environment.GetEnvironmentVariable "RLCD_MODEL_MANIFEST"
            |> Option.ofObj
            |> Option.defaultValue (Path.Combine(directory, "laya-english.manifest.json"))
        // Missing inputs deliberately fail this explicitly selected test; there is no skip/download.
        use model = DecisionModel.Load(directory, manifest) |> ok
        let option id label = ChoiceOption.create id label None |> ok
        let questions =
            [ Question.choice "topic" "Select the topic."
                  [ option "billing" "Billing"; option "technical" "Technical"; option "other" "Other" ] |> ok
              Question.score "urgency" "Rate the urgency."
                  [ option "low" "Low"; option "high" "High" ] |> ok
              Question.boolean "follow-up" "Does the customer request a refund?" None |> ok ]
        let request state =
            DecisionRequest.create { State = state; Questions = questions; Options = RequestOptions.defaults }
            |> ok
        let first = request "The customer was charged twice and requests a refund."
        let second = request "The screen is blank after the update."
        use deadline = new CancellationTokenSource(TimeSpan.FromMinutes 3.)
        let! batch = model.PredictBatch([ first; second ], deadline.Token)
        let results = ok batch
        Assert.Equal(2, results.Length)
        for result in results do
            let answers = DecisionResult.answers result
            Assert.Equal<string list>(
                [ "topic"; "urgency"; "follow-up" ],
                answers |> List.map (Answer.questionId >> QuestionId.value))
            for answer in answers do
                let probabilities = Answer.probabilities answer |> List.map snd
                Assert.All(probabilities, fun probability ->
                    Assert.True(Double.IsFinite probability)
                    Assert.InRange(probability, 0., 1.))
                Assert.InRange(List.sum probabilities, 0.999999, 1.000001)
            let provenance = DecisionResult.provenance result
            Assert.Equal("laya-english", provenance.BundleId)
            Assert.Equal("1.29.0", provenance.RuntimeVersion)
            Assert.Equal("CPUExecutionProvider", provenance.ExecutionProvider)
            Assert.Equal(4, provenance.ArtifactHashes.Length)
            Assert.Contains(
                ("tokenizer.json", "6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30"),
                provenance.ArtifactHashes)
            Assert.Contains(
                ("rl_agent_config.json", "ae287b56bbcf5f8c4f4541ae9dfd00c914c4c48b940b8398c3058af37ba92bbd"),
                provenance.ArtifactHashes)
            Assert.Equal(
                "ce529df16bae1488504f9e5bf5120112e80a36076d16624df7703a7679d1996e",
                provenance.GraphSha256)
            printfn "Real CPU callability: %A; usage=%A; no independent parity assertion."
                provenance (DecisionResult.usage result)
        let! single = model.Predict(first, deadline.Token)
        let answers = single |> ok |> DecisionResult.answers
        Assert.Equal(3, answers.Length)
        match answers |> List.map Answer.value with
        | [ AnswerValue.Choice _; AnswerValue.Score _; AnswerValue.Boolean _ ] -> ()
        | other -> failwithf "Unexpected native answer kinds %A" other
        do! (model :> IAsyncDisposable).DisposeAsync()
        let! disposed = model.Predict(first, CancellationToken.None)
        Assert.Equal(Error PredictionError.Disposed, disposed)
    }
