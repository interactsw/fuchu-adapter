﻿namespace RemotingHelpers

open System
open System.IO
open System.Reflection
open System.Security
open System.Security.Permissions

#if NETFX
open System.Runtime.Remoting
open System.Security.Policy
#else
#endif

/// <summary>
/// Base class for objects accessible across AppDomain boundaries with
/// no lease timeout.
/// </summary>
/// <remarks>
/// We need to execute tests for each test assembly in an AppDomain created
/// with that assembly's location as a codebase, to ensure that we pick up
/// configuration settings such as assembly binding redirects. We therefore
/// need objects capable of cross-AppDomain usage, which means we need to
/// use .NET Remoting, which in turn requires the objects at the boundary
/// to derive from MarshalByRefObject. However, by default objects used
/// in this way only work for as long as their 'lease' lives. This is a
/// mechanism designed to avoid leaking remote objects when you lose network
/// connectivity, but it's unhelpful for intra-process cross-AppDomain
/// communication - it just means that objects become inaccessible after
/// a certain length of time. So this base class extends the lease to be
/// indefinite, preventing early removal.
/// </remarks>
type MarshalByRefObjectInfiniteLease() =
#if NETFX
    inherit MarshalByRefObject()
        override this.InitializeLifetimeService() : obj = null
    interface IDisposable with
        member this.Dispose() = ignore <| RemotingServices.Disconnect(this)
#else
    interface IDisposable with
        member this.Dispose() = ()
#endif

/// <summary>
/// Provides an AppDomain for a particular test assembly.
/// </summary>
/// <remarks>
/// We create an AppDomain for each test, with the codebase set to the
/// assembly's location to ensure that assemblies are resolved correctly.
/// </remarks>
type TestAssemblyHost(source) =
#if NETFX
    let mutable appDomain =
        let setup =
            let assemblyFullPath = Path.GetFullPath(source)
            let configFullPath = assemblyFullPath + ".config";
            let configFullPath =
                if File.Exists(configFullPath)
                    then configFullPath
                    else null
            new AppDomainSetup
                (ApplicationBase = Path.GetDirectoryName(assemblyFullPath),
                 ApplicationName = Guid.NewGuid().ToString(),
                 ConfigurationFile = configFullPath)
        let current = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName
        let evidence = new Evidence(AppDomain.CurrentDomain.Evidence)
        AppDomain.CreateDomain(setup.ApplicationName, evidence, setup)
#endif
    interface IDisposable with
        member this.Dispose() =
#if NETFX
            if appDomain <> null then
                AppDomain.Unload(appDomain)
                appDomain <- null

    /// <summary>
    /// Create an instance of the specified type in the test AppDomain.
    /// </summary>
    member this.CreateInAppdomain<'P when 'P :> MarshalByRefObject and 'P : (new : unit -> 'P)>() =
        appDomain.CreateInstanceFromAndUnwrap((typeof<'P>).Assembly.Location, typeof<'P>.FullName) :?> 'P

    /// <summary>
    /// Create an instance of the specified type in the test AppDomain, using the
    /// constructor arguments supplied.
    /// </summary>
    member this.CreateInAppdomain<'P when 'P :> MarshalByRefObject>(args:obj []) =
        let t = typeof<'P>
        let asm = t.Assembly
        appDomain.CreateInstanceFromAndUnwrap(
            asm.Location,
            t.FullName,
            false,
            BindingFlags.Default,
            null,
            args,
            null,
            null) :?> 'P
#else
            ()
#endif
