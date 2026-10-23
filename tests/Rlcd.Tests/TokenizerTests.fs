namespace Rlcd.Tests

open System
open System.IO
open System.Text.Json.Nodes

open Xunit

open Rlcd

/// Synthetic byte vocabulary and hand-computed IDs, not model/oracle parity.
[<Trait("Category", "Unit")>]
module TokenizerTests =
    let private controls =
        """{"cls_token":"[CLS]","sep_token":"[SEP]","pad_token":"[PAD]","mask_token":"[MASK]"}"""

    let private added content identifier normalized singleWord left right =
        let token = JsonObject()
        token["content"] <- JsonValue.Create(content: string)
        token["id"] <- JsonValue.Create(identifier: int)
        token["normalized"] <- JsonValue.Create(normalized: bool)
        token["single_word"] <- JsonValue.Create(singleWord: bool)
        token["lstrip"] <- JsonValue.Create(left: bool)
        token["rstrip"] <- JsonValue.Create(right: bool)
        token["special"] <- JsonValue.Create((content = "[MASK]"))
        token

    let private document (merges: (string * string) list) additions =
        let root =
            JsonNode.Parse
                """{"version":"1.0","normalizer":{"type":"NFC"},
                    "pre_tokenizer":{"type":"ByteLevel","add_prefix_space":false,"use_regex":true},
                    "model":{"type":"BPE","vocab":{},"merges":[]},"added_tokens":[]}"""
        let vocab = root["model"].["vocab"].AsObject()
        // Independent synthetic fixture: byte b has ID 1000+b.
        let visible = [ 33..126 ] @ [ 161..172 ] @ [ 174..255 ]
        let escaped = [ 0..255 ] |> List.except visible
        for value in visible do
            vocab[string (char value)] <- JsonValue.Create(1000 + value)
        for index, value in List.indexed escaped do
            vocab[string (char (256 + index))] <- JsonValue.Create(1000 + value)
        for index, name in List.indexed [ "[CLS]"; "[SEP]"; "[PAD]"; "[MASK]" ] do
            vocab[name] <- JsonValue.Create(900 + index)
        for index, (left, right) in List.indexed merges do
            vocab[left + right] <- JsonValue.Create(5000 + index)
            let pair = JsonArray()
            pair.Add(JsonValue.Create(left: string))
            pair.Add(JsonValue.Create(right: string))
            root["model"].["merges"].AsArray().Add pair
        for token in added "[MASK]" 903 false false false false :: additions do
            root["added_tokens"].AsArray().Add token
        root

    type private Fixture(root: JsonNode, configuration: string) =
        let directory = Path.Combine(Path.GetTempPath(), $"rlcd-tokenizer-{Guid.NewGuid():N}")
        do Directory.CreateDirectory directory |> ignore
        member _.Read() =
            let path = Path.Combine(directory, "tokenizer.json")
            let config = Path.Combine(directory, "tokenizer_config.json")
            File.WriteAllText(path, root.ToJsonString())
            File.WriteAllText(config, configuration)
            Tokenizer.read config path
        interface IDisposable with
            member _.Dispose() = Directory.Delete(directory, true)

    let private check expected text root =
        use fixture = new Fixture(root, controls)
        let tokenizer = fixture.Read()
        match Tokenizer.encode text tokenizer with
        | Ok actual -> Assert.Equal<int list>(expected, actual)
        | Error error -> failwithf "Unexpected encoding failure: %A" error
        Assert.Equal<Result<int list, TextEncodingError>>(
            Ok expected, (Tokenizer.encoder tokenizer).Encode text)

    [<Theory>]
    [<InlineData("", "")>]
    [<InlineData("abc", "1097,1098,1099")>]
    [<InlineData("A!", "1065,1033")>]
    [<InlineData("é", "1195,1169")>]
    [<InlineData("e\u0301", "1195,1169")>]
    [<InlineData("😀", "1240,1159,1152,1128")>]
    [<InlineData("𐐀", "1240,1144,1144,1128")>]
    [<InlineData("\r\n\t  a  ", "1013,1010,1009,1032,1032,1097,1032,1032")>]
    [<InlineData("\u0000\u0085\u00a0", "1000,1194,1133,1194,1160")>]
    [<InlineData("[MASK][CLS]", "903,1091,1067,1076,1083,1093")>]
    let ``preserves bytes NFC and literal added tokens without automatic specials`` (text: string, ids: string) =
        let expected =
            if ids = "" then [] else ids.Split(',') |> Array.map int |> Array.toList
        check expected text (document [] [])

    [<Fact>]
    let ``ranked merges respect case sensitive contractions and punctuation boundaries`` () =
        let merges =
            [ "n", "'"; "'", "t"; "c", "a"; "ca", "n"; "!", "!"
              "h", "e"; "he", "l"; "hel", "l"; "hell", "o"; "o", "!" ]
        check [ 5008; 1032; 5003; 5001; 5004 ] "hello can't!!" (document merges [])
        check [ 5003; 1039; 1084 ] "can'T" (document merges [])

    [<Fact>]
    let ``whitespace lookahead leaves last space for following word but keeps trailing run`` () =
        let merges = [ "Ġ", "Ġ"; "Ġ", "a"; "č", "Ċ"; "ĉ", "Ġ" ]
        check [ 5000; 5001; 5000; 1032 ] "   a   " (document merges [])
        check [ 5002; 5003; 5001 ] "\r\n\t  a" (document merges [])
        check [ 1009; 1097 ] "\ta" (document merges [])

    [<Fact>]
    let ``supplementary letters participate in letter runs unlike emoji`` () =
        let merges =
            [ "ð", "Ĳ"; "ðĲ", "Ĳ"; "ðĲĲ", "Ģ"; "ðĲĲĢ", "a"
              "ð", "Ł"; "ðŁ", "ĺ"; "ðŁĺ", "Ģ"; "ðŁĺĢ", "a" ]
        check [ 5003 ] "𐐀a" (document merges [])
        check [ 5006; 1097 ] "😀a" (document merges [])

    [<Fact>]
    let ``lowest rank wins and repeated overlapping pairs break ties leftmost`` () =
        check [ 1097; 5000 ] "abc" (document [ "b", "c"; "a", "b" ] [])
        check [ 5001 ] "aaa" (document [ "a", "a"; "aa", "a" ] [])
        check [ 5000; 5000 ] "aaaa" (document [ "a", "a"; "aa", "a" ] [])

    [<Fact>]
    let ``added tokens use longest match and raw phase precedes NFC phase`` () =
        let additions =
            [ added "e\u0301" 7000 false false false false
              added "é" 1233 true false false false
              added "  " 7002 true false false false
              added "   " 7003 true false false false
              added "[MASK]x" 7004 true false false false ]
        check [ 7000; 1233; 7003; 7002; 903; 1120 ] "e\u0301é     [MASK]x" (document [] additions)

    [<Fact>]
    let ``normalized added token patterns themselves receive NFC`` () =
        check [ 7000; 7000 ] "ée\u0301"
            (document [] [ added "e\u0301" 7000 true false false false ])

    [<Fact>]
    let ``single word matching uses scalar word neighbors including marks and joiners`` () =
        let root = document [] [ added "cat" 7000 false true false false ]
        check [ 7000; 1033; 7000 ] "cat!cat" root
        check [ 1097; 1099; 1097; 1116 ] "acat" (document [] [ added "cat" 7000 false true false false ])
        check [ 1240; 1144; 1144; 1128; 1099; 1097; 1116 ] "𐐀cat"
            (document [] [ added "cat" 7000 false true false false ])
        check [ 1099; 1097; 1116; 1226; 1128; 1141 ] "cat\u200d"
            (document [] [ added "cat" 7000 false true false false ])

    [<Fact>]
    let ``rejected longest single word candidate does not fall back to overlapping shorter token`` () =
        check [ 1097; 1098; 1099; 1120 ] "abcx"
            (document [] [ added "abc" 7000 false true false false; added "ab" 7001 false false false false ])

    [<Fact>]
    let ``strip flags consume Unicode whitespace but not non whitespace`` () =
        check [ 1097; 7000; 1098 ] "a\t\u00a0<X>\r\n b"
            (document [] [ added "<X>" 7000 false false true true ])
        check [ 1032; 7000; 1032 ] " <X> "
            (document [] [ added "<X>" 7000 false false false false ])

    [<Fact>]
    let ``special IDs and mask spelling come from sidecars not model constants`` () =
        use fixture = new Fixture(document [] [], controls)
        let special = fixture.Read() |> Tokenizer.specialTokens
        Assert.Equal(900, special |> SpecialTokens.classification |> TokenId.value)
        Assert.Equal(901, special |> SpecialTokens.separator |> TokenId.value)
        Assert.Equal(902, special |> SpecialTokens.padding |> TokenId.value)
        Assert.Equal(903, special |> SpecialTokens.mask |> TokenId.value)
        Assert.Equal("[MASK]", SpecialTokens.maskText special)

    [<Fact>]
    let ``encoding rejects null invalid UTF16 and fabricated null tokenizer`` () =
        use fixture = new Fixture(document [] [], controls)
        let tokenizer = fixture.Read()
        for text in [ null; string (char 0xD800); string (char 0xDC00); "x" + string (char 0xD800) ] do
            Assert.True(Tokenizer.encode text tokenizer |> Result.isError)
        Assert.True(Tokenizer.encode "a" Unchecked.defaultof<Tokenizer> |> Result.isError)

    [<Fact>]
    let ``missing byte vocabulary is an encoding error not silent deletion or fallback`` () =
        let root = document [] []
        root["model"].["vocab"].AsObject().Remove("a") |> ignore
        use fixture = new Fixture(root, controls)
        Assert.True(fixture.Read() |> Tokenizer.encode "a" |> Result.isError)

    [<Theory>]
    [<InlineData("dropout", "0.1")>]
    [<InlineData("dropout", "0")>]
    [<InlineData("unk_token", "\"[MASK]\"")>]
    [<InlineData("byte_fallback", "true")>]
    [<InlineData("fuse_unk", "true")>]
    [<InlineData("ignore_merges", "true")>]
    [<InlineData("continuing_subword_prefix", "\"##\"")>]
    [<InlineData("end_of_word_suffix", "\"</w>\"")>]
    [<InlineData("unknown_feature", "false")>]
    let ``unsupported BPE settings fail at load`` (name: string, json: string) =
        let root = document [] []
        root["model"].[name] <- JsonNode.Parse json
        use fixture = new Fixture(root, controls)
        Assert.Throws<InvalidDataException>(fun () -> fixture.Read() |> ignore) |> ignore

    [<Theory>]
    [<InlineData("normalizer", "{\"type\":\"NFKC\"}")>]
    [<InlineData("pre_tokenizer", "{\"type\":\"ByteLevel\",\"add_prefix_space\":true,\"use_regex\":true}")>]
    [<InlineData("pre_tokenizer", "{\"type\":\"ByteLevel\",\"add_prefix_space\":false,\"use_regex\":false}")>]
    [<InlineData("truncation", "{\"max_length\":5}")>]
    [<InlineData("padding", "{}")>]
    [<InlineData("decoder", "{\"type\":\"WordPiece\"}")>]
    [<InlineData("post_processor", "{\"type\":\"Unknown\"}")>]
    [<InlineData("unknown_pipeline", "null")>]
    let ``unsupported pipeline schemas fail at load`` (name: string, json: string) =
        let root = document [] []
        root[name] <- JsonNode.Parse json
        use fixture = new Fixture(root, controls)
        Assert.Throws<InvalidDataException>(fun () -> fixture.Read() |> ignore) |> ignore

    [<Theory>]
    [<InlineData("[[\"a\",\"missing\"]]")>]
    [<InlineData("[[\"a\",\"b\"]]")>]
    [<InlineData("[[\"a\",\"b\",\"c\"]]")>]
    [<InlineData("[\"a b c\"]")>]
    [<InlineData("[false]")>]
    let ``malformed merge pairs and missing merge vocabulary fail at load`` (json: string) =
        let root = document [] []
        root["model"].["merges"] <- JsonNode.Parse json
        use fixture = new Fixture(root, controls)
        Assert.Throws<InvalidDataException>(fun () -> fixture.Read() |> ignore) |> ignore

    [<Fact>]
    let ``legacy string merges retain order and explicit inert BPE flags are supported`` () =
        let root = document [ "b", "c"; "a", "b" ] []
        root["model"].["merges"] <- JsonNode.Parse """["b c","a b"]"""
        for name, json in
            [ "dropout", "null"; "unk_token", "null"; "byte_fallback", "false"
              "fuse_unk", "false"; "ignore_merges", "false"
              "continuing_subword_prefix", "\"\""; "end_of_word_suffix", "null" ] do
            root["model"].[name] <- JsonNode.Parse json
        check [ 1097; 5000 ] "abc" root

    [<Fact>]
    let ``full encoding is uncapped and immutable encoder supports concurrent callers`` () =
        use fixture = new Fixture(document [] [], controls)
        let encoder = fixture.Read() |> Tokenizer.encoder
        let text = String.replicate 2048 "a "
        let results = [| 1..16 |] |> Array.Parallel.map (fun _ -> encoder.Encode text)
        for result in results do
            Assert.Equal<Result<int list, TextEncodingError>>(Ok (List.replicate 2048 [ 1097; 1032 ] |> List.concat), result)

    let private template =
        """{"type":"TemplateProcessing",
            "single":[{"SpecialToken":{"id":"[CLS]","type_id":0}},
                      {"Sequence":{"id":"A","type_id":0}},
                      {"SpecialToken":{"id":"[SEP]","type_id":0}}],
            "pair":[{"SpecialToken":{"id":"[CLS]","type_id":0}},
                    {"Sequence":{"id":"A","type_id":0}},
                    {"SpecialToken":{"id":"[SEP]","type_id":0}},
                    {"Sequence":{"id":"B","type_id":1}},
                    {"SpecialToken":{"id":"[SEP]","type_id":1}}],
            "special_tokens":{"[CLS]":{"id":"[CLS]","ids":[900],"tokens":["[CLS]"]},
                              "[SEP]":{"id":"[SEP]","ids":[901],"tokens":["[SEP]"]}}}"""

    [<Fact>]
    let ``validated template processing never inserts automatic special tokens`` () =
        let root = document [] []
        root["post_processor"] <- JsonNode.Parse template
        root["decoder"] <- JsonNode.Parse
            """{"type":"ByteLevel","add_prefix_space":true,"trim_offsets":true,"use_regex":true}"""
        check [ 1097; 903 ] "a[MASK]" root

    [<Theory>]
    [<InlineData("\"id\":\"A\"", "\"id\":\"B\"")>]
    [<InlineData("\"ids\":[900]", "\"ids\":[901]")>]
    [<InlineData("\"type_id\":0", "\"type_id\":-1")>]
    [<InlineData("\"Sequence\"", "\"Unknown\"")>]
    [<InlineData("\"id\":\"[CLS]\",\"type_id\"", "\"id\":\"missing\",\"type_id\"")>]
    let ``template processing cannot hide unsupported sequence changes or inconsistent IDs``
        (before: string, after: string) =
        let root = document [] []
        root["post_processor"] <- JsonNode.Parse(template.Replace(before, after))
        use fixture = new Fixture(root, controls)
        Assert.Throws<InvalidDataException>(fun () -> fixture.Read() |> ignore) |> ignore

    [<Fact>]
    let ``ambiguous normalized patterns and strip overlap schemas explicitly reject`` () =
        let cases =
            [ [ added "é" 1233 true false false false
                added "e\u0301" 7000 true false false false ]
              [ added "<X>" 7000 false false false true
                added "  " 7001 false false false false ] ]
        for additions in cases do
            use fixture = new Fixture(document [] additions, controls)
            Assert.Throws<InvalidDataException>(fun () -> fixture.Read() |> ignore) |> ignore

    [<Theory>]
    [<InlineData("-1")>]
    [<InlineData("2147483648")>]
    [<InlineData("1.5")>]
    [<InlineData("null")>]
    let ``vocabulary IDs must fit nonnegative encoder integers`` (identifier: string) =
        let root = document [] []
        root["model"].["vocab"].["a"] <- JsonNode.Parse identifier
        use fixture = new Fixture(root, controls)
        Assert.Throws<InvalidDataException>(fun () -> fixture.Read() |> ignore) |> ignore

    [<Fact>]
    let ``duplicate merge pairs reject rather than changing the first rank`` () =
        let root = document [ "a", "b" ] []
        root["model"].["merges"] <- JsonNode.Parse """["a b","a b"]"""
        use fixture = new Fixture(root, controls)
        Assert.Throws<InvalidDataException>(fun () -> fixture.Read() |> ignore) |> ignore

    [<Fact>]
    let ``raw tokens prevent normalization across extracted boundaries`` () =
        check [ 1097; 7000; 1204; 1129 ] "a<X>\u0301"
            (document [] [ added "<X>" 7000 false false false false ])

    [<Theory>]
    [<InlineData("\u007f\u0080\u00ad", "1127,1194,1128,1194,1173")>]
    [<InlineData("Ａa", "1239,1188,1161,1097")>]
    [<InlineData("cat_", "1099,1097,1116,1095")>]
    [<InlineData("cat\u0301", "1099,1097,1116,1204,1129")>]
    [<InlineData("cat\u200b", "7000,1226,1128,1139")>]
    [<InlineData("cat\u00a0", "7000,1194,1160")>]
    let ``normalization and word boundaries do not introduce hidden rewriting`` (text: string, ids: string) =
        let expected = ids.Split(',') |> Array.map int |> Array.toList
        check expected text (document [] [ added "cat" 7000 false true false false ])

    [<Theory>]
    [<InlineData("{\"content\":\"[MASK]\",\"lstrip\":\"true\"}")>]
    [<InlineData("{\"content\":\"[MASK]\",\"__type\":\"Other\"}")>]
    [<InlineData("{\"content\":\"[MASK]\",\"unknown\":true}")>]
    let ``special token descriptors reject malformed or unsupported fields`` (descriptor: string) =
        let configuration = JsonNode.Parse controls
        configuration["mask_token"] <- JsonNode.Parse descriptor
        use fixture = new Fixture(document [] [], configuration.ToJsonString())
        Assert.Throws<InvalidDataException>(fun () -> fixture.Read() |> ignore) |> ignore

    [<Fact>]
    let ``special token descriptors preserve the declared content and derived ID`` () =
        let configuration = controls.Replace(
            "\"mask_token\":\"[MASK]\"",
            "\"mask_token\":{\"content\":\"[MASK]\",\"__type\":\"AddedToken\",\"lstrip\":false}")
        use fixture = new Fixture(document [] [], configuration)
        Assert.Equal(Ok [ 903 ], fixture.Read() |> Tokenizer.encode "[MASK]")

    [<Fact>]
    let ``Alphabetic symbols prevent single word extraction even when not letters`` () =
        check [ 1226; 1146; 1182; 1099; 1097; 1116 ] "Ⓐcat"
            (document [] [ added "cat" 7000 false true false false ])

    // Unicode 16.0 UCD Alphabetic/So intersection endpoints and their immediate neighbors.
    // Expected UTF-8 byte IDs below are literal hand-computed values, not encoder output.
    [<Theory>]
    [<InlineData(0x24B5, false, "1226,1146,1181")>]
    [<InlineData(0x24B6, true, "1226,1146,1182")>]
    [<InlineData(0x24E9, true, "1226,1147,1169")>]
    [<InlineData(0x24EA, false, "1226,1147,1170")>]
    [<InlineData(0x1F12F, false, "1240,1159,1132,1175")>]
    [<InlineData(0x1F130, true, "1240,1159,1132,1176")>]
    [<InlineData(0x1F149, true, "1240,1159,1133,1137")>]
    [<InlineData(0x1F14A, false, "1240,1159,1133,1138")>]
    [<InlineData(0x1F14F, false, "1240,1159,1133,1143")>]
    [<InlineData(0x1F150, true, "1240,1159,1133,1144")>]
    [<InlineData(0x1F169, true, "1240,1159,1133,1169")>]
    [<InlineData(0x1F16A, false, "1240,1159,1133,1170")>]
    [<InlineData(0x1F16F, false, "1240,1159,1133,1175")>]
    [<InlineData(0x1F170, true, "1240,1159,1133,1176")>]
    [<InlineData(0x1F189, true, "1240,1159,1134,1137")>]
    [<InlineData(0x1F18A, false, "1240,1159,1134,1138")>]
    [<InlineData(0x30, true, "1048")>]
    [<InlineData(0x5F, true, "1095")>]
    [<InlineData(0x20DD, true, "1226,1131,1157")>]
    [<InlineData(0x93E, true, "1224,1164,1190")>]
    [<InlineData(0x2160, true, "1226,1133,1160")>]
    [<InlineData(0x200C, true, "1226,1128,1140")>]
    [<InlineData(0xB2, false, "1194,1178")>]
    [<InlineData(0x1C89, true, "1225,1178,1137")>]
    [<InlineData(0x1C8B, false, "1225,1178,1139")>]
    let ``Unicode word property is scalar aware on both sides in both added token phases``
        (scalar: int, isWord: bool, ids: string) =
        let symbol = Char.ConvertFromUtf32 scalar
        let symbolIds = ids.Split(',') |> Array.map int |> Array.toList
        let catIds = if isWord then [ 1099; 1097; 1116 ] else [ 7000 ]
        for normalized in [ false; true ] do
            check (symbolIds @ catIds) (symbol + "cat")
                (document [] [ added "cat" 7000 normalized true false false ])
            check (catIds @ symbolIds) ("cat" + symbol)
                (document [] [ added "cat" 7000 normalized true false false ])

    [<Fact>]
    let ``every Unicode16 Alphabetic OtherSymbol scalar blocks single word matching`` () =
        use fixture = new Fixture(document [] [ added "cat" 7000 false true false false ], controls)
        let tokenizer = fixture.Read()
        // All 130 members, as identified by the normative Unicode 16 property intersection.
        for first, last in [ 0x24B6, 0x24E9; 0x1F130, 0x1F149
                             0x1F150, 0x1F169; 0x1F170, 0x1F189 ] do
            for scalar in first .. last do
                let symbol = Char.ConvertFromUtf32 scalar
                for text in [ symbol + "cat"; "cat" + symbol ] do
                    match Tokenizer.encode text tokenizer with
                    | Ok ids -> Assert.DoesNotContain(7000, ids)
                    | Error error -> failwithf "Unexpected encoding failure: %A" error

    [<Fact>]
    let ``Alphabetic symbols remain nonletters for ByteLevel segmentation`` () =
        check [ 5001; 1097 ] "Ⓐa"
            (document [ "â", "Ĵ"; "âĴ", "¶"; "âĴ¶", "a" ] [])
        check [ 5002; 1097 ] "🄰a"
            (document [ "ð", "Ł"; "ðŁ", "Ħ"; "ðŁĦ", "°"; "ðŁĦ°", "a" ] [])
