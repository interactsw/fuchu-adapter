namespace Discovery

open System
open System.Linq
open System.Reflection

open Microsoft.VisualStudio.TestPlatform.ObjectModel
open Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter
open Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging

open Fuchu
open Fuchu.Impl

open Filters
open RemotingHelpers
open SourceLocation
open TestNaming

// Can we use this in Execution too? To make naming handling common? Or is there a reason this is a struct?
// I think it's a struct because we need it to be able to traverse AppDomain boundaries when running on .NET FX.

type DiscoveryResult =
    struct
        val DisplayName: string
        val TypeName:    string
        val MethodName:  string
        new(displayName: string, typeName: string, methodName: string) =
            { DisplayName = displayName; TypeName = typeName; MethodName = methodName }
    end

type VsDiscoverCallbackProxy(log: IMessageLogger) =
    inherit MarshalByRefObjectInfiniteLease()
    interface IObserver<string> with
        member this.OnCompleted(): unit = 
            ()
        member x.OnError(error: exn): unit = 
            log.SendMessage(TestMessageLevel.Error, error.ToString())
        member x.OnNext(message: string): unit = 
            log.SendMessage(TestMessageLevel.Informational, message)

type DiscoverProxy(proxyHandler:Tuple<IObserver<string>>) =
    inherit MarshalByRefObjectInfiniteLease()
    let observer = proxyHandler.Item1

    member this.DiscoverTests(source: string) =
        observer.OnNext("DiscoverTests")

        let asm = Assembly.LoadFrom(source)
        observer.OnNext("Loaded assembly")
        for a in asm.GetReferencedAssemblies() do
            observer.OnNext(a.FullName)


        if not (asm.GetReferencedAssemblies().Any(fun a -> a.Name = "Fuchu")) then
            observer.OnNext(sprintf "Skipping: %s because it does not reference Fuchu" source)
            Array.empty
        else            
            observer.OnNext(sprintf "%s references Fuchu" source)
            let tests =
                match testFromAssembly (asm) with
                | Some t -> t
                | None -> TestList []
            observer.OnNext(sprintf "%s: %d tests" source (Seq.length (Fuchu.Test.toTestCodeList tests)))
            for (s, tc) in (Fuchu.Test.toTestCodeList tests) do
                observer.OnNext(sprintf " %s: %A tests" s tc)
            Fuchu.Test.toTestCodeList tests
            |> Seq.map (fun (name, testFunc) ->
                let nameInfo = nameInfoFromTestNameAndTestCode asm (name, testFunc)
                new DiscoveryResult(nameInfo.DisplayName, nameInfo.TypeName, nameInfo.MethodName))
            |> Array.ofSeq

[<FileExtension(".dll")>]
[<FileExtension(".exe")>]
[<DefaultExecutorUri(Ids.ExecutorId)>]
type Discoverer() =
    interface ITestDiscoverer with
        member x.DiscoverTests
            (sources: System.Collections.Generic.IEnumerable<string>,
             discoveryContext: IDiscoveryContext,
             logger: IMessageLogger,
             discoverySink: ITestCaseDiscoverySink): unit =
            try
#if NETFX
                let platform = ".NET FX"
#else
                let platform = ".NET Core"
#endif
                System.Console.WriteLine("discovering tests")
                logger.SendMessage(TestMessageLevel.Informational, "Fuchu.TestAdapter " + platform + ", " + (DateTime.Now.ToString()))
                let vsCallback = new VsDiscoverCallbackProxy(logger)
                for assemblyPath in (sourcesUsingFuchu sources) do
                    logger.SendMessage(TestMessageLevel.Informational, "Fuchu.TestAdapter inspecting: " + assemblyPath);
                    use host = new TestAssemblyHost(assemblyPath)
#if NETFX
                    let discoverProxy = host.CreateInAppdomain<DiscoverProxy>([|Tuple.Create<IObserver<string>>(vsCallback)|])
#else
                    let discoverProxy:DiscoverProxy = new DiscoverProxy(Tuple.Create<IObserver<string>>(vsCallback))
#endif
                    let obs = vsCallback :> IObserver<string>
                    obs.OnNext("Testing callback")
                    let testList = discoverProxy.DiscoverTests(assemblyPath)
                    logger.SendMessage(TestMessageLevel.Informational, (sprintf "Fuchu.TestAdapter %s: found %d tests"  assemblyPath (Array.length testList)));

                    let locationFinder = new SourceLocationFinder(assemblyPath)
                    for { DisplayName = displayName; TypeName = typeName; MethodName = methodName } in testList do
                        logger.SendMessage(TestMessageLevel.Informational, (sprintf "Fuchu.TestAdapter %s: '%s' '%s' '%s'"  assemblyPath displayName typeName methodName));

                        let ni:NameInfo = { DisplayName = displayName; TypeName = typeName; MethodName = methodName }
                        let tc = new TestCase(makeFullyQualifiedName ni, Ids.ExecutorUri, assemblyPath, DisplayName = displayName)
                        match locationFinder.getSourceLocation typeName methodName with
                        | Some location ->
                            tc.CodeFilePath <- location.SourcePath
                            tc.LineNumber <- location.LineNumber
                        | None -> ()
                        logger.SendMessage(
                            TestMessageLevel.Informational,
                            (sprintf
                                "TestCast - FullyQualifiedName: '%s' Id: '%s' DisplayName: '%s' CodeFilePath: '%s' ExecutorUri: '%s' LineNumber: %d Source: '%s'"
                                tc.FullyQualifiedName
                                (tc.Id.ToString())
                                tc.DisplayName
                                tc.CodeFilePath
                                (tc.ExecutorUri.ToString())
                                tc.LineNumber
                                tc.Source))

                        discoverySink.SendTestCase(tc)
            with
            | x -> logger.SendMessage(Logging.TestMessageLevel.Error, x.ToString())

