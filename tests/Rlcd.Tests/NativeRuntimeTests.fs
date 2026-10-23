namespace Rlcd.Tests

open System.Runtime.InteropServices

open Microsoft.ML.OnnxRuntime
open Xunit

[<Trait("Category", "NativeRuntime")>]
module NativeRuntimeTests =
    [<Fact>]
    let ``pinned native runtime loads and exposes the CPU execution provider`` () =
        let environment = OrtEnv.Instance()
        let version = environment.GetVersionString()
        let providers = environment.GetAvailableProviders()

        printfn
            "Native ONNX Runtime %s; architecture %A; providers: %s"
            version
            RuntimeInformation.ProcessArchitecture
            (String.concat ", " providers)

        Assert.Equal("1.29.0", version)
        Assert.Contains("CPUExecutionProvider", providers)
