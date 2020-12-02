module TestNaming

open System
open System.Reflection
open Fuchu

// We report tests to Microsoft's test framework through a TestCase object.
// When the user runs test selectively, we will receive a collection of TestCase
// objects with the same values as the ones we supplied when reporting the test.
// TestCase has the following properties
//
//  Property
//  ExecutorUri         The executor that will run this - for us, this is always
//                          executor://Fuchu.VisualStudio
//  Source              The path to the DLL containing the test
//  CodeFilePath        The path to the source file in which this test is defined
//  LineNumber          The line number within CodeFilePath where this test is defined
//  Id                  A Guid uniquely identifying this test
//  FullyQualifiedName  A structured name that determines where this test appears in
//                          the Test Explorer
//  DisplayName         The test name shown for the node in Test Explorer, and anywhere
//                          else a name is required (e.g., the Test Detail Summary panel)
//  
//  Properties          A dictionary - I think we could use this for our own purposes?
//  

let private isFsharpFuncType t =
    let baseType =
        let rec findBase (t:Type) =
            if t.BaseType = typeof<obj> then
                t
            else
                findBase t.BaseType
        findBase t
    baseType.IsGenericType && baseType.GetGenericTypeDefinition() = typedefof<FSharpFunc<unit, unit>>

// If the test function we've found doesn't seem to be in the test assembly, it's
// possible we're looking at an FsCheck 'testProperty' style check. In that case,
// the function of interest (i.e., the one in the test assembly, and for which we
// might be able to find corresponding source code) is referred to in a field
// of the function object.
let getFuncTypeToUse (testFunc:TestCode) (asm:Assembly) =
    let t = testFunc.GetType()
    if t.Assembly.FullName = asm.FullName then
        t
    else
        let nestedFunc =
             t.GetFields()
            |> Seq.tryFind (fun f -> isFsharpFuncType f.FieldType)
        match nestedFunc with
            | Some f -> f.GetValue(testFunc).GetType()
            | None -> t

type NameInfo = { DisplayName: string; TypeName: string; MethodName: string; }

let nameInfoFromTestNameAndTestCode (asm:Assembly) (name: string, testFunc:TestCode) =
    let t = getFuncTypeToUse testFunc asm
    let m =
        query
          {
            for m in t.GetMethods() do
            where ((m.Name = "Invoke") && (m.DeclaringType = t))
            exactlyOne
          }
    { DisplayName = name; TypeName = t.FullName; MethodName = m.Name; }

// TODO: if we were to strip out the @ and number that make the test names look odd, could we still put the true
// name somewhere to ensure uniqueness?
// Alternatively, could we somehow infer what the symbol name is, maybe working back to the property that exposes it?
let makeFullyQualifiedName (nameInfo:NameInfo) =
    nameInfo.TypeName.Replace("+", ".") + "." + nameInfo.MethodName