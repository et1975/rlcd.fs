namespace Rlcd.Tests

open System
open System.IO
open System.Diagnostics
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading

open Microsoft.FSharp.Reflection
open Xunit

open Rlcd

[<Trait("Category", "Unit")>]
module ModelBundleTests =
    let private content = Encoding.UTF8.GetBytes "{}"
    let private digest = Convert.ToHexStringLower(SHA256.HashData content)

    let private manifestJson =
        """{
  "schemaVersion":"laya-decision-v1",
  "bundleId":"laya-english",
  "precision":"float32",
  "graph":{
    "path":"laya.onnx",
    "sha256":"ce529df16bae1488504f9e5bf5120112e80a36076d16624df7703a7679d1996e",
    "sizeBytes":1688711336
  },
  "sidecars":{
    "tokenizer":{"path":"tokenizer.json","sha256":"DIGEST","sizeBytes":2},
    "tokenizerConfig":{"path":"tokenizer_config.json","sha256":"DIGEST","sizeBytes":2},
    "agentConfig":{"path":"rl_agent_config.json","sha256":"DIGEST","sizeBytes":2}
  },
  "limits":{"maxSequenceLength":512,"maxHeadLength":192,"maxOptionTokens":48},
  "inputs":[
    {"name":"input_ids","elementType":"int64","dimensions":["questions","sequence_length"]},
    {"name":"attention_mask","elementType":"int64","dimensions":["questions","sequence_length"]},
    {"name":"marker_pos","elementType":"int64","dimensions":["questions","option_count"]},
    {"name":"marker_mask","elementType":"bool","dimensions":["questions","option_count"]},
    {"name":"qtype","elementType":"int64","dimensions":["questions"]}
  ],
  "outputs":[
    {"name":"logits","elementType":"float32","dimensions":["questions","option_count"]},
    {"name":"act_logits","elementType":"float32","dimensions":["questions","2"]}
  ],
  "customOperatorLibraries":[],
  "externalData":[],
  "testedShapes":[],
  "provenance":{
    "repository":"mariojcr/laya-onnx",
    "revision":"aeebb05497cc473360caf4de1653107cf08d6b10",
    "sourceRepository":"convaiinnovations/laya",
    "sourceRevision":null
  }
}"""
            .Replace("DIGEST", digest)

    type private Fixture() =
        let directory = Path.Combine(Path.GetTempPath(), $"rlcd-tests-{Guid.NewGuid():N}")
        do Directory.CreateDirectory directory |> ignore
        member _.Directory = directory

        member _.ReadManifest(json: string) =
            let path = Path.Combine(directory, "manifest.json")
            File.WriteAllText(path, json)
            ModelBundle.readManifest path

        member _.WriteArtifact() =
            File.WriteAllBytes(Path.Combine(directory, "tokenizer.json"), content)

        interface IDisposable with
            member _.Dispose() = Directory.Delete(directory, true)

    let private tokenizer manifest =
        manifest
        |> ModelBundle.artifacts
        |> List.find (fun artifact -> BundleArtifact.path artifact = "tokenizer.json")

    let private agentJson =
        """{"max_len":512,"head_max_len":192,"temperature":[1.0,0.1,7.0],
            "temperature_by_options":{"choice:2":1.2,"score:3-5":0.3,"noul:2":2.0}}"""

    let private tokenizerJson =
        """{
          "version":"1.0",
          "normalizer":{"type":"NFC"},
          "pre_tokenizer":{"type":"ByteLevel","add_prefix_space":false,"use_regex":true},
          "model":{"type":"BPE","vocab":{"[CLS]":0,"[SEP]":1,"[PAD]":2,"[MASK]":3},"merges":[]},
          "added_tokens":[
            {"id":3,"content":"[MASK]","special":true,"normalized":false,
             "single_word":false,"lstrip":false,"rstrip":false}
          ]
        }"""

    let private tokenizerConfigJson =
        """{"cls_token":"[CLS]","sep_token":"[SEP]","pad_token":"[PAD]","mask_token":"[MASK]"}"""

    let private sidecarsWithControls
        (fixture: Fixture)
        tokenizerContents
        controlContents
        configurationContents
        =
        let manifest = JsonNode.Parse manifestJson

        for key, name, text in
            [
                "tokenizer", "tokenizer.json", tokenizerContents
                "tokenizerConfig", "tokenizer_config.json", controlContents
                "agentConfig", "rl_agent_config.json", configurationContents
            ] do
            let bytes = Encoding.UTF8.GetBytes(text: string)
            File.WriteAllBytes(Path.Combine(fixture.Directory, name), bytes)
            let descriptions = manifest["sidecars"]
            let description = descriptions[key]
            description["sizeBytes"] <- JsonValue.Create bytes.Length

            description["sha256"] <-
                JsonValue.Create(Convert.ToHexStringLower(SHA256.HashData bytes))

        fixture.ReadManifest(manifest.ToJsonString())

    let private sidecars fixture tokenizerContents configurationContents =
        sidecarsWithControls fixture tokenizerContents tokenizerConfigJson configurationContents

    let private withAddedTokens entries =
        let tokenizer = JsonNode.Parse tokenizerJson
        let added = tokenizer["added_tokens"].AsArray()
        added.Clear()

        for content, identifier in entries do
            let token =
                JsonNode.Parse
                    """{"id":0,"content":"","special":false,"normalized":false,
                        "single_word":false,"lstrip":false,"rstrip":false}"""

            token["id"] <- JsonValue.Create(identifier: int)
            token["content"] <- JsonValue.Create(content: string)
            added.Add token

        tokenizer.ToJsonString()

    [<Fact>]
    let ``pinned manifest exposes graph and required sidecars`` () =
        use fixture = new Fixture()
        let manifest = fixture.ReadManifest manifestJson
        let actual = manifest |> ModelBundle.artifacts |> List.map BundleArtifact.path

        Assert.Equal<string list>(
            [
                "laya.onnx"
                "tokenizer.json"
                "tokenizer_config.json"
                "rl_agent_config.json"
            ],
            actual
        )

    [<Theory>]
    [<InlineData("laya-decision-v1", "laya-decision-v2")>]
    [<InlineData("laya-english", "unrelated-model")>]
    [<InlineData("float32", "float16")>]
    [<InlineData("1688711336", "1688711335")>]
    [<InlineData("aeebb05497cc473360caf4de1653107cf08d6b10", "unknown")>]
    [<InlineData("\"maxSequenceLength\":512", "\"maxSequenceLength\":513")>]
    [<InlineData("\"maxHeadLength\":192", "\"maxHeadLength\":0")>]
    [<InlineData("\"maxOptionTokens\":48", "\"maxOptionTokens\":49")>]
    [<InlineData("\"customOperatorLibraries\":[]", "\"customOperatorLibraries\":[\"custom.so\"]")>]
    [<InlineData("\"externalData\":[]", "\"externalData\":[\"../weights.bin\"]")>]
    [<InlineData("\"marker_mask\"", "\"unknown_mask\"")>]
    [<InlineData("\"bool\"", "\"int64\"")>]
    [<InlineData("\"dimensions\":[\"questions\"]", "\"dimensions\":[\"questions\",\"1\"]")>]
    [<InlineData("\"questions\",\"2\"", "\"questions\",\"3\"")>]
    [<InlineData("\"sizeBytes\":2", "\"sizeBytes\":0")>]
    [<InlineData("\"sizeBytes\":2", "\"sizeBytes\":67108865")>]
    [<InlineData("\"schemaVersion\":", "\"unknownField\":0,\"schemaVersion\":")>]
    [<InlineData("\"bundleId\":", "\"bundleId\":\"laya-english\",\"bundleId\":")>]
    [<InlineData("\"testedShapes\":[]", "\"testedShapes\":[{}]")>]
    let ``manifest rejects unsupported or malformed contracts`` (before: string, after: string) =
        use fixture = new Fixture()
        let changed = manifestJson.Replace(before, after)
        Assert.NotEqual<string>(manifestJson, changed)

        Assert.Throws<InvalidDataException>(fun () -> fixture.ReadManifest changed |> ignore)
        |> ignore

    [<Fact>]
    let ``self declared hash cannot authorize an arbitrary graph`` () =
        use fixture = new Fixture()

        let changed =
            manifestJson.Replace(
                "ce529df16bae1488504f9e5bf5120112e80a36076d16624df7703a7679d1996e",
                digest
            )

        Assert.Throws<InvalidDataException>(fun () -> fixture.ReadManifest changed |> ignore)
        |> ignore

    [<Theory>]
    [<InlineData("../tokenizer.json")>]
    [<InlineData("/tokenizer.json")>]
    [<InlineData("nested/../../tokenizer.json")>]
    [<InlineData("nested//tokenizer.json")>]
    [<InlineData("./tokenizer.json")>]
    [<InlineData("C:/tokenizer.json")>]
    [<InlineData("nested\\\\tokenizer.json")>]
    [<InlineData("tokenizer.json:stream")>]
    [<InlineData("tokenizer.json.")>]
    [<InlineData("")>]
    let ``manifest rejects unsafe artifact paths`` (path: string) =
        use fixture = new Fixture()
        let changed = manifestJson.Replace("\"tokenizer.json\"", $"\"{path}\"")

        Assert.Throws<InvalidDataException>(fun () -> fixture.ReadManifest changed |> ignore)
        |> ignore

    [<Theory>]
    [<InlineData("")>]
    [<InlineData("abc")>]
    [<InlineData("fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffz")>]
    let ``sidecar requires a real SHA256 representation`` (hash: string) =
        use fixture = new Fixture()
        let changed = manifestJson.Replace(digest, hash)

        Assert.Throws<InvalidDataException>(fun () -> fixture.ReadManifest changed |> ignore)
        |> ignore

    [<Fact>]
    let ``artifact validates real bytes rather than trusting metadata`` () =
        use fixture = new Fixture()
        fixture.WriteArtifact()
        let artifact = fixture.ReadManifest manifestJson |> tokenizer
        artifact |> BundleArtifact.verify fixture.Directory

    [<Fact>]
    let ``missing artifact is explicit IO failure`` () =
        use fixture = new Fixture()
        let artifact = fixture.ReadManifest manifestJson |> tokenizer

        Assert.Throws<FileNotFoundException>(fun () ->
            artifact |> BundleArtifact.verify fixture.Directory)
        |> ignore

    [<Theory>]
    [<InlineData("[]")>]
    [<InlineData("too long")>]
    let ``wrong size or hash rejects corrupted artifact`` (replacement: string) =
        use fixture = new Fixture()
        let artifact = fixture.ReadManifest manifestJson |> tokenizer
        File.WriteAllText(Path.Combine(fixture.Directory, "tokenizer.json"), replacement)

        Assert.Throws<InvalidDataException>(fun () ->
            artifact |> BundleArtifact.verify fixture.Directory)
        |> ignore

    [<Fact>]
    let ``file symlink is rejected even when bytes match`` () =
        use fixture = new Fixture()
        let target = Path.Combine(fixture.Directory, "target.json")
        File.WriteAllBytes(target, content)

        File.CreateSymbolicLink(Path.Combine(fixture.Directory, "tokenizer.json"), target)
        |> ignore

        let artifact = fixture.ReadManifest manifestJson |> tokenizer

        Assert.Throws<InvalidDataException>(fun () ->
            artifact |> BundleArtifact.verify fixture.Directory)
        |> ignore

    [<Fact>]
    let ``model directory symlink is rejected`` () =
        use fixture = new Fixture()
        fixture.WriteArtifact()
        let link = Path.Combine(fixture.Directory, "link")
        Directory.CreateSymbolicLink(link, fixture.Directory) |> ignore
        let artifact = fixture.ReadManifest manifestJson |> tokenizer

        Assert.Throws<InvalidDataException>(fun () -> artifact |> BundleArtifact.verify link)
        |> ignore

    [<Fact>]
    let ``nested directory symlink is rejected`` () =
        use fixture = new Fixture()
        fixture.WriteArtifact()

        Directory.CreateSymbolicLink(Path.Combine(fixture.Directory, "nested"), fixture.Directory)
        |> ignore

        let changed =
            manifestJson.Replace("\"tokenizer.json\"", "\"nested/tokenizer.json\"")

        let artifact =
            fixture.ReadManifest changed
            |> ModelBundle.artifacts
            |> List.find (fun item -> BundleArtifact.path item = "nested/tokenizer.json")

        Assert.Throws<InvalidDataException>(fun () ->
            artifact |> BundleArtifact.verify fixture.Directory)
        |> ignore

    [<Fact>]
    let ``bundle cannot succeed without the pinned graph`` () =
        use fixture = new Fixture()
        let manifest = fixture.ReadManifest manifestJson

        Assert.Throws<FileNotFoundException>(fun () ->
            manifest |> ModelBundle.verify fixture.Directory |> ignore)
        |> ignore

    [<Fact>]
    let ``configuration is a private single-case reference union`` () =
        let wrapper = typeof<AgentConfiguration>
        let flags = BindingFlags.Public ||| BindingFlags.NonPublic
        Assert.True(FSharpType.IsUnion(wrapper, flags))
        Assert.True(wrapper.IsClass)
        let case = FSharpType.GetUnionCases(wrapper, flags) |> Assert.Single
        let field = case.GetFields() |> Assert.Single
        Assert.Equal(typeof<Map<string, float>>, field.PropertyType)
        Assert.False(FSharpValue.PreComputeUnionConstructorInfo(case, flags).IsPublic)
        Assert.Empty(wrapper.GetConstructors())

    [<Fact>]
    let ``configuration retains raw temperatures and reports required clamps`` () =
        use fixture = new Fixture()
        let path = Path.Combine(fixture.Directory, "rl_agent_config.json")
        File.WriteAllText(path, agentJson)
        let configuration = AgentConfiguration.read path
        let temperatures = AgentConfiguration.temperatures configuration
        Assert.Equal(1.2, temperatures["choice:2"])
        Assert.Equal(0.1, temperatures["temperature[1]"])

        Assert.Equal<string list>(
            [ "score:3-5"; "temperature[1]"; "temperature[2]" ],
            AgentConfiguration.clampingRequired configuration
        )

    [<Fact>]
    let ``configuration preserves finite temperatures and clamps only outside inclusive bounds`` () =
        use fixture = new Fixture()
        let path = Path.Combine(fixture.Directory, "rl_agent_config.json")
        File.WriteAllText(
            path,
            """{"max_len":512,"head_max_len":192,"temperature":[0.5,5.0,-1.0],
                "temperature_by_options":{"choice:2":0.0,"score:3-5":0.5,"noul:2":5.0}}"""
        )
        let configuration = AgentConfiguration.read path

        Assert.Equal<Map<string, float>>(
            Map.ofList
                [
                    "temperature[0]", 0.5
                    "temperature[1]", 5.0
                    "temperature[2]", -1.0
                    "choice:2", 0.0
                    "score:3-5", 0.5
                    "noul:2", 5.0
                ],
            AgentConfiguration.temperatures configuration
        )
        Assert.Equal<string list>(
            [ "choice:2"; "temperature[2]" ],
            AgentConfiguration.clampingRequired configuration
        )

    [<Theory>]
    [<InlineData("[1.0,0.1,7.0]", "[1.0,1.0]")>]
    [<InlineData("[1.0,0.1,7.0]", "[1.0,1.0,1.0,1.0]")>]
    [<InlineData("[1.0,0.1,7.0]", "[1.0,null,1.0]")>]
    [<InlineData("[1.0,0.1,7.0]", "[1.0,\"NaN\",1.0]")>]
    [<InlineData("[1.0,0.1,7.0]", "[1.0,1e400,1.0]")>]
    [<InlineData("[1.0,0.1,7.0]", "[1.0,true,1.0]")>]
    [<InlineData("\"choice:2\":1.2", "\"choice:2\":\"1.2\"")>]
    [<InlineData("\"choice:2\":1.2", "\"choice:2\":1e400")>]
    [<InlineData("\"choice:2\":1.2", "\"choice:2\":1.2,\"choice:2\":1.4")>]
    [<InlineData("choice:2", "choice:1")>]
    [<InlineData("noul:2", "boolean:2")>]
    [<InlineData("\"max_len\":512", "\"max_len\":1024")>]
    [<InlineData("\"head_max_len\":192", "\"head_max_len\":193")>]
    [<InlineData("\"temperature\":", "\"unknown_temperature\":")>]
    let ``invalid temperature or length configuration is rejected``
        (before: string, after: string)
        =
        use fixture = new Fixture()
        let path = Path.Combine(fixture.Directory, "rl_agent_config.json")
        File.WriteAllText(path, agentJson.Replace(before, after))

        Assert.Throws<InvalidDataException>(fun () -> AgentConfiguration.read path |> ignore)
        |> ignore

    [<Fact>]
    let ``sidecar verification validates contracts without pretending to certify graph`` () =
        use fixture = new Fixture()
        let manifest = sidecars fixture tokenizerJson agentJson
        let configuration = manifest |> ModelBundle.verifySidecars fixture.Directory
        Assert.Equal(1.2, (AgentConfiguration.temperatures configuration)["choice:2"])

    [<Theory>]
    [<InlineData("\"BPE\"", "\"WordPiece\"")>]
    [<InlineData("\"NFC\"", "\"NFKC\"")>]
    [<InlineData("\"ByteLevel\"", "\"Whitespace\"")>]
    [<InlineData("\"add_prefix_space\":false", "\"add_prefix_space\":true")>]
    [<InlineData("\"use_regex\":true", "\"use_regex\":false")>]
    [<InlineData("\"id\":3", "\"id\":4")>]
    [<InlineData("\"special\":true", "\"special\":\"true\"")>]
    [<InlineData("\"[CLS]\":0", "\"[CLS]\":3")>]
    [<InlineData("\"[SEP]\":1", "\"missing\":1")>]
    let ``unsupported tokenizer sidecars are rejected`` (before: string, after: string) =
        use fixture = new Fixture()
        let manifest = sidecars fixture (tokenizerJson.Replace(before, after)) agentJson

        Assert.Throws<InvalidDataException>(fun () ->
            manifest |> ModelBundle.verifySidecars fixture.Directory |> ignore)
        |> ignore

    [<Theory>]
    [<InlineData("")>]
    [<InlineData("{")>]
    [<InlineData("null")>]
    [<InlineData("[]")>]
    [<InlineData("{\"schemaVersion\": null}")>]
    let ``malformed or wrong-root JSON reports invalid data`` (json: string) =
        use fixture = new Fixture()

        Assert.Throws<InvalidDataException>(fun () -> fixture.ReadManifest json |> ignore)
        |> ignore

    [<Fact>]
    let ``manifest itself cannot be a symlink`` () =
        use fixture = new Fixture()
        fixture.ReadManifest manifestJson |> ignore
        let link = Path.Combine(fixture.Directory, "linked.json")

        File.CreateSymbolicLink(link, Path.Combine(fixture.Directory, "manifest.json"))
        |> ignore

        Assert.Throws<InvalidDataException>(fun () -> ModelBundle.readManifest link |> ignore)
        |> ignore

    [<Fact>]
    let ``dangling artifact symlink is invalid data not a missing regular file`` () =
        use fixture = new Fixture()

        File.CreateSymbolicLink(
            Path.Combine(fixture.Directory, "tokenizer.json"),
            Path.Combine(fixture.Directory, "absent")
        )
        |> ignore

        let artifact = fixture.ReadManifest manifestJson |> tokenizer

        Assert.Throws<InvalidDataException>(fun () ->
            BundleArtifact.verify fixture.Directory artifact)
        |> ignore

    [<Fact>]
    let ``sidecar verification does not skip hash checking`` () =
        use fixture = new Fixture()
        let manifest = sidecars fixture tokenizerJson agentJson
        let changed = agentJson.Replace("1.2", "1.3")
        File.WriteAllText(Path.Combine(fixture.Directory, "rl_agent_config.json"), changed)

        Assert.Throws<InvalidDataException>(fun () ->
            ModelBundle.verifySidecars fixture.Directory manifest |> ignore)
        |> ignore

    [<Fact>]
    let ``manifest size is bounded before JSON parsing`` () =
        use fixture = new Fixture()
        let huge = String(' ', 1048577)

        Assert.Throws<InvalidDataException>(fun () -> fixture.ReadManifest huge |> ignore)
        |> ignore

    [<Fact>]
    let ``all finite temperatures remain available for explicit downstream clamping`` () =
        use fixture = new Fixture()
        let path = Path.Combine(fixture.Directory, "rl_agent_config.json")
        File.WriteAllText(path, agentJson.Replace("[1.0,0.1,7.0]", "[-1.0,0.0,1e300]"))
        let configuration = AgentConfiguration.read path
        Assert.Equal(-1.0, (AgentConfiguration.temperatures configuration)["temperature[0]"])
        Assert.Contains("temperature[0]", AgentConfiguration.clampingRequired configuration)

    [<Fact>]
    let ``many added tokens preserve the last token and matching vocabulary entries`` () =
        use fixture = new Fixture()

        let entries =
            ("[MASK]", 3) :: [ for index in 0..4095 -> $"extra{index}", index + 4 ]

        let tokenizer = withAddedTokens entries
        let controls = tokenizerConfigJson.Replace("[CLS]", "extra4095")
        let manifest = sidecarsWithControls fixture tokenizer controls agentJson
        let configuration = ModelBundle.verifySidecars fixture.Directory manifest
        Assert.Equal(1.2, (AgentConfiguration.temperatures configuration)["choice:2"])

    [<Theory>]
    [<InlineData("new", 0, "other", 4)>]
    [<InlineData("[MASK]", 4, "other", 5)>]
    [<InlineData("new", 4, "other", 4)>]
    [<InlineData("new", 4, "new", 5)>]
    let ``added token content and ID collisions are rejected``
        (firstContent: string, firstId: int, secondContent: string, secondId: int)
        =
        use fixture = new Fixture()
        let tokenizer = withAddedTokens [ firstContent, firstId; secondContent, secondId ]
        let manifest = sidecars fixture tokenizer agentJson

        Assert.Throws<InvalidDataException>(fun () ->
            ModelBundle.verifySidecars fixture.Directory manifest |> ignore)
        |> ignore

    [<Fact>]
    let ``late vocabulary ID collision after many added tokens is rejected`` () =
        use fixture = new Fixture()

        let entries =
            [ for index in 0..4095 -> $"extra{index}", index + 4 ] @ [ "collision", 0 ]

        let manifest = sidecars fixture (withAddedTokens entries) agentJson

        Assert.Throws<InvalidDataException>(fun () ->
            ModelBundle.verifySidecars fixture.Directory manifest |> ignore)
        |> ignore

    [<Theory>]
    [<InlineData("\\uD800")>]
    [<InlineData("\\uDC00")>]
    let ``malformed Unicode manifest values report invalid data`` (escaped: string) =
        use fixture = new Fixture()
        let json = manifestJson.Replace("laya-decision-v1", escaped)

        let error =
            Assert.Throws<InvalidDataException>(fun () -> fixture.ReadManifest json |> ignore)

        Assert.IsType<InvalidOperationException>(error.InnerException) |> ignore

    [<Theory>]
    [<InlineData("\\uD800")>]
    [<InlineData("\\uDC00")>]
    let ``malformed Unicode manifest property names report invalid data`` (escaped: string) =
        use fixture = new Fixture()
        let json = manifestJson.Replace("\"bundleId\":", $"\"{escaped}\":")

        let error =
            Assert.Throws<InvalidDataException>(fun () -> fixture.ReadManifest json |> ignore)

        Assert.IsType<InvalidOperationException>(error.InnerException) |> ignore

    [<Theory>]
    [<InlineData("\"[CLS]\":0", "\"\\uD800\":0")>]
    [<InlineData("\"content\":\"[MASK]\"", "\"content\":\"\\uD800\"")>]
    let ``malformed Unicode in tokenizer metadata reports invalid data``
        (before: string, after: string)
        =
        use fixture = new Fixture()
        let manifest = sidecars fixture (tokenizerJson.Replace(before, after)) agentJson

        Assert.Throws<InvalidDataException>(fun () ->
            ModelBundle.verifySidecars fixture.Directory manifest |> ignore)
        |> ignore

    [<Fact>]
    let ``malformed Unicode temperature bucket names report invalid data`` () =
        use fixture = new Fixture()
        let path = Path.Combine(fixture.Directory, "rl_agent_config.json")
        File.WriteAllText(path, agentJson.Replace("choice:2", "\\uD800"))

        Assert.Throws<InvalidDataException>(fun () -> AgentConfiguration.read path |> ignore)
        |> ignore

    [<Fact>]
    let ``valid surrogate pairs remain accepted in token names and content`` () =
        use fixture = new Fixture()
        let tokenizer = tokenizerJson.Replace("[MASK]", "\\uD83D\\uDE00")
        let controls = tokenizerConfigJson.Replace("[MASK]", "\\uD83D\\uDE00")
        let manifest = sidecarsWithControls fixture tokenizer controls agentJson
        ModelBundle.verifySidecars fixture.Directory manifest |> ignore

    [<Theory>]
    [<InlineData("value")>]
    [<InlineData("property")>]
    let ``verifier reports structured nonzero errors for malformed Unicode`` (location: string) =
        task {
            use fixture = new Fixture()

            let json =
                if location = "value" then
                    manifestJson.Replace("laya-decision-v1", "\\uD800")
                else
                    manifestJson.Replace("\"bundleId\":", "\"\\uD800\":")

            let manifestPath = Path.Combine(fixture.Directory, "manifest.json")
            File.WriteAllText(manifestPath, json)

            let script =
                Path.GetFullPath(
                    Path.Combine(__SOURCE_DIRECTORY__, "../../scripts/VerifyArtifacts.fsx")
                )

            let start =
                ProcessStartInfo(
                    "dotnet",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                )

            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"

            for argument in
                [
                    "fsi"
                    "--exec"
                    "--"
                    script
                    "--model-dir"
                    fixture.Directory
                    "--manifest"
                    manifestPath
                ] do
                start.ArgumentList.Add argument

            use child = Process.Start start
            let output = child.StandardOutput.ReadToEndAsync()
            let errors = child.StandardError.ReadToEndAsync()
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 60.0)

            try
                do! child.WaitForExitAsync timeout.Token
            with :? OperationCanceledException ->
                child.Kill(true)
                failwith "Verifier process exceeded its safety deadline."

            let! output = output
            let! errors = errors
            Assert.Equal(2, child.ExitCode)
            Assert.True(String.IsNullOrWhiteSpace errors, errors)
            use document = JsonDocument.Parse output
            Assert.Equal("blocked", document.RootElement.GetProperty("status").GetString())
            Assert.False(document.RootElement.GetProperty("integrityVerified").GetBoolean())
            Assert.False(document.RootElement.GetProperty("nativeCertified").GetBoolean())

            let expectedDetail =
                if location = "value" then
                    "Malformed Unicode in JSON string value."
                else
                    "Malformed Unicode in JSON property name."

            Assert.Equal<string>(
                expectedDetail,
                document.RootElement.GetProperty("detail").GetString()
            )
        }
