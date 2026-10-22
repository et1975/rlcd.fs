#!/usr/bin/env -S dotnet fsi
#r "../src/Rlcd/bin/Release/net10.0/Rlcd.dll"

open System.Text.Json

open Rlcd

let collect results =
    let prepend result rest =
        match result, rest with
        | Ok value, Ok values -> Ok(value :: values)
        | Error errors, Error remaining -> Error(errors @ remaining)
        | Error errors, Ok _
        | Ok _, Error errors -> Error errors

    List.foldBack prepend results (Ok [])

let triage state =
    let category =
        [
            ChoiceOption.create "billing" "Billing" (Some "Charges, invoices, and refunds")
            ChoiceOption.create "technical" "Technical" (Some "Product faults and access problems")
        ]
        |> collect
        |> Result.bind (Question.choice "category" "Choose the ticket category.")

    let urgency =
        [
            ChoiceOption.create "routine" "Routine" (Some "Work can continue")
            ChoiceOption.create "blocked" "Blocked" (Some "Work cannot continue")
            ChoiceOption.create "critical" "Critical" (Some "A widespread service interruption")
        ]
        |> collect
        |> Result.bind (Question.score "urgency" "Rate the urgency in the stated ordinal order.")

    [
        category
        urgency
        Question.boolean "follow-up" "Does the customer need a follow-up reply?" None
    ]
    |> collect
    |> Result.bind (fun questions ->
        DecisionRequest.create
            {
                State = state
                Questions = questions
                Options = RequestOptions.defaults
            })

let summarizeQuestion question =
    {|
        id = question |> Question.id |> QuestionId.value
        kind = question |> Question.kind |> string
        instructions = Question.instructions question
        options =
            question
            |> Question.options
            |> List.mapi (fun position option ->
                {|
                    position = position
                    id = option |> ChoiceOption.id |> OptionId.value
                    label = ChoiceOption.label option
                    criterion = option |> ChoiceOption.criterion |> Option.toObj
                |})
    |}

let print value =
    JsonSerializer.Serialize(value, JsonSerializerOptions(WriteIndented = true)) |> printfn "%s"

let args = fsi.CommandLineArgs |> Array.skip 1

let state =
    match args with
    | [||] -> Ok "The customer was charged twice and asks when the refund will arrive."
    | [| "--state"; text |] -> Ok text
    | _ -> Error [ "Usage: ./examples/GenericDecisions.fsx [--state <text>]" ]

let result =
    state
    |> Result.bind (fun text -> triage text |> Result.mapError (List.map (sprintf "%A")))

match result with
| Error errors ->
    print
        {|
            status = "invalid-request"
            inferenceAvailable = false
            errors = errors
        |}

    exit 2
| Ok request ->
    let options = DecisionRequest.options request
    let budget = RequestOptions.budget options

    print
        {|
            status = "constructed"
            inferenceAvailable = false
            state = DecisionRequest.state request
            questions = request |> DecisionRequest.questions |> List.map summarizeQuestion
            budget =
                {|
                    sequenceLength = TokenBudget.sequenceLength budget
                    headLength = TokenBudget.headLength budget
                    optionTokens = TokenBudget.optionTokens budget
                |}
            truncationPolicy = options |> RequestOptions.truncation |> string
        |}
