namespace Rlcd

open System
open System.IO
open System.Threading
open System.Threading.Tasks

open Microsoft.ML.OnnxRuntime

/// Local bundle or CPU runtime loading failure. Loading never acquires model files.
[<RequireQualifiedAccess>]
type ModelLoadError =
    /// A required path was null or blank.
    | InvalidArgument of field: string
    /// A local manifest, graph, sidecar, or directory is absent.
    | MissingArtifact of path: string
    /// Integrity or supported sidecar/manifest validation failed.
    | InvalidBundle of message: string
    /// Native graph tensor names, types, ranks, or dimensions are incompatible.
    | IncompatibleModel of details: string list
    /// ONNX Runtime or its native library could not load the session.
    | NativeFailure of message: string
    /// A local filesystem operation failed.
    | IoFailure of message: string

/// An all-or-nothing prediction failure; never contains partial answers.
[<RequireQualifiedAccess>]
type PredictionError =
    /// An empty/null batch or fabricated null validated request was supplied.
    | InvalidRequests of ValidationError list
    /// A request budget exceeds the pinned model's supported limit.
    | ModelLimitExceeded of requestIndex: int * field: string * requested: int * maximum: int
    /// The one active-call slot or a whole-call bound was exceeded; there is no waiting queue.
    | CapacityExceeded of resource: string * maximum: int
    /// Existing sequence preparation rejected at least one request.
    | SequenceFailure of SequenceError list
    /// Native outputs have invalid names, types, shapes, lengths, or finite values.
    | InvalidOutput of details: string list
    /// Existing calibration rejected the indicated zero-based question row.
    | CalibrationFailure of questionIndex: int * errors: CalibrationError list
    /// Native execution failed.
    | NativeFailure of message: string
    /// Caller cancellation was observed; ONNX termination was requested for any active run.
    | Cancelled
    /// The owner was disposed, including while this prediction was active.
    | Disposed

/// Actual artifact/runtime identity, not a parity or calibration certification.
type ModelProvenance =
    {
        /// Validated bundle identifier.
        BundleId: string
        /// Verified graph SHA-256.
        GraphSha256: string
        /// Verified manifest-relative artifact paths and hashes, including every sidecar.
        ArtifactHashes: (string * string) list
        /// Native runtime version reported by OrtEnv.
        RuntimeVersion: string
        /// The execution provider selected by this implementation.
        ExecutionProvider: string
    }

/// Actual assembled-input usage for one request within a successful call.
type PredictionUsage =
    {
        /// Number of question rows.
        QuestionCount: int
        /// Sum of unpadded assembled sequence lengths, after any diagnosed truncation.
        InputTokens: int
        /// Sum of row widths actually submitted, including native-chunk padding.
        PaddedTokens: int
        /// Number of native chunks containing this request's rows; shared calls are counted.
        NativeRuns: int
    }

/// An immutable request result; answers and diagnostic groups retain question order.
type DecisionResult =
    private
        {
            Answers: Answer list
            Diagnostics: SequenceDiagnostic list list
            Usage: PredictionUsage
            Provenance: ModelProvenance
        }

/// Ordered prediction read-out.
[<RequireQualifiedAccess>]
module DecisionResult =
    let internal create answers diagnostics usage provenance =
        { Answers = answers; Diagnostics = diagnostics; Usage = usage; Provenance = provenance }

    /// Return answers in the original question order.
    let answers (result: DecisionResult) : Answer list = result.Answers

    /// Return all sequence observations grouped by question; shared-state observations repeat.
    let diagnostics (result: DecisionResult) : SequenceDiagnostic list list = result.Diagnostics

    /// Return actual assembled/native input usage.
    let usage (result: DecisionResult) : PredictionUsage = result.Usage

    /// Return verified artifact and actual runtime identity, without a certification assertion.
    let provenance (result: DecisionResult) : ModelProvenance = result.Provenance

type internal TensorMetadata =
    { Name: string; ElementType: Type; Dimensions: int list; IsTensor: bool }

type internal TensorOutput =
    { Name: string; ElementType: Type; Dimensions: int64 list; Values: float32 array }

/// The synchronous external runtime boundary; ownership transfers to Inference.create.
type internal IInferenceRuntime =
    inherit IDisposable
    abstract Inputs: TensorMetadata list
    abstract Outputs: TensorMetadata list
    abstract Version: string
    abstract Run: batch: SequenceBatch * cancellationToken: CancellationToken -> TensorOutput list

[<RequireQualifiedAccess>]
module internal InferenceContract =
    let private metadata name elementType dimensions =
        { Name = name; ElementType = elementType; Dimensions = dimensions; IsTensor = true }

    let inputs =
        [
            metadata "input_ids" typeof<int64> [ -1; -1 ]
            metadata "attention_mask" typeof<int64> [ -1; -1 ]
            metadata "marker_pos" typeof<int64> [ -1; -1 ]
            metadata "marker_mask" typeof<bool> [ -1; -1 ]
            metadata "qtype" typeof<int64> [ -1 ]
        ]

    let outputs =
        [
            metadata "logits" typeof<float32> [ -1; -1 ]
            metadata "act_logits" typeof<float32> [ -1; 2 ]
        ]

    let private checkMetadata (expected: TensorMetadata list) (actual: TensorMetadata list) =
        if (actual |> List.sortBy _.Name) = (expected |> List.sortBy _.Name) then []
        else
            [ "Tensor names, types, ranks, or dimensions differ from laya-decision-v1." ]

    let validate (runtime: IInferenceRuntime) =
        match checkMetadata inputs runtime.Inputs @ checkMetadata outputs runtime.Outputs with
        | [] -> Ok ()
        | errors -> Error(ModelLoadError.IncompatibleModel errors)

    let outputErrors batch (outputs: TensorOutput list) =
        let rows = SequenceBatch.questionTypes batch |> List.length |> int64
        let width = SequenceBatch.markerMask batch |> List.head |> List.length |> int64
        let expected = [ "logits", [ rows; width ]; "act_logits", [ rows; 2L ] ]
        [
            if outputs |> List.map _.Name |> List.sort <> [ "act_logits"; "logits" ] then
                "Expected exactly logits and act_logits outputs."
            for tensor in outputs do
                match List.tryFind (fun (name, _) -> name = tensor.Name) expected with
                | None -> ()
                | Some (_, dimensions) ->
                    if tensor.ElementType <> typeof<float32> then
                        $"{tensor.Name}: expected float32."
                    if tensor.Dimensions <> dimensions then
                        $"{tensor.Name}: invalid output shape."
                    if tensor.Values.LongLength <> List.fold (*) 1L dimensions then
                        $"{tensor.Name}: invalid output element count."
                    if tensor.Values |> Array.exists (Single.IsFinite >> not) then
                        $"{tensor.Name}: nonfinite output, including padding."
        ]

/// Per-call pinned managed buffers; creation unwinds every successfully created OrtValue.
type internal InferenceTensors(names: string array, values: OrtValue array) =
    member _.Names = names
    member _.Values = values
    interface IDisposable with
        member _.Dispose() = values |> Array.iter _.Dispose()

[<RequireQualifiedAccess>]
module internal InferenceTensors =
    let private append projection shape rows (values: ResizeArray<OrtValue>) =
        let data = rows |> List.concat |> List.map projection |> List.toArray
        values.Add(OrtValue.CreateTensorValueFromMemory(data, shape))

    let private fill batch values =
        let rows = SequenceBatch.questionTypes batch |> List.length |> int64
        let tokens = SequenceBatch.inputIds batch
        let markers = SequenceBatch.markerPositions batch
        let tokenShape = [| rows; int64 tokens.Head.Length |]
        let markerShape = [| rows; int64 markers.Head.Length |]
        append (TokenId.value >> int64) tokenShape tokens values
        append int64 tokenShape (SequenceBatch.attentionMask batch) values
        append (TokenPosition.value >> int64) markerShape markers values
        append id markerShape (SequenceBatch.markerMask batch) values
        append int64 [| rows |] [ SequenceBatch.questionTypes batch ] values

    let create batch =
        let values = ResizeArray<OrtValue>()
        let mutable transferred = false
        try
            fill batch values
            let result = new InferenceTensors(
                [| "input_ids"; "attention_mask"; "marker_pos"; "marker_mask"; "qtype" |],
                values.ToArray())
            transferred <- true
            result
        finally
            if not transferred then values |> Seq.iter _.Dispose()

[<RequireQualifiedAccess>]
module private NativeOutput =
    let copy name (value: OrtValue) =
        let shape = value.GetTensorTypeAndShape()
        let elementType =
            if shape.ElementDataType = Tensors.TensorElementType.Float then typeof<float32>
            else typeof<obj>
        {
            Name = name
            ElementType = elementType
            Dimensions = shape.Shape |> Array.toList
            Values =
                if elementType = typeof<float32> then value.GetTensorDataAsSpan<float32>().ToArray()
                else [||]
        }

/// CPU-only session. Run registrations die before RunOptions, inputs, and session ownership.
type private OrtRuntime(session: InferenceSession) =
    let metadata (items: Collections.Generic.IReadOnlyDictionary<string, NodeMetadata>) =
        items
        |> Seq.map (fun pair ->
            let node = pair.Value
            {
                Name = pair.Key
                ElementType = if node.IsTensor then node.ElementType else typeof<obj>
                Dimensions = if node.IsTensor then node.Dimensions |> Array.toList else []
                IsTensor = node.IsTensor
            })
        |> Seq.toList

    interface IInferenceRuntime with
        member _.Inputs = metadata session.InputMetadata
        member _.Outputs = metadata session.OutputMetadata
        member _.Version = OrtEnv.Instance().GetVersionString()
        member _.Dispose() = session.Dispose()
        member _.Run(batch, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()
            use inputs = InferenceTensors.create batch
            use options = new RunOptions()
            use registration = cancellationToken.Register(fun () -> options.Terminate <- true)
            cancellationToken.ThrowIfCancellationRequested()
            let names = [| "logits"; "act_logits" |]
            use results = session.Run(options, inputs.Names, inputs.Values, names)
            cancellationToken.ThrowIfCancellationRequested()
            results
            |> Seq.mapi (fun index value -> NativeOutput.copy names[index] value)
            |> Seq.toList

    static member Create(graphPath: string) =
        use options = new SessionOptions()
        options.ExecutionMode <- ExecutionMode.ORT_SEQUENTIAL
        options.InterOpNumThreads <- 1
        options.IntraOpNumThreads <- max 1 (min 4 Environment.ProcessorCount)
        options.AppendExecutionProvider_CPU(1)
        new OrtRuntime(new InferenceSession(graphPath, options)) :> IInferenceRuntime

/// Atomic zero-queue admission and synchronous ownership handoff, not a background worker.
/// States: idle=0, active=1, cancelling-active=2, closing-active=3,
/// cancelling-idle=4, released=5. The cancellation caller and active call hand off cleanup.
type private SessionLifetime(runtime: IInferenceRuntime) =
    let shutdown = new CancellationTokenSource()
    let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable state = 0

    let release () =
        try
            try runtime.Dispose()
            finally
                shutdown.Dispose()
                Volatile.Write(&state, 5)
            completion.TrySetResult() |> ignore
        with error ->
            // Retain every cleanup fault for all joiners, then preserve the original throw.
            completion.TrySetException error |> ignore
            reraise()

    member _.Token = shutdown.Token
    member _.IsClosed = Volatile.Read(&state) >= 2
    member _.Completion = completion.Task

    member _.TryEnter() =
        match Interlocked.CompareExchange(&state, 1, 0) with
        | 0 -> Ok ()
        | 1 -> Error(PredictionError.CapacityExceeded("activeCalls", 1))
        | _ -> Error PredictionError.Disposed

    member this.Leave() =
        match Volatile.Read(&state) with
        | 1 -> if Interlocked.CompareExchange(&state, 0, 1) <> 1 then this.Leave()
        | 2 -> if Interlocked.CompareExchange(&state, 4, 2) <> 2 then this.Leave()
        | 3 ->
            if Interlocked.CompareExchange(&state, 5, 3) = 3 then release ()
            else this.Leave()
        | _ -> ()

    member this.Close() =
        match Volatile.Read(&state) with
        | 0 ->
            if Interlocked.CompareExchange(&state, 4, 0) = 0 then
                try shutdown.Cancel()
                finally release ()
            else this.Close()
        | 1 ->
            if Interlocked.CompareExchange(&state, 2, 1) = 1 then
                try shutdown.Cancel()
                finally
                    if Interlocked.CompareExchange(&state, 3, 2) <> 2 then release ()
            else this.Close()
        | _ -> ()

[<RequireQualifiedAccess>]
module private Prediction =
    let private budgetError index request =
        let budget = request |> DecisionRequest.options |> RequestOptions.budget
        [
            "sequenceLength", TokenBudget.sequenceLength budget, 512
            "headLength", TokenBudget.headLength budget, 192
            "optionTokens", TokenBudget.optionTokens budget, 48
        ]
        |> List.tryPick (fun (field, requested, maximum) ->
            if requested > maximum then
                Some(PredictionError.ModelLimitExceeded(index, field, requested, maximum))
            else None)

    let validate requests =
        let errors =
            Validation.collection "requests" requests
            @ [
                if not (isNull (box requests)) && List.isEmpty requests then
                    ValidationError.EmptyBatch
              ]
        if not errors.IsEmpty then Error(PredictionError.InvalidRequests errors)
        elif requests.Length > 64 then Error(PredictionError.CapacityExceeded("requests", 64))
        elif requests |> List.sumBy (DecisionRequest.questions >> List.length) > 128 then
            Error(PredictionError.CapacityExceeded("questions", 128))
        else
            match requests |> List.mapi budgetError |> List.tryPick id with
            | Some error -> Error error
            | None -> Ok requests

    let prepare tokens encoder requests =
        validate requests
        |> Result.bind (fun requests ->
            Sequence.buildBatch tokens encoder requests
            |> Result.mapError PredictionError.SequenceFailure)
        |> Result.bind (fun batch ->
            SequenceBatch.chunks (SpecialTokens.padding tokens) 8 batch
            |> Result.mapError PredictionError.SequenceFailure
            |> Result.map (fun chunks -> batch, chunks))

    let private decodeRow configuration offset (logits: TensorOutput, actions: TensorOutput) =
        fun index (question, mask) ->
            let width = List.length mask
            Answer.decode configuration
                {
                    Question = question
                    OptionLogits =
                        logits.Values[index * width .. (index + 1) * width - 1] |> Array.toList
                    OptionMask = mask
                    ActionLogits = actions.Values[index * 2 .. index * 2 + 1] |> Array.toList
                }
            |> Result.mapError (fun errors ->
                PredictionError.CalibrationFailure(offset + index, errors))

    let decode configuration offset questions batch outputs =
        match InferenceContract.outputErrors batch outputs with
        | errors when not errors.IsEmpty -> Error(PredictionError.InvalidOutput errors)
        | _ ->
            let logits = outputs |> List.find (fun tensor -> tensor.Name = "logits")
            let actions = outputs |> List.find (fun tensor -> tensor.Name = "act_logits")
            List.zip questions (SequenceBatch.markerMask batch)
            |> List.mapi (decodeRow configuration offset (logits, actions))
            |> List.foldBack (fun result rest ->
                match result, rest with
                | Ok answer, Ok answers -> Ok(answer :: answers)
                | Error error, _ | _, Error error -> Error error) <| Ok []

    let results provenance batch chunks answers =
        let padded =
            chunks
            |> List.mapi (fun index chunk ->
                SequenceBatch.inputIds chunk |> List.map (fun row -> index, row.Length))
            |> List.concat
        let groups = SequenceBatch.sequences batch
        ((answers, padded), groups)
        ||> List.mapFold (fun (remainingAnswers, remainingPadded) sequences ->
            let count = sequences.Length
            let selected, rest = List.splitAt count remainingAnswers
            let usageRows, restRows = List.splitAt count remainingPadded
            let usage =
                {
                    QuestionCount = count
                    InputTokens = sequences |> List.sumBy (QuestionSequence.inputIds >> List.length)
                    PaddedTokens = usageRows |> List.sumBy snd
                    NativeRuns = usageRows |> List.map fst |> List.distinct |> List.length
                }
            let diagnostics = sequences |> List.map QuestionSequence.diagnostics
            DecisionResult.create selected diagnostics usage provenance, (rest, restRows))
        |> fst

/// One admitted call includes preparation, every native chunk, and result decoding.
type internal InferenceOwner
    (tokens: SpecialTokens, encoder: ITextEncoder, configuration: AgentConfiguration,
     provenance: ModelProvenance, runtime: IInferenceRuntime) =
    let lifetime = SessionLifetime(runtime)

    let runChunks (cancellationToken: CancellationToken) requests chunks =
        let rec run offset remainingQuestions remainingChunks accumulated =
            cancellationToken.ThrowIfCancellationRequested()
            match remainingChunks with
            | [] -> Ok(List.concat (List.rev accumulated))
            | chunk :: rest ->
                let count = SequenceBatch.questionTypes chunk |> List.length
                let selected, remaining = List.splitAt count remainingQuestions
                let outputs = runtime.Run(chunk, cancellationToken)
                cancellationToken.ThrowIfCancellationRequested()
                Prediction.decode configuration offset selected chunk outputs
                |> Result.bind (fun answers ->
                    run (offset + count) remaining rest (answers :: accumulated))
        run 0 (requests |> List.collect DecisionRequest.questions) chunks []

    let predict requests (cancellationToken: CancellationToken) =
        use linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token)
        try
            linked.Token.ThrowIfCancellationRequested()
            let prepared = Prediction.prepare tokens encoder requests
            linked.Token.ThrowIfCancellationRequested()
            prepared
            |> Result.bind (fun (batch, chunks) ->
                runChunks linked.Token requests chunks
                |> Result.map (Prediction.results provenance batch chunks))
        with
        | :? OperationCanceledException when linked.IsCancellationRequested ->
            Error(if lifetime.IsClosed then PredictionError.Disposed else PredictionError.Cancelled)
        | :? OnnxRuntimeException as error ->
            if lifetime.IsClosed then Error PredictionError.Disposed
            elif linked.IsCancellationRequested then Error PredictionError.Cancelled
            else Error(PredictionError.NativeFailure error.Message)

    member _.PredictBatch
        (requests, cancellationToken)
        : Task<Result<DecisionResult list, PredictionError>> =
        match lifetime.TryEnter() with
        | Error error -> Task.FromResult(Error error)
        | Ok () -> task {
            try
                // Native Run is synchronous; precisely one awaited worker is admitted per owner.
                let! result = Task.Run(fun () -> predict requests cancellationToken)
                if lifetime.IsClosed then return Error PredictionError.Disposed
                elif cancellationToken.IsCancellationRequested then
                    return Error PredictionError.Cancelled
                else return result
            finally
                lifetime.Leave()
          }

    interface IDisposable with
        member _.Dispose() = lifetime.Close()
    interface IAsyncDisposable with
        member _.DisposeAsync() =
            lifetime.Close()
            ValueTask(lifetime.Completion :> Task)

/// Real loading plus the internal resource-transfer seam used by controlled IO tests.
[<RequireQualifiedAccess>]
module internal Inference =
    let create tokens encoder configuration provenance (runtime: IInferenceRuntime) =
        let mutable transferred = false
        try
            InferenceContract.validate runtime
            |> Result.map (fun () ->
                let owner = new InferenceOwner(tokens, encoder, configuration, provenance, runtime)
                transferred <- true
                owner)
        finally
            if not transferred then runtime.Dispose()

    let private provenance (verified: BundleVerification) artifacts (runtime: IInferenceRuntime) =
        {
            BundleId = verified.BundleId
            GraphSha256 = BundleArtifact.sha256 (List.head artifacts)
            ArtifactHashes =
                artifacts
                |> List.map (fun artifact ->
                    BundleArtifact.path artifact, BundleArtifact.sha256 artifact)
            RuntimeVersion = runtime.Version
            ExecutionProvider = "CPUExecutionProvider"
        }

    let private loadVerified directory manifestPath =
        let manifest = ModelBundle.readManifest manifestPath
        let verified = ModelBundle.verifyArtifacts directory manifest
        let artifacts = ModelBundle.artifacts manifest
        let path index = Path.Combine(directory, BundleArtifact.path artifacts[index])
        let tokenizer = Tokenizer.read (path 2) (path 1)
        let configuration = AgentConfiguration.read (path 3)
        let runtime = OrtRuntime.Create verified.GraphPath
        // Ownership transfers only after all other IO/parsing has completed.
        let mutable handedOff = false
        try
            let provenance = provenance verified artifacts runtime
            let tokens = Tokenizer.specialTokens tokenizer
            let encoder = Tokenizer.encoder tokenizer
            handedOff <- true
            create tokens encoder configuration provenance runtime
        finally
            if not handedOff then runtime.Dispose()

    let load directory manifestPath =
        if String.IsNullOrWhiteSpace directory then
            Error(ModelLoadError.InvalidArgument "modelDirectory")
        elif String.IsNullOrWhiteSpace manifestPath then
            Error(ModelLoadError.InvalidArgument "manifestPath")
        else
            try loadVerified directory manifestPath
            with
            | :? FileNotFoundException as error ->
                Error(ModelLoadError.MissingArtifact error.FileName)
            | :? DirectoryNotFoundException as error ->
                Error(ModelLoadError.MissingArtifact error.Message)
            | :? InvalidDataException as error -> Error(ModelLoadError.InvalidBundle error.Message)
            | :? OnnxRuntimeException as error -> Error(ModelLoadError.NativeFailure error.Message)
            | :? DllNotFoundException as error -> Error(ModelLoadError.NativeFailure error.Message)
            | :? EntryPointNotFoundException as error ->
                Error(ModelLoadError.NativeFailure error.Message)
            | :? BadImageFormatException as error ->
                Error(ModelLoadError.NativeFailure error.Message)
            | :? TypeInitializationException as error when
                (error.InnerException :? DllNotFoundException)
                || (error.InnerException :? EntryPointNotFoundException)
                || (error.InnerException :? BadImageFormatException)
                || (error.InnerException :? OnnxRuntimeException) ->
                Error(ModelLoadError.NativeFailure error.InnerException.Message)
            | :? IOException as error -> Error(ModelLoadError.IoFailure error.Message)
            | :? UnauthorizedAccessException as error ->
                Error(ModelLoadError.IoFailure error.Message)
            | :? ArgumentException as error -> Error(ModelLoadError.InvalidArgument error.ParamName)
