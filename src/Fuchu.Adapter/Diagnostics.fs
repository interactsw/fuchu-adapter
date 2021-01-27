module Diagnostics

open System

type GenericLogger =
   abstract member Trace: string -> unit
   abstract member Info: string -> unit
   abstract member Warning: string -> unit
   abstract member Error: string -> unit

type private OnDisposed(onDisposed: unit -> unit) =
    interface IDisposable with
        member this.Dispose() =
            onDisposed()

type FuchuAdapterLogger(logger:GenericLogger) =
    member this.ExecutingAllTests (assemblyPath:string) : IDisposable =
        logger.Info(sprintf "Executing all tests in %s" assemblyPath)
        let after = new OnDisposed(fun () -> logger.Info(sprintf "Execution of all tests complete for %s" assemblyPath))
        after :> System.IDisposable

    member this.ExecutingSpecificTests (assemblyPath:string, testsToInclude: string[]) : IDisposable =
        logger.Info(sprintf "Executing tests from: %s. %d tests (%s)" assemblyPath testsToInclude.Length (String.Join(",", testsToInclude)))
        let after = new OnDisposed(fun () -> logger.Info(sprintf "Execution of tests complete for %s" assemblyPath))
        after :> System.IDisposable
