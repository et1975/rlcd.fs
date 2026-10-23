namespace Rlcd

open System

/// A nonnegative supplied token ID; vocabulary membership belongs to the encoder.
type TokenId = private TokenId of int

/// A nonnegative zero-based position in an assembled sequence.
type TokenPosition = private TokenPosition of int

/// The source text affected by encoding or truncation.
[<RequireQualifiedAccess>]
type SequencePart =
    /// Shared request state, encoded once per request.
    | State
    /// A question's prefixed instructions.
    | Instructions of QuestionId
    /// A rendered option identified independently of its tensor position.
    | Option of QuestionId * OptionId

/// Ordered observations about sanitization and information loss.
[<RequireQualifiedAccess>]
type SequenceDiagnostic =
    /// Literal mask spellings replaced with a single space each.
    | MaskTextReplaced of part: SequencePart * count: int
    /// Nonempty source text produced no tokens.
    | EmptyEncoding of part: SequencePart
    /// Token counts before and after a particular truncation stage.
    | TokensTruncated of part: SequencePart * original: int * retained: int
    /// These options have identical spans after head budgets or the final sequence cut.
    | OptionsCollapsed of question: QuestionId * options: OptionId list
    /// The final sequence cut removed tokens, possibly including separator tokens.
    | SequenceClipped of question: QuestionId * original: int64 * retained: int

/// A failure explicitly reported by the supplied text encoder.
[<RequireQualifiedAccess>]
type TextEncodingError =
    /// Encoding could not be completed; the message belongs to the encoder.
    | Failed of message: string

/// Sequence construction failures; errors never contain partial successful results.
[<RequireQualifiedAccess>]
type SequenceError =
    /// A required reference value was null.
    | MissingValue of field: string
    /// Token IDs must be nonnegative.
    | InvalidTokenId of value: int
    /// Positions must be nonnegative.
    | InvalidTokenPosition of value: int
    /// The mask spelling must contain non-whitespace text.
    | InvalidMaskText
    /// Structural special tokens must have distinct IDs.
    | ConflictingSpecialTokens
    /// The supplied encoder explicitly failed.
    | EncoderFailed of part: SequencePart * error: TextEncodingError
    /// An encoder output ID is negative, at the specified zero-based output index.
    | InvalidEncodedToken of part: SequencePart * index: int * value: int
    /// An encoder emitted a mask after literal mask sanitization.
    | UnexpectedMaskToken of part: SequencePart * index: int
    /// Reject policy refused information loss; sanitization alone is not a rejection.
    | LossRejected of diagnostics: SequenceDiagnostic list
    /// The sequence budget dropped markers; neither policy may remap these options.
    | MissingOptionMarkers of question: QuestionId * options: OptionId list
    /// Batch construction requires at least one request.
    | EmptyBatch
    /// All errors from a request at its zero-based input position.
    | InvalidBatchItem of index: int * errors: SequenceError list

/// Construction and read-out of supplied token IDs.
[<RequireQualifiedAccess>]
module TokenId =
    /// Reject negative IDs without assuming a model vocabulary.
    let create (value: int) : Result<TokenId, SequenceError list> =
        if value < 0 then
            Error [ SequenceError.InvalidTokenId value ]
        else
            Ok (TokenId value)

    /// Return the supplied ID.
    let value (token: TokenId) : int =
        let (TokenId value) = token
        value

/// Construction and read-out of token positions.
[<RequireQualifiedAccess>]
module TokenPosition =
    /// Reject negative positions.
    let create (value: int) : Result<TokenPosition, SequenceError list> =
        if value < 0 then
            Error [ SequenceError.InvalidTokenPosition value ]
        else
            Ok (TokenPosition value)

    /// Return the zero-based position.
    let value (position: TokenPosition) : int =
        let (TokenPosition value) = position
        value

/// Supplied structural token IDs and the exact literal mask spelling.
type SpecialTokens =
    private
        {
            Classification: TokenId
            Separator: TokenId
            Mask: TokenId
            Padding: TokenId
            MaskText: string
        }

/// Validated special-token construction; no model-specific IDs are assumed.
[<RequireQualifiedAccess>]
module SpecialTokens =
    let private identifiers classification separator mask padding =
        let values =
            TokenId.create classification,
            TokenId.create separator,
            TokenId.create mask,
            TokenId.create padding
        match values with
        | Ok classification, Ok separator, Ok mask, Ok padding ->
            Ok (classification, separator, mask, padding)
        | classification, separator, mask, padding ->
            [ classification; separator; mask; padding ]
            |> List.collect (function Ok _ -> [] | Error errors -> errors)
            |> Error

    let private errors classification separator mask padding maskText =
        [
            if String.IsNullOrWhiteSpace maskText then
                SequenceError.InvalidMaskText
            if [ classification; separator; mask; padding ]
               |> List.distinct
               |> List.length <> 4 then
                SequenceError.ConflictingSpecialTokens
        ]

    /// Require nonnegative distinct IDs and a nonblank exact mask spelling.
    let create
        (classification: int)
        (separator: int)
        (mask: int)
        (padding: int)
        (maskText: string)
        : Result<SpecialTokens, SequenceError list> =
        identifiers classification separator mask padding
        |> Validation.addErrors (errors classification separator mask padding maskText)
        |> Result.map (fun (classification, separator, mask, padding) ->
            {
                Classification = classification
                Separator = separator
                Mask = mask
                Padding = padding
                MaskText = maskText
            })

    /// Return the classification/start token.
    let classification (tokens: SpecialTokens) : TokenId = tokens.Classification

    /// Return the separator token.
    let separator (tokens: SpecialTokens) : TokenId = tokens.Separator

    /// Return the option-marker token.
    let mask (tokens: SpecialTokens) : TokenId = tokens.Mask

    /// Return the batch-padding token.
    let padding (tokens: SpecialTokens) : TokenId = tokens.Padding

    /// Return the exact mask spelling to sanitize before encoding.
    let maskText (tokens: SpecialTokens) : string = tokens.MaskText

/// Supplied pure, deterministic encoding without automatic special tokens or truncation.
/// Implementations must report failure through Result, not throw. Full output is required
/// so sequence construction can detect every truncation; this is not a tokenizer implementation.
type ITextEncoder =
    /// Encode the exact supplied text to ordered raw IDs, or an explanatory failure.
    abstract Encode: text: string -> Result<int list, TextEncodingError>

/// One question's immutable, unpadded sequence with stable option alignment.
type QuestionSequence =
    private
        {
            QuestionId: QuestionId
            OptionIds: OptionId list
            Kind: QuestionKind
            InputIds: TokenId list
            MarkerPositions: TokenPosition list
            Diagnostics: SequenceDiagnostic list
        }

/// Read-out of an assembled question.
[<RequireQualifiedAccess>]
module QuestionSequence =
    let internal create question ids markers diagnostics =
        {
            QuestionId = Question.id question
            OptionIds = Question.options question |> List.map ChoiceOption.id
            Kind = Question.kind question
            InputIds = ids
            MarkerPositions = markers
            Diagnostics = diagnostics
        }

    /// Return the stable caller question ID.
    let questionId (sequence: QuestionSequence) : QuestionId = sequence.QuestionId

    /// Return option IDs in marker order.
    let optionIds (sequence: QuestionSequence) : OptionId list = sequence.OptionIds

    /// Return the question's semantic kind.
    let kind (sequence: QuestionSequence) : QuestionKind = sequence.Kind

    /// Return the unpadded token sequence.
    let inputIds (sequence: QuestionSequence) : TokenId list = sequence.InputIds

    /// Return one marker position for every option.
    let markerPositions (sequence: QuestionSequence) : TokenPosition list = sequence.MarkerPositions

    /// Return ordered diagnostics, including shared-state diagnostics.
    let diagnostics (sequence: QuestionSequence) : SequenceDiagnostic list = sequence.Diagnostics

/// Rectangular immutable batch columns in request order, then question order.
type SequenceBatch =
    private
        {
            Sequences: QuestionSequence list list
            InputIds: TokenId list list
            AttentionMask: int list list
            MarkerPositions: TokenPosition list list
            MarkerMask: bool list list
            QuestionTypes: int list
        }

/// Read-out of model-free batch columns; no native tensors are created.
[<RequireQualifiedAccess>]
module SequenceBatch =
    let private pad length value items =
        items @ List.replicate (length - List.length items) value

    let private qtype kind =
        match kind with
        | QuestionKind.Choice -> 0
        | QuestionKind.Score -> 1
        | QuestionKind.Boolean -> 2

    let internal create padding zero sequences =
        let items = List.concat sequences
        let lengths = items |> List.map (QuestionSequence.inputIds >> List.length)
        let counts = items |> List.map (QuestionSequence.markerPositions >> List.length)
        let width = lengths |> List.fold max 0
        let optionWidth = counts |> List.fold max 0
        {
            Sequences = sequences
            InputIds = items |> List.map (QuestionSequence.inputIds >> pad width padding)
            AttentionMask =
                lengths |> List.map (fun length -> List.replicate length 1 |> pad width 0)
            MarkerPositions =
                items |> List.map (QuestionSequence.markerPositions >> pad optionWidth zero)
            MarkerMask =
                counts |> List.map (fun count -> List.replicate count true |> pad optionWidth false)
            QuestionTypes = items |> List.map (QuestionSequence.kind >> qtype)
        }

    /// Return unpadded sequences, preserving request grouping.
    let sequences (batch: SequenceBatch) : QuestionSequence list list = batch.Sequences

    /// Return rows padded to the longest sequence in this batch.
    let inputIds (batch: SequenceBatch) : TokenId list list = batch.InputIds

    /// Return one for real tokens, zero for padding.
    let attentionMask (batch: SequenceBatch) : int list list = batch.AttentionMask

    /// Return marker positions padded with zero to the largest option count.
    let markerPositions (batch: SequenceBatch) : TokenPosition list list = batch.MarkerPositions

    /// Return true for real options, false for padded marker positions.
    let markerMask (batch: SequenceBatch) : bool list list = batch.MarkerMask

    /// Return Laya question-type codes: choice=0, score=1, boolean/noul=2.
    let questionTypes (batch: SequenceBatch) : int list = batch.QuestionTypes

    // Reuse the same collation for bounded native calls, without re-encoding text.
    let internal chunks padding size (batch: SequenceBatch) =
        TokenPosition.create 0
        |> Result.map (fun zero ->
            batch.Sequences
            |> List.concat
            |> List.chunkBySize size
            |> List.map (fun sequences -> create padding zero [ sequences ]))

/// Pure rendering, budget enforcement, sequence assembly and batch collation.
/// Adapted from Laya common.py at 9d955671415fc19f069b9cc998928075c1f255ec:
/// render_options (106-132), build_sequence (135-207), collate_items (528-565).
[<RequireQualifiedAccess>]
module Sequence =
    type private EncodedText =
        {
            Tokens: TokenId list
            Diagnostics: SequenceDiagnostic list
        }

    let private missing field value =
        if isNull (box value) then [ SequenceError.MissingValue field ] else []

    let private sanitize tokens part (text: string) =
        let maskText = SpecialTokens.maskText tokens
        let count = text.Split([| maskText |], StringSplitOptions.None).Length - 1
        text.Replace(maskText, " ", StringComparison.Ordinal),
        [
            if count > 0 then
                SequenceDiagnostic.MaskTextReplaced(part, count)
        ]

    let private validateTokens tokens part raw =
        if isNull (box raw) then
            Error [ SequenceError.MissingValue "encodedTokens" ]
        else
            raw
            |> List.mapi (fun index value ->
                if value = (SpecialTokens.mask tokens |> TokenId.value) then
                    Error [ SequenceError.UnexpectedMaskToken(part, index) ]
                else
                    TokenId.create value
                    |> Result.mapError (fun _ ->
                        [ SequenceError.InvalidEncodedToken(part, index, value) ]))
            |> Validation.collect

    let private encode tokens (encoder: ITextEncoder) part prefix text =
        let sanitized, diagnostics = sanitize tokens part text
        encoder.Encode(prefix + sanitized)
        |> Result.mapError (fun error -> [ SequenceError.EncoderFailed(part, error) ])
        |> Result.bind (validateTokens tokens part)
        |> Result.map (fun ids ->
            {
                Tokens = ids
                Diagnostics =
                    diagnostics
                    @ [
                        if List.isEmpty ids then
                            SequenceDiagnostic.EmptyEncoding part
                      ]
            })

    let private questionName kind =
        match kind with
        | QuestionKind.Choice -> "choice"
        | QuestionKind.Score -> "score"
        | QuestionKind.Boolean -> "noul"

    let private render kind index option =
        let label = ChoiceOption.label option
        let criterion = ChoiceOption.criterion option
        match kind with
        | QuestionKind.Choice ->
            criterion
            |> Option.map (fun text -> $"{label}: {text}")
            |> Option.defaultValue label
        | QuestionKind.Score ->
            // The generic API's optional criterion maps to Laya's required level criterion.
            let text = criterion |> Option.defaultValue label
            $"level {index}: {text}"
        | QuestionKind.Boolean ->
            let fallback =
                if index = 0 then
                    "no, the statement does not hold"
                else
                    "yes, the statement holds"
            let text = criterion |> Option.defaultValue fallback
            $"{label}: {text}"

    let private truncate part limit encoded =
        let retained = encoded.Tokens |> List.truncate limit
        {
            Tokens = retained
            Diagnostics =
                encoded.Diagnostics
                @ [
                    if retained.Length < encoded.Tokens.Length then
                        SequenceDiagnostic.TokensTruncated(
                            part, encoded.Tokens.Length, retained.Length
                        )
                  ]
        }

    let private optionPart question option =
        SequencePart.Option(Question.id question, ChoiceOption.id option)

    let private encodeOptions tokens encoder budget question =
        Question.options question
        |> List.mapi (fun index option ->
            let part = optionPart question option
            render (Question.kind question) index option
            |> encode tokens encoder part " "
            |> Result.map (truncate part (TokenBudget.optionTokens budget))
            |> Result.map (fun encoded ->
                option,
                { encoded with Tokens = SpecialTokens.mask tokens :: encoded.Tokens }))
        |> Validation.collect

    let private reserveInstructions budget question options =
        let total = options |> List.sumBy (fun (_, encoded) -> int64 encoded.Tokens.Length)
        let head = int64 (TokenBudget.headLength budget)
        if head - total >= 16L then
            options
        else
            let per = max 4L ((head - 16L) / int64 (max 1 options.Length)) |> int
            options
            |> List.map (fun (option, encoded) ->
                // Rebudget whole spans, including the marker, just as Laya does.
                option, truncate (optionPart question option) per encoded)

    let private encodeHead tokens encoder budget question options =
        let optionSize = options |> List.sumBy (fun (_, encoded) -> int64 encoded.Tokens.Length)
        let limit = max 8L (int64 (TokenBudget.headLength budget) - optionSize) |> int
        let part = SequencePart.Instructions(Question.id question)
        let prefix = $"{questionName (Question.kind question)} question: "
        Question.instructions question
        |> encode tokens encoder part prefix
        |> Result.map (truncate part limit)

    let private optionPositions start options =
        options
        |> List.mapFold (fun position (option, encoded) ->
            (option, position, encoded), position + encoded.Tokens.Length) start
        |> fst

    let private collapse question spans =
        spans
        |> List.groupBy (fun (_, encoded) -> encoded.Tokens)
        |> List.choose (fun (_, group) ->
            if group.Length < 2 then
                None
            else
                group
                |> List.map (fst >> ChoiceOption.id)
                |> fun ids -> Some(SequenceDiagnostic.OptionsCollapsed(Question.id question, ids)))

    let private rejectLoss policy diagnostics =
        let losses =
            diagnostics
            |> List.filter (function
                | SequenceDiagnostic.MaskTextReplaced _ -> false
                | SequenceDiagnostic.EmptyEncoding _
                | SequenceDiagnostic.TokensTruncated _
                | SequenceDiagnostic.OptionsCollapsed _
                | SequenceDiagnostic.SequenceClipped _ -> true)
        match policy, losses with
        | TruncationPolicy.Reject, _ :: _ -> Error [ SequenceError.LossRejected diagnostics ]
        | TruncationPolicy.Reject, []
        | TruncationPolicy.TruncateWithDiagnostics, _ -> Ok ()

    let private clip tokens maximum state head options =
        let separator = SpecialTokens.separator tokens
        let start = SpecialTokens.classification tokens :: head.Tokens @ [ separator ]
        let positions = optionPositions start.Length options
        let prefix = start @ (options |> List.collect (snd >> _.Tokens)) @ [ separator ]
        let room = max 0L (int64 maximum - int64 prefix.Length - 1L) |> int
        let retainedState = truncate SequencePart.State room state
        let combined = prefix @ retainedState.Tokens @ [ separator ]
        let ids = combined |> List.truncate maximum
        ids, retainedState, positions, int64 combined.Length

    let private diagnostics question originalLength length (state, head) (options, retained) =
        let collapses =
            collapse question options @ collapse question retained |> List.distinct
        state.Diagnostics @ head.Diagnostics
        @ (retained |> List.collect (snd >> _.Diagnostics))
        @ collapses
        @ [
            if originalLength > int64 length then
                SequenceDiagnostic.SequenceClipped(
                    Question.id question, originalLength, length
                )
          ]

    let private complete question policy diagnostics (ids, positions) =
        rejectLoss policy diagnostics
        |> Result.bind (fun () ->
            positions
            |> List.map (fun (_, index, _) -> TokenPosition.create index)
            |> Validation.collect)
        |> Result.map (fun markers -> QuestionSequence.create question ids markers diagnostics)

    let private assemble tokens request question state (head, options) =
        let config = DecisionRequest.options request
        let maximum = config |> RequestOptions.budget |> TokenBudget.sequenceLength
        let ids, state, positions, originalLength = clip tokens maximum state head options
        let absent =
            positions
            |> List.choose (fun (option, index, _) ->
                if index >= ids.Length then Some(ChoiceOption.id option) else None)
        if not (List.isEmpty absent) then
            Error [ SequenceError.MissingOptionMarkers(Question.id question, absent) ]
        else
            let retained =
                positions
                |> List.map (fun (option, index, encoded) ->
                    option, truncate (optionPart question option) (ids.Length - index) encoded)
            let observations =
                diagnostics question originalLength ids.Length (state, head) (options, retained)
            complete question (RequestOptions.truncation config) observations (ids, positions)

    let private buildQuestion tokens encoder request state question =
        let budget = request |> DecisionRequest.options |> RequestOptions.budget
        encodeOptions tokens encoder budget question
        |> Result.map (reserveInstructions budget question)
        |> Result.bind (fun options ->
            encodeHead tokens encoder budget question options
            |> Result.bind (fun head -> assemble tokens request question state (head, options)))

    /// Build all questions in input order, encoding shared state exactly once.
    /// Score criteria fall back to labels; boolean criteria use Laya's false/true defaults.
    /// Default Reject refuses token loss/empty encoding/collapse; literal masks are sanitized
    /// and diagnosed under either policy. Missing markers always fail without remapping.
    let build
        (tokens: SpecialTokens)
        (encoder: ITextEncoder)
        (request: DecisionRequest)
        : Result<QuestionSequence list, SequenceError list> =
        Ok ()
        |> Validation.addErrors (
            missing "tokens" tokens @ missing "encoder" encoder @ missing "request" request
        )
        |> Result.bind (fun () ->
            DecisionRequest.state request |> encode tokens encoder SequencePart.State "")
        |> Result.bind (fun state ->
            DecisionRequest.questions request
            |> List.map (buildQuestion tokens encoder request state)
            |> Validation.collect)

    /// Build every request or return all indexed failures, never a partial batch.
    /// Pad only to actual batch maxima and preserve request/question/option order.
    let buildBatch
        (tokens: SpecialTokens)
        (encoder: ITextEncoder)
        (requests: DecisionRequest list)
        : Result<SequenceBatch, SequenceError list> =
        let errors =
            missing "tokens" tokens @ missing "encoder" encoder @ missing "requests" requests
            @ [
                if not (isNull (box requests)) && List.isEmpty requests then
                    SequenceError.EmptyBatch
              ]
        Ok ()
        |> Validation.addErrors errors
        |> Result.bind (fun () ->
            requests
            |> List.mapi (fun index request ->
                build tokens encoder request
                |> Result.mapError (fun errors ->
                    [ SequenceError.InvalidBatchItem(index, errors) ]))
            |> Validation.collect)
        |> Result.bind (fun sequences ->
            TokenPosition.create 0
            |> Result.map (fun zero ->
                SequenceBatch.create (SpecialTokens.padding tokens) zero sequences))
