namespace Execution

open System
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Reflection

open Microsoft.VisualStudio.TestPlatform.ObjectModel
open Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter
open Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging

open Newtonsoft.Json

open Fuchu
open Fuchu.Impl

open Filters
open RemotingHelpers
open Diagnostics
open TestNaming

// When running in the .NET Framework, we run tests in a separate AppDomain to enable us to isolate
// assembly loading. This means that calls back into the ITestExecutionRecorder to report our
// progress must cross back into the AppDomain in which the test framework originally launched
// our executor. This in turn means that any information that we need to pass into the
// ITestExecutionRecorder needs to cross over that AppDomain boundary. To enable this, we
// serialize all the properties as JSON, meaning that the only thing we need to marshal over
// this AppDomain boundary is a string. (We also do this in .NET Core and .NET 5+. It's not
// necessary in these runtimes, but we do it anyway so that the code works the same way in
// all runtimes.)
// Here are the keys of the various things that might go into that JSON. Note that most of these
// are optional.
module private Keys =
    let FullyQualifiedName = "FullyQualifiedName"
    let DisplayName = "DisplayName"
    let Message = "Message"
    let Duration = "Duration"
    let StackTrace = "StackTrace"

// This lives in the main AppDomain - the one in which the test framework instantiated
// our test executor - so it is able to have a direct reference to the ITestExecutionRecorder.
// In .NET Framework scenarios, this is made available via .NET Remoting in the AppDomain in
// which we host the code under test.
// The interface we present remotely is an IObserver<string*string>, where the first tuple
// member indicates which type of message we want to report to the ITestExecutionRecorder,
// and the second is a JSON string containing all of the properties required for that
// particular kind of message.
type TestExecutionRecorderProxy(recorder:ITestExecutionRecorder, assemblyPath:string) =
    inherit MarshalByRefObjectInfiniteLease()
    interface IObserver<string * string> with
        member this.OnCompleted(): unit = 
            ()
        member x.OnError(error: exn): unit = 
            recorder.SendMessage(TestMessageLevel.Error, error.ToString())
        member x.OnNext((messageType, message): string * string): unit = 
            match messageType with
            | "LogInfo" -> recorder.SendMessage(TestMessageLevel.Informational, message)
            | "CaseStarted" ->
                let testCase = new TestCase(message, Ids.ExecutorUri, assemblyPath)
                recorder.RecordStart(testCase)
            | "CasePassed" ->
                let d = JsonConvert.DeserializeObject<Dictionary<string, string>>(message)
                let fullyQualifiedName = d.[Keys.FullyQualifiedName]
                let displayName = d.[Keys.DisplayName]
                let duration = TimeSpan.ParseExact(d.[Keys.Duration], "c", System.Globalization.CultureInfo.InvariantCulture)
                let testCase = new TestCase(fullyQualifiedName, Ids.ExecutorUri, assemblyPath)
                let result =
                    new Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult
                        (testCase,
                         DisplayName = displayName,
                         Outcome = TestOutcome.Passed,
                         Duration = duration,
                         ComputerName = Environment.MachineName)
                recorder.RecordResult(result)
                recorder.RecordEnd(testCase, result.Outcome)
            | "CaseFailed" ->
                let d = JsonConvert.DeserializeObject<Dictionary<string, string>>(message)
                let fullyQualifiedName = d.[Keys.FullyQualifiedName]
                let displayName = d.[Keys.DisplayName]
                let message = d.[Keys.Message]
                let stackTrace = d.[Keys.StackTrace]
                let duration = TimeSpan.ParseExact(d.[Keys.Duration], "c", System.Globalization.CultureInfo.InvariantCulture)
                let testCase = new TestCase(fullyQualifiedName, Ids.ExecutorUri, assemblyPath)
                let result =
                    new Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult
                        (testCase,
                         DisplayName = displayName,
                         ErrorMessage = message,
                         ErrorStackTrace = stackTrace,
                         Outcome = TestOutcome.Failed,
                         Duration = duration,
                         ComputerName = Environment.MachineName)
                recorder.RecordResult(result)
                recorder.RecordEnd(testCase, result.Outcome)
            | "CaseSkipped" ->
                let d = JsonConvert.DeserializeObject<Dictionary<string, string>>(message)
                let fullyQualifiedName = d.[Keys.FullyQualifiedName]
                let displayName = d.[Keys.DisplayName]
                let message = d.[Keys.Message]
                let testCase = new TestCase(fullyQualifiedName, Ids.ExecutorUri, assemblyPath)
                let result =
                    new Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult
                        (testCase,
                         DisplayName = displayName,
                         ErrorMessage = message,
                         Outcome = TestOutcome.Skipped,
                         ComputerName = Environment.MachineName)
                recorder.RecordResult(result)
                recorder.RecordEnd(testCase, result.Outcome)
            | _ -> recorder.SendMessage(TestMessageLevel.Error, Printf.sprintf "Fuchu.Adapter internal error - recorder proxy received unknown message type: %s" messageType)
        

type VsCallbackForwarder(observer: IObserver<string * string>, assemblyPath: string) =
    let testEnded (messageType, values: (string*string) list) =
        let textStream = new System.IO.MemoryStream()
        let d = values.ToDictionary((fun (k, _) -> k), (fun (_, v) -> v))
        let text = JsonConvert.SerializeObject(d)
        observer.OnNext((messageType, text))
        
    member this.LogInfo (message:string) =
        observer.OnNext(("LogInfo", message))

    member this.CaseStarted(name: string) =
        observer.OnNext("CaseStarted", name)

    member this.CasePassed(fullyQualifiedName: string, displayName: string, duration: TimeSpan) =
        testEnded (
            "CasePassed",
            [
                Keys.FullyQualifiedName, fullyQualifiedName;
                Keys.DisplayName, displayName;
                Keys.Duration, duration.ToString("c")
            ])

    member this.CaseFailed(fullyQualifiedName: string, displayName: string, message: string, stackTrace: string, duration: TimeSpan) =
        testEnded (
            "CaseFailed",
            [
                Keys.FullyQualifiedName, fullyQualifiedName;
                Keys.DisplayName, displayName;
                Keys.Message, message;
                Keys.StackTrace, stackTrace;
                Keys.Duration, duration.ToString("c")
            ])

    member this.CaseSkipped(fullyQualifiedName: string, displayName: string, message: string) =
        testEnded (
            "CaseSkipped",
            [
                Keys.FullyQualifiedName, fullyQualifiedName;
                Keys.DisplayName, displayName;
                Keys.Message, message
            ])

type private LogWrapper(vsCallback:VsCallbackForwarder) =
    interface GenericLogger with
        member this.Trace(msg: string) =
            vsCallback.LogInfo(msg)
        member this.Info(msg: string): unit = 
            vsCallback.LogInfo(msg)
        member this.Warning(msg: string): unit = 
            raise (System.NotImplementedException())
        member this.Error(msg: string): unit = 
            raise (System.NotImplementedException())

// The curious 1-tuple argument here is so that we can force the AppDomain class to locate the correct
// constructor. It enables us to pass in an argument that is unambiguously typed as the required
// interface. (Otherwise, it ends up being of type TestExecutionRecorderProxy, and the dynamic
// creation attempt seems not to be able to work out that it needs the constructor that takes an
// IObserver<string*string>)
type ExecuteProxy(proxyHandler: Tuple<IObserver<string * string>>, assemblyPath: string, testsToInclude: string[]) =
    inherit MarshalByRefObjectInfiniteLease()
    let vsCallback: VsCallbackForwarder = new VsCallbackForwarder(proxyHandler.Item1, assemblyPath)
    let logger = new FuchuAdapterLogger(new LogWrapper(vsCallback))
    member this.ExecuteTests() =
        use _logExecution =
            if testsToInclude = null then
                logger.ExecutingAllTests(assemblyPath)
            else
                logger.ExecutingSpecificTests(assemblyPath, testsToInclude)

        let asm = Assembly.LoadFrom(assemblyPath)
        if not (asm.GetReferencedAssemblies().Any(fun a -> a.Name = "Fuchu")) then
            vsCallback.LogInfo(sprintf "Skipping: %s because it does not reference Fuchu" assemblyPath)
        else            
            let tests =
                match testFromAssembly (asm) with
                | Some t -> t
                | None -> TestList []
            //let testList: seq<fullyQualifiedName:string, displayName:string, testCode:TestCode> =
            let testList: seq<string * string * TestCode> =
                let allTests = Fuchu.Test.toTestCodeList tests
                vsCallback.LogInfo(sprintf "All tests: %d" (allTests.Count()))

                // Fuchu puts the display name into the string part of these tuples. That's not
                // necessarily globally unique - it's only unique within the scope of the
                // corresponding TestCode.
                // We rewrite them here to use the FullyQualifiedName we supplied to VS during
                // discovery so that when the test running invokes our 'printer', we're able
                // to correlate each report to an exact test. But we still need access to
                // the display name (i.e., the name Fuchu reports back in toTestCodeList)
                // so that we can fill in the TestCase.DisplayName propertly, so 
                let allTestsWithNames =
                    allTests
                    |> Seq.map (fun (displayName, tc) ->
                        (
                            nameInfoFromTestNameAndTestCode asm (displayName, tc) |> makeFullyQualifiedName,
                            displayName,
                            tc
                        ))
                if testsToInclude = null then
                    allTestsWithNames
                else
                    let requiredTests = testsToInclude |> HashSet
                    allTestsWithNames
                    |> Seq.filter (fun (fullyQualifiedName, _, tc) ->
                        let fullyQualifiedTestName = fullyQualifiedName
                        vsCallback.LogInfo(sprintf "Considering including: %s" fullyQualifiedTestName)
                        requiredTests.Contains(fullyQualifiedTestName))
            let (fullyQualifiedNameToDisplayName: Map<string, string>, testsByFullyQualifiedName:list<string * TestCode>) =
                testList |>
                (Seq.fold
                    (fun (m, tests) -> fun (fqn, dn, t) -> ((Map.add fqn dn m), ((fqn, t)::tests)))
                    (Map.empty<string, string>, List.empty)
                )
            vsCallback.LogInfo(sprintf "Number of tests included: %d" (testList.Count()))
            let pmap (f: _ -> _) (s: _ seq) = s.AsParallel().Select(f) :> _ seq

            let displayNameFromFullyQualifiedName fullyQualifiedName =
                fullyQualifiedNameToDisplayName.[fullyQualifiedName]
            let testPrinters =
                {
                    BeforeRun = vsCallback.CaseStarted
                    Passed = (fun fullyQualifiedName duration -> vsCallback.CasePassed(fullyQualifiedName, (displayNameFromFullyQualifiedName fullyQualifiedName), duration))
                    Ignored = (fun fullyQualifiedName message -> vsCallback.CaseSkipped(fullyQualifiedName, (displayNameFromFullyQualifiedName fullyQualifiedName), message))
                    Failed = (fun fullyQualifiedName message duration -> vsCallback.CaseFailed(fullyQualifiedName, (displayNameFromFullyQualifiedName fullyQualifiedName), message, null, duration))
                    Exception = (fun fullyQualifiedName ex duration -> vsCallback.CaseFailed(fullyQualifiedName, (displayNameFromFullyQualifiedName fullyQualifiedName), ex.Message, ex.StackTrace, duration))
                }
            evalTestList testPrinters pmap testsByFullyQualifiedName
            //evalTestList testPrinters Seq.map testsByFullyQualifiedName
            |> Seq.toList   // Force evaluation
            |> ignore

type AssemblyExecutor(proxyHandler: IObserver<string * string>, assemblyPath: string, testsToInclude: string[]) =
    let host = new TestAssemblyHost(assemblyPath)
    let wrappedArg: Tuple<IObserver<string*string>> = Tuple.Create(proxyHandler)
#if NETFX
    let proxy = host.CreateInAppdomain<ExecuteProxy>([|wrappedArg; assemblyPath; testsToInclude|])
#else
    let proxy = new ExecuteProxy(wrappedArg, assemblyPath, testsToInclude)
#endif
    interface IDisposable with
        member this.Dispose() =
            (host :> IDisposable).Dispose()
    member this.ExecuteTests() =
        proxyHandler.OnNext("LogInfo", "Executing tests in assembly " + assemblyPath)
        proxy.ExecuteTests()


[<ExtensionUri(Ids.ExecutorId)>]
type FuchuTestExecutor() =
    let mutable (executors:AssemblyExecutor []) = null

    let runAllExecutors () =
        executors
        |> Seq.map
            (fun executor -> async { executor.ExecuteTests() })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore
        for executor in executors do
            (executor :> IDisposable).Dispose()
        executors <- null

    interface ITestExecutor with
        member x.Cancel(): unit = 
            failwith "Not implemented yet"
        member x.RunTests(tests: IEnumerable<TestCase>, runContext: IRunContext, frameworkHandle: IFrameworkHandle): unit =
            let recorder = frameworkHandle :> ITestExecutionRecorder
            recorder.SendMessage(Logging.TestMessageLevel.Informational, "Running selected tests")
            for t in tests do
                recorder.SendMessage(
                    Logging.TestMessageLevel.Informational,
                    (sprintf "%A" t))
                recorder.SendMessage(
                    Logging.TestMessageLevel.Informational,
                    (sprintf "DisplayName: '%s' FullyQualifiedName: '%s' Id: '%s' CodeFilePath: '%s' LineNumber: %d Source: '%s'" t.DisplayName t.FullyQualifiedName (t.Id.ToString()) t.CodeFilePath t.LineNumber t.Source))
            try
                let testsByAssembly =
                    query
                      {
                        for testCase in tests do
                        groupBy testCase.Source
                      }
                executors <-
                    testsByAssembly
                    |> Seq.map
                        (fun testGroup ->
                            let assemblyPath = testGroup.Key
                            let callbackProxy:IObserver<string*string> = new TestExecutionRecorderProxy(recorder, assemblyPath) :> IObserver<string*string>
                            let testNames =
                                query
                                  {
                                    for test in testGroup do
                                    select test.FullyQualifiedName
                                  }
                                |> Array.ofSeq
                            new AssemblyExecutor(callbackProxy, assemblyPath, testNames))
                    |> Array.ofSeq

                runAllExecutors ()
            with
            | x -> (frameworkHandle :> ITestExecutionRecorder).SendMessage(Logging.TestMessageLevel.Error, x.ToString())

        member x.RunTests(sources: IEnumerable<string>, runContext: IRunContext, frameworkHandle: IFrameworkHandle): unit =
            let recorder = frameworkHandle :> ITestExecutionRecorder
            recorder.SendMessage(Logging.TestMessageLevel.Informational, sprintf "Running all tests (%s)" (FileVersionInfo.GetVersionInfo(typeof<FuchuTestExecutor>.Assembly.Location).FileVersion))
            try
                executors <-
                    sourcesUsingFuchu sources
                    |> Seq.map
                        (fun assemblyPath ->
                            let callbackProxy:IObserver<string*string> = new TestExecutionRecorderProxy(recorder, assemblyPath) :> IObserver<string*string>
                            recorder.SendMessage(Logging.TestMessageLevel.Informational, ("Creating test host for " + assemblyPath))
                            new AssemblyExecutor(callbackProxy, assemblyPath, null))
                    |> Array.ofSeq

                runAllExecutors ()
            with
            | x -> (frameworkHandle :> ITestExecutionRecorder).SendMessage(Logging.TestMessageLevel.Error, x.ToString())

