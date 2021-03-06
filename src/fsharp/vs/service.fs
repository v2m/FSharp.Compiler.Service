//----------------------------------------------------------------------------
// Copyright (c) 2002-2012 Microsoft Corporation. 
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.html file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
//----------------------------------------------------------------------------

// Open up the compiler as an incremental service for parsing,
// type checking and intellisense-like environment-reporting.

namespace Microsoft.FSharp.Compiler.SourceCodeServices

open Internal.Utilities
open System
open System.IO
open System.Text
open System.Threading
open System.Collections.Generic
 
open Microsoft.FSharp.Core.Printf
open Microsoft.FSharp.Compiler.AbstractIL
open Microsoft.FSharp.Compiler.AbstractIL.Internal  
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library  
open Microsoft.FSharp.Compiler 
open Microsoft.FSharp.Compiler.MSBuildResolver
open Microsoft.FSharp.Compiler.AbstractIL.Diagnostics 
open Microsoft.FSharp.Compiler.PrettyNaming
open Internal.Utilities.Collections
open Internal.Utilities.Debug
open System.Security.Permissions

open Microsoft.FSharp.Compiler.Env 
open Microsoft.FSharp.Compiler.Parser
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.ErrorLogger
open Microsoft.FSharp.Compiler.Lexhelp
open Microsoft.FSharp.Compiler.Build
open Microsoft.FSharp.Compiler.Tast
open Microsoft.FSharp.Compiler.Tastops
open Microsoft.FSharp.Compiler.Tastops.DebugPrint
open Microsoft.FSharp.Compiler.Lib
open Microsoft.FSharp.Compiler.AbstractIL.IL 
open Microsoft.FSharp.Compiler.Layout
open Microsoft.FSharp.Compiler.TypeChecker
open Microsoft.FSharp.Compiler.Infos
open Microsoft.FSharp.Compiler.Nameres
open Internal.Utilities.StructuredFormat
open ItemDescriptionIcons 
open ItemDescriptionsImpl 

[<AutoOpen>]
module EnvMisc =
#if SILVERLIGHT
    let GetEnvInteger e dflt = dflt
#else
    let GetEnvInteger e dflt = match System.Environment.GetEnvironmentVariable(e) with null -> dflt | t -> try int t with _ -> dflt
#endif
    let buildCacheSize   = GetEnvInteger "mFSharp_BuildCacheSize" 3
    let recentForgroundTypeCheckLookupSize = GetEnvInteger "mFSharp_RecentForegroundTypeCheckCacheSize" 5
    let braceMatchCacheSize = GetEnvInteger "mFSharp_BraceMatchCacheSize" 5
    let untypedCheckMruSize = GetEnvInteger "mFSharp_UntypedCheckMruCacheSize" 2
    let maxTypeCheckErrorsOutOfProjectContext = GetEnvInteger "mFSharp_MaxErrorsOutOfProjectContext" 3



//----------------------------------------------------------------------------
// Methods
//--------------------------------------------------------------------------

[<Sealed>]
type MethodGroupItemParameter(name: string, canonicalTypeTextForSorting: string, display: string, description: string) = 
    member __.ParameterName = name
    [<Obsolete("This member has been renamed to 'ParameterName'")>]
    member __.Name = name
    member __.CanonicalTypeTextForSorting = canonicalTypeTextForSorting
    member __.Display = display
    member __.Description = description

/// Format parameters for Intellisense completion
module internal Params = 
    let printCanonicalizedTypeName g (denv:DisplayEnv) tau =
        // get rid of F# abbreviations and such
        let strippedType = stripTyEqnsWrtErasure EraseAll g tau
        // pretend no namespaces are open
        let denv = denv.SetOpenPaths([])
        // now printing will see a .NET-like canonical representation, that is good for sorting overloads into a reasonable order (see bug 94520)
        NicePrint.stringOfTy denv strippedType

    let ParamOfRecdField g denv f decription =
        MethodGroupItemParameter(
          name = f.rfield_id.idText,
          canonicalTypeTextForSorting = printCanonicalizedTypeName g denv f.rfield_type,
          display = NicePrint.prettyStringOfTy denv f.rfield_type,
          description = decription
        )
    
    let ParamOfUnionCaseField g denv isGenerated (i : int) f = 
        let description = if isGenerated i f then "" else NicePrint.stringOfParamData denv (ParamData(false, false, NotOptional, Some f.rfield_id, f.rfield_type)) 
        ParamOfRecdField g denv f description

    // TODO this code is similar to NicePrint.fs:formatParamDataToBuffer, refactor or figure out why different?
    let ParamsOfParamDatas g denv (paramDatas:ParamData list) rty = 
        let paramNames,paramPrefixes,paramTypes = 
            paramDatas 
            |> List.map (fun (ParamData(isParamArrayArg,_,optArgInfo,nmOpt,pty)) -> 
                let isOptArg = optArgInfo.IsOptional
                match nmOpt, isOptArg, tryDestOptionTy denv.g pty with 
                // Layout an optional argument 
                | Some(nm), true, ptyOpt -> 
                    // detect parameter type, if ptyOpt is None - this is .NET style optional argument
                    let pty = defaultArg ptyOpt pty
                    nm.idText, (sprintf "?%s:" nm.idText),  pty
                // Layout an unnamed argument 
                | None, _,_ -> 
                    "", "", pty
                // Layout a named argument 
                | Some nm,_,_ -> 
                    let prefix = if isParamArrayArg then sprintf "params %s: " nm.idText else sprintf "%s: " nm.idText
                    nm.idText, prefix,pty)
            |> List.unzip3 
        let paramTypeAndRetLs,_ = NicePrint.layoutPrettifiedTypes denv (paramTypes@[rty])
        let paramTypeLs,_ = List.frontAndBack  paramTypeAndRetLs
        (paramNames,paramPrefixes,(paramTypes,paramTypeLs)||>List.zip) |||> List.map3 (fun nm paramPrefix (tau,tyL) -> 
            MethodGroupItemParameter(
              name = nm,
              canonicalTypeTextForSorting = printCanonicalizedTypeName g denv tau,
              display = paramPrefix+(showL tyL),
              description = ""
            ))

    let ParamsOfTypes g denv args rtau = 
        let ptausL, _ = NicePrint.layoutPrettifiedTypes denv (args@[rtau]) 
        let argsL,_ = List.frontAndBack ptausL 
        let mkParam (tau,tyL) =
            MethodGroupItemParameter(
              name = "",
              canonicalTypeTextForSorting = printCanonicalizedTypeName g denv tau,
              display =  Layout.showL tyL,
              description = "" 
            )
        (args,argsL) ||> List.zip |> List.map mkParam

#if EXTENSIONTYPING
    let (|ItemIsTypeWithStaticArguments|_|) g item =
        match item with
        | Item.Types(_name,tys) ->
            match tys with
            | [Microsoft.FSharp.Compiler.Tastops.AppTy g (tyconRef,_typeInst)] ->
                if tyconRef.IsProvidedErasedTycon || tyconRef.IsProvidedGeneratedTycon then
                    Some tyconRef
                else
                    None
            | _ -> None
        | _ -> None
#endif

    let rec ParamsOfItem (infoReader:InfoReader) m denv d = 
        let amap = infoReader.amap
        let g = infoReader.g
        match d with
        | Item.Value vref -> 
            let getParamsOfTypes() = 
                let _, tau = vref.TypeScheme
                if isFunTy denv.g tau then 
                    let arg,rtau = destFunTy denv.g tau 
                    let args = tryDestTupleTy denv.g arg 
                    ParamsOfTypes g denv args rtau
                else []
            match vref.ValReprInfo with
            | None -> 
                // ValReprInfo = None i.e. in let bindings defined in types or in local functions
                // in this case use old approach and return only information about types
                getParamsOfTypes ()
            | Some valRefInfo ->
                // ValReprInfo will exist for top-level syntactic functions
                // per spec: binding is considered to define a syntactic function if it is either a function or its immediate right-hand-side is a anonymous function
                let (_, argInfos,  returnTy, _) = GetTopValTypeInFSharpForm  g valRefInfo vref.Type m
                match argInfos with
                | [] -> 
                    // handles cases like 'let foo = List.map'
                    getParamsOfTypes() 
                | argInfo::_ ->
                    // result 'paramDatas' collection corresponds to the first argument of curried function
                    // i.e. let func (a : int) (b : int) = a + b
                    // paramDatas will contain information about a and returnTy will be: int -> int
                    // This is good enough as we don't provide ways to display info for the second curried argument
                    let paramDatas = 
                        argInfo
                        |> List.map ParamNameAndType.FromArgInfo
                        |> List.map (fun (ParamNameAndType(nm,pty)) -> ParamData(false, false, NotOptional, nm, pty))
                    ParamsOfParamDatas g denv paramDatas returnTy
        | Item.UnionCase(ucr)   -> 
            match ucr.UnionCase.RecdFields with
            | [f] -> [ParamOfUnionCaseField g denv NicePrint.isGeneratedUnionCaseField -1 f]
            | fs -> fs |> List.mapi (ParamOfUnionCaseField g denv NicePrint.isGeneratedUnionCaseField)
        | Item.ActivePatternCase(apref)   -> 
            let v = apref.ActivePatternVal 
            let _,tau = v.TypeScheme
            let args, _ = stripFunTy denv.g tau 
            ParamsOfTypes g denv args tau
        | Item.ExnCase(ecref)     -> 
            ecref |> recdFieldsOfExnDefRef |> List.mapi (ParamOfUnionCaseField g denv NicePrint.isGeneratedExceptionField) 
        | Item.Property(_,pinfo :: _) -> 
            let paramDatas = pinfo.GetParamDatas(amap,m)
            let rty = pinfo.GetPropertyType(amap,m) 
            ParamsOfParamDatas g denv paramDatas rty
        | Item.CtorGroup(_,(minfo :: _)) 
        | Item.MethodGroup(_,(minfo :: _)) -> 
            let paramDatas = minfo.GetParamDatas(amap, m, minfo.FormalMethodInst) |> List.head
            let rty = minfo.GetFSharpReturnTy(amap, m, minfo.FormalMethodInst)
            ParamsOfParamDatas g denv paramDatas rty
        | Item.CustomBuilder (_,vref) -> ParamsOfItem infoReader m denv (Item.Value vref)
        | Item.TypeVar _ -> []

        | Item.CustomOperation (_,usageText, Some minfo) -> 
            match usageText() with 
            | None -> 
                let argNamesAndTys = ItemDescriptionsImpl.ParamNameAndTypesOfUnaryCustomOperation g minfo 
                let _, argTys, _ = PrettyTypes.PrettifyTypesN g (argNamesAndTys |> List.map (fun (ParamNameAndType(_,ty)) -> ty))
                let paramDatas = (argNamesAndTys, argTys) ||> List.map2 (fun (ParamNameAndType(nm,_)) argTy -> ParamData(false,false,NotOptional,nm,argTy))
                let rty = minfo.GetFSharpReturnTy(amap, m, minfo.FormalMethodInst)
                ParamsOfParamDatas g denv paramDatas rty
            | Some _ -> 
                [] // no parameter data available for binary operators like 'zip', 'join' and 'groupJoin' since they use bespoke syntax 

        | Item.FakeInterfaceCtor _ -> []
        | Item.DelegateCtor delty -> 
            let (SigOfFunctionForDelegate(_, _, _, fty)) = GetSigOfFunctionForDelegate infoReader delty m AccessibleFromSomeFSharpCode
            ParamsOfParamDatas g denv [ParamData(false,false,NotOptional,None, fty)] delty
#if EXTENSIONTYPING
        | ItemIsTypeWithStaticArguments g tyconRef ->
            // similar code to TcProvidedTypeAppToStaticConstantArgs 
            let typeBeforeArguments = 
                match tyconRef.TypeReprInfo with 
                | TProvidedTypeExtensionPoint info -> info.ProvidedType
                | _ -> failwith "unreachable"
            let staticParameters = typeBeforeArguments.PApplyWithProvider((fun (typeBeforeArguments,provider) -> typeBeforeArguments.GetStaticParameters(provider)), range=m) 
            let staticParameters = staticParameters.PApplyArray(id, "GetStaticParameters",m)
            staticParameters 
                |> Array.map (fun sp -> 
                    let typ = Import.ImportProvidedType amap m (sp.PApply((fun x -> x.ParameterType),m))
                    let spKind = NicePrint.stringOfTy denv typ
                    let spName = sp.PUntaint((fun sp -> sp.Name), m)
                    let spOpt = sp.PUntaint((fun sp -> sp.IsOptional), m)
                    MethodGroupItemParameter(
                      name = spName,
                      canonicalTypeTextForSorting = spKind,
                      display = sprintf "%s%s: %s" (if spOpt then "?" else "") spName spKind,
                      description = ""))
                |> Array.toList 
#endif
        |  _ -> []


/// A single method for Intellisense completion
[<Sealed; NoEquality; NoComparison>]
// Note: instances of this type do not hold any references to any compiler resources.
type MethodGroupItem(description: ToolTipText, typeText: string, parameters: MethodGroupItemParameter[], isStaticArguments: bool) = 
    member __.Description = description
    member __.TypeText = typeText
    [<Obsolete("This member has been renamed to 'TypeText'")>]
    member __.Type = typeText
    member __.Parameters = parameters
    // is this not really a method, but actually a static arguments list, like TP<42,"foo"> ?
    member __.IsStaticArguments = isStaticArguments


/// A table of methods for Intellisense completion
//
// Note: this type does not hold any strong references to any compiler resources, nor does evaluating any of the properties execute any
// code on the compiler thread.  
[<Sealed>]
type MethodGroup( name: string, unsortedMethods: MethodGroupItem[] ) = 
    // BUG 413009 : [ParameterInfo] takes about 3 seconds to move from one overload parameter to another
    // cache allows to avoid recomputing parameterinfo for the same item
#if FX_ATLEAST_40
    static let methodOverloadsCache = System.Runtime.CompilerServices.ConditionalWeakTable()
#endif

    let methods = 
        unsortedMethods 
        // Methods with zero arguments show up here as taking a single argument of type 'unit'.  Patch them now to appear as having zero arguments.
        |> Array.map (fun meth -> 
            let parms = meth.Parameters
            if parms.Length = 1 && parms.[0].CanonicalTypeTextForSorting="Microsoft.FSharp.Core.Unit" then 
                MethodGroupItem(meth.Description,meth.TypeText,[||],meth.IsStaticArguments) 
            else 
                meth)
        // Fix the order of methods, to be stable for unit testing.
        |> Array.sortBy (fun meth -> 
            let parms = meth.Parameters
            parms.Length, (parms |> Array.map (fun p -> p.CanonicalTypeTextForSorting)))
    [<Obsolete("This member has been renamed to 'MethodName'")>]
    member x.Name = name
    member x.MethodName = name
    member x.Methods = methods

    static member Create(infoReader:InfoReader,m,denv,items:Item list) = 
        let g = infoReader.g
        if isNil items then new MethodGroup("", [| |]) else
        let name = items.Head.DisplayName 
        let getOverloadsForItem item =
#if FX_ATLEAST_40
            match methodOverloadsCache.TryGetValue item with
            | true, overloads -> overloads
            | false, _ ->
#endif
                let items =
                    match item with 
                    | Item.MethodGroup(nm,minfos) -> List.map (fun minfo -> Item.MethodGroup(nm,[minfo])) minfos 
                    | Item.CtorGroup(nm,cinfos) -> List.map (fun minfo -> Item.CtorGroup(nm,[minfo])) cinfos 
                    | Item.FakeInterfaceCtor _
                    | Item.DelegateCtor _ -> [item]
                    | Item.NewDef _ 
                    | Item.ILField _ -> []
                    | Item.Event _ -> []
                    | Item.RecdField(rfinfo) -> 
                        if isFunction g rfinfo.FieldType then [item] else []
                    | Item.Value v -> 
                        if isFunction g v.Type then [item] else []
                    | Item.UnionCase(ucr) -> 
                        if not ucr.UnionCase.IsNullary then [item] else []
                    | Item.ExnCase(ecr) -> 
                        if recdFieldsOfExnDefRef ecr |> nonNil then [item] else []
                    | Item.Property(_,pinfos) -> 
                        let pinfo = List.head pinfos 
                        if pinfo.IsIndexer then [item] else []
#if EXTENSIONTYPING
                    | Params.ItemIsTypeWithStaticArguments g _ -> [item] // we pretend that provided-types-with-static-args are method-like in order to get ParamInfo for them
#endif
                    | Item.CustomOperation(_name, _helpText, _minfo) -> [item]
                    | Item.TypeVar _ -> []
                    | Item.CustomBuilder _ -> []
                    | _ -> []

                let methods = 
                    items |> Array.ofList |> Array.map (fun item -> 
                        MethodGroupItem(
                          description=ToolTipText [FormatDescriptionOfItem true infoReader m denv item],
                          typeText= (FormatReturnTypeOfItem infoReader m denv item),
                          parameters = Array.ofList (Params.ParamsOfItem infoReader m denv item),
                          isStaticArguments = (match item with | Item.Types _ -> true | _ -> false)
                        ))
#if FX_ATLEAST_40
                methodOverloadsCache.Add(item, methods)
#endif
                methods
        let methods = [| for item in items do yield! getOverloadsForItem item |]

        new MethodGroup(name, methods)

//----------------------------------------------------------------------------
// Scopes. 
//--------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type (*internal*) FindDeclFailureReason = 
    // generic reason: no particular information about error
    | Unknown
    // source code file is not available
    | NoSourceCode
    // trying to find declaration of ProvidedType without TypeProviderDefinitionLocationAttribute
    | ProvidedType of string
    // trying to find declaration of ProvidedMember without TypeProviderDefinitionLocationAttribute
    | ProvidedMember of string

type FindDeclResult = 
    /// declaration not found + reason
    | DeclNotFound of FindDeclFailureReason
    /// found declaration
    | DeclFound of range


/// This type is used to describe what was found during the name resolution.
/// (Depending on the kind of the items, we may stop processing or continue to find better items)
[<RequireQualifiedAccess>]
[<NoEquality; NoComparison>]
type internal NameResResult = 
    | Members of (Item list * DisplayEnv * range)
    | Cancel of DisplayEnv * range
    | Empty
    | TypecheckStaleAndTextChanged
    

[<RequireQualifiedAccess>]
type ResolveOverloads = 
|   Yes
|   No

[<RequireQualifiedAccess>]
type GetPreciseCompletionListFromExprTypingsResult =
    | NoneBecauseTypecheckIsStaleAndTextChanged
    | NoneBecauseThereWereTypeErrors
    | None
    | Some of (Item list * DisplayEnv * range)

type Names = string list 


// A scope represents everything we get back from the typecheck of a file.
// It acts like an in-memory database about the file.
// It is effectively immutable and not updated: when we re-typecheck we just drop the previous
// scope object on the floor and make a new one.
[<Sealed>]
type TypeCheckInfo
          (// Information corresponding to miscellaneous command-line options (--define, etc).
           _sTcConfig: Build.TcConfig,
           g: Env.TcGlobals,
           // The signature of the assembly being checked, up to and including the current file
           ccuSig: ModuleOrNamespaceType,
           thisCcu: CcuThunk,
           tcImports: TcImports,
           projectFileName: string ,
           mainInputFileName: string ,
           sResolutions: TcResolutions,
           // This is a name resolution environment to use if no better match can be found.
           sFallback:Nameres.NameResolutionEnv,
           loadClosure : LoadClosure option,
           syncop:(unit->unit)->unit,
           checkAlive : (unit -> bool),
           textSnapshotInfo:obj option) = 

    let (|CNR|) (cnr:CapturedNameResolution) =
        (cnr.Pos, cnr.Item, cnr.ItemOccurence, cnr.DisplayEnv, cnr.NameResolutionEnv, cnr.AccessorDomain, cnr.Range)

    // These strings are potentially large and the editor may choose to hold them for a while.
    // Use this cache to fold together data tip text results that are the same. 
    // Is not keyed on 'Names' collection because this is invariant for the current position in 
    // this unchanged file. Keyed on lineStr though to prevent a change to the currently line
    // being available against a stale scope.
    let getToolTipTextCache = AgedLookup<int*int*string,ToolTipText>(recentForgroundTypeCheckLookupSize,areSame=(fun (x,y) -> x = y))
    
    let amap = tcImports.GetImportMap()
    let infoReader = new InfoReader(g,amap)
    let ncenv = new NameResolver(g,amap,infoReader,Nameres.FakeInstantiationGenerator)
    
    /// Find the most precise naming environment for the given line and column
    let GetBestEnvForPos cursorPos  =
        
        let bestSoFar = ref None

        // Find the most deeply nested enclosing scope that contains given position
        sResolutions.CapturedEnvs |> ResizeArray.iter (fun (possm,env,ad) -> 
            if rangeContainsPos possm cursorPos then
                match !bestSoFar with 
                | Some (bestm,_,_) -> 
                    if rangeContainsRange bestm possm then 
                      bestSoFar := Some (possm,env,ad)
                | None -> 
                    bestSoFar := Some (possm,env,ad))

        let mostDeeplyNestedEnclosingScope = !bestSoFar 
        
        match mostDeeplyNestedEnclosingScope with 
        | Some (m,_,_) -> Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "Strict Inclusion found env for range %s\n" (stringOfRange m))
        | None ->Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "Strict Inclusion found no environment, cursorPos = %s\n" (stringOfPos cursorPos))
        
        // Look for better subtrees on the r.h.s. of the subtree to the left of where we are 
        // Should really go all the way down the r.h.s. of the subtree to the left of where we are 
        // This is all needed when the index is floating free in the area just after the environment we really want to capture 
        // We guarantee to only refine to a more nested environment.  It may not be strictly  
        // the right environment, but will alwauys be at least as rich 

        let bestAlmostIncludedSoFar = ref None 

        sResolutions.CapturedEnvs |> ResizeArray.iter (fun (possm,env,ad) -> 
            // take only ranges that strictly do not include cursorPos (all ranges that touch cursorPos were processed during 'Strict Inclusion' part)
            if rangeBeforePos possm cursorPos && not (posEq possm.End cursorPos) then 
                let contained = 
                    match mostDeeplyNestedEnclosingScope with 
                    | Some (bestm,_,_) -> rangeContainsRange bestm possm 
                    | None -> true 
                
                if contained then 
                    match  !bestAlmostIncludedSoFar with 
                    | Some (rightm:range,_,_) -> 
                        if posGt possm.End rightm.End || 
                          (posEq possm.End rightm.End && posGt possm.Start rightm.Start) then
                            bestAlmostIncludedSoFar := Some (possm,env,ad)
                    | _ -> bestAlmostIncludedSoFar := Some (possm,env,ad))
        
        let resEnv = 
            match !bestAlmostIncludedSoFar with 
            | Some (m,env,ad) -> 
                Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "Chose refined-rightmost env covering range %s\n" (stringOfRange m))
                env,ad
            | None -> 
                match mostDeeplyNestedEnclosingScope with 
                | Some (m,env,ad) -> 
                    Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "Chose refined env covering range %s\n" (stringOfRange m))
                    env,ad
                | None -> 
                    Trace.PrintLine("CompilerServicesVerbose", fun () -> "Using fallback global env\n")
                    (sFallback,AccessibleFromSomeFSharpCode)
        let pm = mkRange mainInputFileName cursorPos cursorPos 

        resEnv,pm

    /// The items that come back from ResolveCompletionsInType are a bit
    /// noisy. Filter a few things out.
    ///
    /// e.g. prefer types to constructors for ToolTipText 
    let FilterItemsForCtors filterCtors items = 
        let items = items |> List.filter (function (Item.CtorGroup _) when filterCtors = ResolveTypeNamesToTypeRefs -> false | _ -> true) 
        items
        
    /// Looks at the exact name resolutions that occurred during type checking
    /// If 'membersByResidue' is specified, we look for members of the item obtained 
    /// from the name resolution and filter them by the specified residue (?)
    let GetPreciseItemsFromNameResolution(line,colAtEndOfNames,membersByResidue,filterCtors, resolveOverloads, hasTextChangedSinceLastTypecheck) = 
        let endOfNamesPos = mkPos line colAtEndOfNames

        let quals = 
            (match resolveOverloads with ResolveOverloads.Yes -> sResolutions.CapturedNameResolutions | ResolveOverloads.No -> sResolutions.CapturedMethodGroupResolutions)
            |> ResizeArray.filter (fun cnr -> 
                Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "Checking position %s = %s\n" (stringOfPos endOfNamesPos) (stringOfPos cnr.Pos))
                posEq cnr.Pos endOfNamesPos)
        Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "GetPreciseItemsFromNameResolution: Found %d relevant quals\n" quals.Count)
        
        let items = quals |> ResizeArray.toList |> List.rev  // Logic below expects the list to be in reverse order of resolution
        
        // Filter items to show only valid & return Some if there are any
        let returnItemsOfType items g denv (m:range) f =
            let items = items |> RemoveDuplicateItems g
            let items = items |> RemoveExplicitlySuppressed g
            let items = items |> FilterItemsForCtors filterCtors
            if nonNil items then
                Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "GetPreciseItemsFromNameResolution: Results in %d items!\n" items.Length)
                if hasTextChangedSinceLastTypecheck(textSnapshotInfo, m) then
                    NameResResult.TypecheckStaleAndTextChanged // typecheck is stale, wait for second-chance IntelliSense to bring up right result
                else
                    f(items, denv, m) 
            else NameResResult.Empty        

        match items, membersByResidue with 
        
        // If we're looking for members using a residue, we'd expect only
        // a single item (pick the first one) and we need the residue (which may be "")
        | CNR(_,Item.Types(_,(typ::_)),_,denv,nenv,ad,m)::_, Some _ -> 
            let items = ResolveCompletionsInType ncenv nenv (ConstraintSolver.IsApplicableMethApprox g amap m) m ad true typ 
            returnItemsOfType items g denv m NameResResult.Members
        
        // Value reference from the name resolution. Primarilly to disallow "let x.$ = 1"
        // In most of the cases, value references can be obtained from expression typings or from environment,
        // so we wouldn't have to handle values here. However, if we have something like:
        //   let varA = "string"
        //   let varA = if b then 0 else varA.
        // then the expression typings get confused (thinking 'varA:int'), so we use name resolution even for usual values.
        
        | CNR(_, Item.Value(vref), occurence, denv, nenv, ad, m)::_, Some _ ->
            if (occurence = ItemOccurence.Binding || occurence = ItemOccurence.Pattern) then 
              // Return empty list to stop further lookup - for value declarations
              NameResResult.Cancel(denv, m)
            else 
              // If we have any valid items for the value, then return completions for its type now.
              // Adjust the type in case this is the 'this' pointer stored in a reference cell.
              let ty = StripSelfRefCell(g, vref.BaseOrThisInfo, vref.TauType) 
              // patch accessibility domain to remove protected members if accessing NormalVal
              let ad = 
                match vref.BaseOrThisInfo, ad with
                | ValBaseOrThisInfo.NormalVal, AccessibleFrom(paths, Some tcref) ->
                    let tcref = generalizedTyconRef tcref
                    // check that type of value is the same or subtype of tcref
                    // yes - allow access to protected members
                    // no - strip ability to access protected members
                    if Microsoft.FSharp.Compiler.Typrelns.TypeFeasiblySubsumesType 0 g amap m tcref Microsoft.FSharp.Compiler.Typrelns.CanCoerce ty then
                        ad
                    else
                        AccessibleFrom(paths, None)
                | _ -> ad

              let items = ResolveCompletionsInType ncenv nenv (ConstraintSolver.IsApplicableMethApprox g amap m) m ad false ty
              returnItemsOfType items g denv m NameResResult.Members
        
        // No residue, so the items are the full resolution of the name
        | CNR(_,_,_,denv,_,_,m) :: _, None -> 
            Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "GetPreciseItemsFromNameResolution: No residue, precise results found\n")
            let items = items |> List.map (fun (CNR(_,item,_,_,_,_,_)) -> item) 
                              // "into" is special magic syntax, not an identifier or a library call.  It is part of capturedNameResolutions as an 
                              // implementation detail of syntax coloring, but we should not report name resolution results for it, to prevent spurious QuickInfo.
                              |> List.filter (function Item.CustomOperation(CustomOperations.Into,_,_) -> false | _ -> true) 
            returnItemsOfType items g denv m NameResResult.Members
        | _ , _ -> NameResResult.Empty

    /// finds captured typing for the given position
    let GetExprTypingForPosition(endOfExprPos) = 
        let quals = 
            sResolutions.CapturedExpressionTypings 
            |> Seq.filter (fun (pos,typ,denv,_,_,_) -> 
                    // We only want expression types that end at the particular position in the file we are looking at.
                    let isLocationWeCareAbout = posEq pos endOfExprPos
                    // Get rid of function types.  True, given a 2-arg curried function "f x y", it is legal to do "(f x).GetType()",
                    // but you almost never want to do this in practice, and we choose not to offer up any intellisense for 
                    // F# function types.
                    let isFunction = isFunTy denv.g typ
                    Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "GetPreciseCompletionListFromExprTypings: isFunction=%A, isLocationWeCareAbout=%A\n" isFunction isLocationWeCareAbout)
                    isLocationWeCareAbout && not isFunction)
            |> Seq.toArray

        let thereWereSomeQuals = not (Array.isEmpty quals)
        // filter out errors

        let quals = quals 
                    |> Array.filter (fun (_,typ,denv,_,_,_) -> not (isTyparTy denv.g typ && (destTyparTy denv.g typ).IsFromError))
        thereWereSomeQuals, quals
    
    /// obtains captured typing for the given position
    /// if type of captured typing is record - returns list of record fields
    let GetRecdFieldsForExpr(r : range) = 
        let _, quals = GetExprTypingForPosition(r.End)
        let bestQual = 
            match quals with
            | [||] -> None
            | quals ->  
                quals |> Array.tryFind (fun (_,_,_,_,_,rq) -> 
                                            ignore(r)  // for breakpoint
                                            posEq r.Start rq.Start)
        match bestQual with
        | Some (_,typ,denv,_nenv,ad,m) when isRecdTy denv.g typ ->
            let items = Nameres.ResolveRecordOrClassFieldsOfType ncenv m ad typ false
            Some (items, denv, m)
        | _ -> None


    /// Looks at the exact expression types at the position to the left of the 
    /// residue then the source when it was typechecked.
    let GetPreciseCompletionListFromExprTypings(parseResults:ParseFileResults, endOfExprPos, filterCtors, hasTextChangedSinceLastTypecheck: (obj * range -> bool)) = 
        
        let thereWereSomeQuals, quals = GetExprTypingForPosition(endOfExprPos)

        match quals with
        | [| |] -> 
            if thereWereSomeQuals then
                GetPreciseCompletionListFromExprTypingsResult.NoneBecauseThereWereTypeErrors 
            else
                GetPreciseCompletionListFromExprTypingsResult.None
        | _ ->
            let bestQual, textChanged = 
                match parseResults.ParseTree with
                | Some(input) -> 
                    match UntypedParseImpl.GetRangeOfExprLeftOfDot(endOfExprPos,Some(input)) with   // TODO we say "colAtEndOfNames" everywhere, but that's not really a good name ("foo  .  $" hit Ctrl-Space at $)
                    | Some( exprRange) ->
                        if hasTextChangedSinceLastTypecheck(textSnapshotInfo, exprRange) then
                            None, true // typecheck is stale, wait for second-chance IntelliSense to bring up right result
                        else
                            // See bug 130733.  We have an up-to-date sync parse, and know the exact range of the prior expression.
                            // The quals all already have the same ending position, so find one with a matching starting position, if it exists.
                            // If not, then the stale typecheck info does not have a capturedExpressionTyping for this exact expression, and the
                            // user can wait for typechecking to catch up and second-chance intellisense to give the right result.
                            let qual = 
                                quals |> Array.tryFind (fun (_,_,_,_,_,r) -> 
                                                            ignore(r)  // for breakpoint
                                                            posEq exprRange.Start r.Start)
                            qual, false
                    | None -> 
                        // TODO In theory I think we should never get to this code path; it would be nice to add an assert.
                        // In practice, we do get here in some weird cases like "2.0 .. 3.0" and hitting Ctrl-Space in between the two dots of the range operator.
                        // I wasn't able to track down what was happening in those werid cases, not worth worrying about, it doesn't manifest as a product bug or anything.
                        None, false
                | _ -> None, false

            match bestQual with
            | Some bestQual ->
                let (_,typ,denv,nenv,ad,m) = bestQual 
                let items = ResolveCompletionsInType ncenv nenv (ConstraintSolver.IsApplicableMethApprox g amap m) m ad false typ 
                Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "GetPreciseCompletionListFromExprTypings: Results in %d items!\n" items.Length)
                let items = items |> RemoveDuplicateItems g
                let items = items |> RemoveExplicitlySuppressed g
                let items = items |> FilterItemsForCtors filterCtors 
                GetPreciseCompletionListFromExprTypingsResult.Some(items,denv,m)
            | None -> 
                if textChanged then GetPreciseCompletionListFromExprTypingsResult.NoneBecauseTypecheckIsStaleAndTextChanged
                else GetPreciseCompletionListFromExprTypingsResult.None

    /// Find items in the best naming environment.
    let GetEnvironmentLookupResolutions(cursorPos,plid,filterCtors,showObsolete) = 
        let (nenv,ad),m = GetBestEnvForPos cursorPos
        let items = Nameres.ResolvePartialLongIdent ncenv nenv (ConstraintSolver.IsApplicableMethApprox g amap m) m ad plid showObsolete
        let items = items |> RemoveDuplicateItems g 
        let items = items |> RemoveExplicitlySuppressed g
        let items = items |> FilterItemsForCtors filterCtors 
         
        items, nenv.DisplayEnv, m 

    /// Find record fields in the best naming environment.
    let GetClassOrRecordFieldsEnvironmentLookupResolutions(cursorPos, plid, (_residue : string option)) = 
        let (nenv, ad),m = GetBestEnvForPos cursorPos
        let items = Nameres.ResolvePartialLongIdentToClassOrRecdFields ncenv nenv m ad plid false
        let items = items |> RemoveDuplicateItems g 
        let items = items |> RemoveExplicitlySuppressed g
        items, nenv.DisplayEnv,m 

    /// Resolve a location and/or text to items.
    //   Three techniques are used
    //        - look for an exact known name resolution from type checking
    //        - use the known type of an expression, e.g. (expr).Name, to generate an item list  
    //        - lookup an entire name in the name resolution environment, e.g. A.B.Name, to generate an item list
    //
    // The overall aim is to resolve as accurately as possible based on what we know from type inference
    
    let GetDeclItemsForNamesAtPosition(parseResultsOpt : ParseFileResults option,
                                       origLongIdentOpt: string list option, residueOpt:string option, line:int, lineStr:string, colAtEndOfNamesAndResidue, filterCtors, resolveOverloads, hasTextChangedSinceLastTypecheck: (obj * range -> bool)) = 

        let GetBaseClassCandidates (denv : DisplayEnv) = function
            | Item.ModuleOrNamespaces _ -> true
            | Item.Types(_, ty::_) when (isClassTy denv.g ty) && not (isSealedTy denv.g ty) -> true
            | _ -> false   

        let GetInterfaceCandidates (denv : DisplayEnv) = function
            | Item.ModuleOrNamespaces _ -> true
            | Item.Types(_, ty::_) when (isInterfaceTy denv.g ty) -> true
            | _ -> false   
        /// Post-filter items to make sure they have precisely the right name
        /// This also checks that there are some remaining results 
        /// exactMatchResidue = Some _ -- means that we are looking for exact matches
        let FilterRelevantItemsBy (exactMatchResidue : _ option) f (items, denv, m) =
            
            // can throw if type is in located in non-resolved CCU: i.e. bigint if reference to System.Numerics is absent
            let f denv item = try f denv item with _ -> false
                                                
            // Return only items with the specified name
            let filterDeclItemsByResidue residue (items: Item list) = 
                items |> List.filter (fun item -> 
                    let n1 =  item.DisplayName 
                    Trace.PrintLine("CompilerServicesVerbose", fun () -> sprintf "\nn1 = <<<%s>>>\nn2 = <<<%s>>>\n" n1 residue)
                    if not (f denv item) then false
                    else
                        match item with
                        | Item.Types _ | Item.CtorGroup _ -> residue + "Attribute" = n1 || residue = n1
                        | _ -> residue = n1 )
            
            // Are we looking for items with precisely the given name?
            if nonNil items && exactMatchResidue.IsSome then
                Trace.PrintLine("CompilerServices", fun _ -> sprintf "looking through %d items before filtering by residue\n" (List.length items))       
                let items = items |> filterDeclItemsByResidue exactMatchResidue.Value
                if nonNil items then Some(items,denv,m) else None        
            else 
                // When (items = []) we must returns Some([],..) and not None
                // because this value is used if we want to stop further processing (e.g. let x.$ = ...)
                let items = List.filter (f denv) items
                Some(items, denv, m) 

        let loc = 
            match colAtEndOfNamesAndResidue with
            | pastEndOfLine when pastEndOfLine >= lineStr.Length -> lineStr.Length
            | atDot when lineStr.[atDot] = '.' -> atDot + 1
            | atStart when atStart = 0 -> 0
            | otherwise -> otherwise - 1

        let FindInEnv(plid, showObsolete) = GetEnvironmentLookupResolutions(mkPos line loc,plid,filterCtors, showObsolete) 

        let FindRecordFieldsInEnv(plid, residue) = GetClassOrRecordFieldsEnvironmentLookupResolutions(mkPos line loc, plid, residue)

        match UntypedParseImpl.TryGetCompletionContext(mkPos line colAtEndOfNamesAndResidue, parseResultsOpt) with
        | Some Invalid -> None
        | Some (CompletionContext.Inherit(InheritanceContext.Class, (plid, _))) ->
            FindInEnv(plid, false) 
            |> FilterRelevantItemsBy None GetBaseClassCandidates
        | Some (CompletionContext.Inherit(InheritanceContext.Interface, (plid, _))) ->
            FindInEnv(plid, false) 
            |> FilterRelevantItemsBy None GetInterfaceCandidates
        | Some (CompletionContext.Inherit(InheritanceContext.Unknown, (plid, _))) ->
            FindInEnv(plid, false) 
            |> FilterRelevantItemsBy None (fun denv t -> (GetBaseClassCandidates denv t) || (GetInterfaceCandidates denv t))
        | Some(CompletionContext.RecordField(RecordContext.New(plid, residue))) ->
            FindRecordFieldsInEnv(plid, residue)
            |> Some            
        | Some(CompletionContext.RecordField(RecordContext.CopyOnUpdate(r, (plid, residue)))) -> 
            match GetRecdFieldsForExpr(r) with
            | None -> 
                FindRecordFieldsInEnv(plid, residue)
                |> Some
            | x -> x
        | Some(CompletionContext.RecordField(RecordContext.Constructor(typeName))) ->
            FindRecordFieldsInEnv([typeName], None)
            |> Some
        | cc ->

        let findFirstNonWsPos i = 
            if i >= lineStr.Length then None
            else
            let mutable p = i
            while p >= 0 && System.Char.IsWhiteSpace(lineStr.[p]) do
                p <- p - 1
            if p >= 0 then Some p else None
        
        // are last two chars (except whitespaces) = ".."
        let isLikeRangeOp = 
            match findFirstNonWsPos (colAtEndOfNamesAndResidue - 1) with
            | Some x when x >= 1 && lineStr.[x] = '.' && lineStr.[x - 1] = '.' -> true
            | _ -> false

        // if last two chars are .. and we are not in range operator context - no completion
        if isLikeRangeOp && not (cc = Some (CompletionContext.RangeOperator)) then None
        else
                                    
        // Try to use the exact results of name resolution during type checking to generate the results
        // This is based on position (i.e. colAtEndOfNamesAndResidue). 
        let nameResItems = 
            match residueOpt with 
            | None -> GetPreciseItemsFromNameResolution(line, colAtEndOfNamesAndResidue, None, filterCtors,resolveOverloads, hasTextChangedSinceLastTypecheck)
            | Some residue ->
                // deals with cases when we have spaces between dot and\or identifier, like A  . $
                // if this is our case - then wen need to locate end position of the name skipping whitespaces
                // this allows us to handle cases like: let x . $ = 1 

                // colAtEndOfNamesAndResidue is 1-based so at first we need to convert it to 0-based 
                //
                // TODO: this code would be a lot simpler if we just passed in colAtEndOfNames in 
                // the first place. colAtEndOfNamesAndResidue serves no purpose. The cracking below is
                // inaccurate and incomplete in any case since it only works on a single line.
                match findFirstNonWsPos (colAtEndOfNamesAndResidue - 1) with
                | Some p when lineStr.[p] = '.' ->
                    match findFirstNonWsPos (p - 1) with
                    | Some colAtEndOfNames ->                 
                        let colAtEndOfNames = colAtEndOfNames + 1 // convert 0-based to 1-based
                        GetPreciseItemsFromNameResolution(line, colAtEndOfNames, Some(residue), filterCtors,resolveOverloads, hasTextChangedSinceLastTypecheck)
                    | None -> NameResResult.Empty
                | _ -> NameResResult.Empty        
        
        // Normalize to form A.B.C.D where D is the residue. It may be empty for "A.B.C."
        // residueOpt = Some when we are looking for the exact match
        let plid, exactMatchResidueOpt = 
            match origLongIdentOpt, residueOpt with
            | None, _ -> [], None
            | Some(origLongIdent), Some _ -> origLongIdent, None
            | Some(origLongIdent), None ->
                assert (nonNil origLongIdent)
                // note: as above, this happens when we are called for "precise" resolution - (F1 keyword, data tip etc..)
                let plid, residue = List.frontAndBack origLongIdent
                plid, Some residue
                
        /// Post-filter items to make sure they have precisely the right name
        /// This also checks that there are some remaining results 
        let (|FilterRelevantItems|_|) orig =
            FilterRelevantItemsBy exactMatchResidueOpt (fun _ _ -> true) orig

        match nameResItems with            
        | NameResResult.TypecheckStaleAndTextChanged -> None // second-chance intellisense will try again
        | NameResResult.Cancel(denv,m) -> Some([], denv, m)
        | NameResResult.Members(FilterRelevantItems(items)) -> 
            Trace.PrintLine("CompilerServices", fun _ -> sprintf "GetDeclItemsForNamesAtPosition: lookup based on name resolution results successful, #items = %d, exists ctor = %b\n" (p13 items).Length (items |> p13 |> List.exists (function Item.CtorGroup _ -> true | _ -> false)))       
            Some items
        | _ ->
        
        match origLongIdentOpt with
        | None -> None
        | Some _ -> 
            Trace.PrintLine("CompilerServices", fun _ -> sprintf "GetDeclItemsForNamesAtPosition: plid = %+A, residue = %+A, colAtEndOfNamesAndResidue = %+A\n" plid exactMatchResidueOpt colAtEndOfNamesAndResidue)       

            // Try to use the type of the expression on the left to help generate a completion list
            let mutable thereIsADotInvolved = false
            let qualItems = 
                match parseResultsOpt with
                | None -> 
                    // Note, you will get here if the 'reason' is not CompleteWord/MemberSelect/DisplayMemberList, as those are currently the 
                    // only reasons we do a sync parse to have the most precise and likely-to-be-correct-and-up-to-date info.  So for example,
                    // if you do QuickInfo hovering over A in "f(x).A()", you will only get a tip if typechecking has a name-resolution recorded
                    // for A, not if merely we know the capturedExpressionTyping of f(x) and you very recently typed ".A()" - in that case, 
                    // you won't won't get a tip until the typechecking catches back up.
                    GetPreciseCompletionListFromExprTypingsResult.None
                | Some(upi) -> 

// See ServiceUntypedParse - GetRangeOfExprLeftOfDot and TryFindExpressionASTLeftOfDotLeftOfCursor are similar, but different, can we refactor commonality?
//                match UntypedParseImpl.GetRangeOfExprLeftOfDot(line,colAtEndOfNamesAndResidue,upi.ParseTree) with
//                | Some((_,_),(el,ec)) ->
//                    thereIsADotInvolved <- true
//                    GetPreciseCompletionListFromExprTypings(upi, el-1, ec, filterCtors)
                match UntypedParseImpl.TryFindExpressionASTLeftOfDotLeftOfCursor(mkPos line colAtEndOfNamesAndResidue,upi.ParseTree) with
                | Some(pos,_) ->
                    thereIsADotInvolved <- true
                    GetPreciseCompletionListFromExprTypings(upi, pos, filterCtors, hasTextChangedSinceLastTypecheck)
                | None -> 
                    // Can get here in a case like: if "f xxx yyy" is legal, and we do "f xxx y"
                    // We have no interest in expression typings, those are only useful for dot-completion.  We want to fallback
                    // to "Use an environment lookup as the last resort" below
                    GetPreciseCompletionListFromExprTypingsResult.None

            match qualItems,thereIsADotInvolved with            
            | GetPreciseCompletionListFromExprTypingsResult.Some(FilterRelevantItems(items)), _
                    // Initially we only use the expression typings when looking up, e.g. (expr).Nam or (expr).Name1.Nam
                    // These come through as an empty plid and residue "". Otherwise we try an environment lookup
                    // and then return to the qualItems. This is because the expression typings are a little inaccurate, primarily because
                    // it appears we're getting some typings recorded for non-atomic expressions like "f x"
                    when (match plid with [] -> true | _ -> false)  -> 
                Trace.PrintLine("CompilerServices", fun _ -> sprintf "GetDeclItemsForNamesAtPosition: lookup based on expression typings successful\n")       
                Some items
            | GetPreciseCompletionListFromExprTypingsResult.NoneBecauseThereWereTypeErrors, _ ->
                // There was an error, e.g. we have "<expr>." and there is an error determining the type of <expr>  
                // In this case, we don't want any of the fallback logic, rather, we want to produce zero results.
                None
            | GetPreciseCompletionListFromExprTypingsResult.NoneBecauseTypecheckIsStaleAndTextChanged, _ ->         
                // we want to report no result and let second-chance intellisense kick in
                None
            | _, true when (match plid with [] -> true | _ -> false)  -> 
                // If the user just pressed '.' after an _expression_ (not a plid), it is never right to show environment-lookup top-level completions.
                // The user might by typing quickly, and the LS didn't have an expression type right before the dot yet.
                // Second-chance intellisense will bring up the correct list in a moment.
                None
            | _ ->         
            // Use an environment lookup as the last resort

            let envItems =  FindInEnv(plid, residueOpt.IsSome)
            match nameResItems, envItems, qualItems with            
            
            // First, use unfiltered name resolution items, if they're not empty
            | NameResResult.Members(items, denv, m), _, _ when nonNil items -> 
                Trace.PrintLine("CompilerServices", fun _ -> sprintf "GetDeclItemsForNamesAtPosition: lookup based on name resolution results successful, #items = %d, exists ctor = %b\n" (items).Length (items |> List.exists (function Item.CtorGroup _ -> true | _ -> false)))       
                Some(items, denv, m)                
            
            // If we have nonempty items from environment that were resolved from a type, then use them... 
            // (that's better than the next case - here we'd return 'int' as a type)
            | _, FilterRelevantItems(items, denv, m), _ when nonNil items ->
                Trace.PrintLine("CompilerServices", fun _ -> sprintf "GetDeclItemsForNamesAtPosition: lookup based on name and environment successful\n")       
                Some(items, denv, m)

            // Try again with the qualItems
            | _, _, GetPreciseCompletionListFromExprTypingsResult.Some(FilterRelevantItems(items)) ->
                Some(items)
                
            | _ -> None


    // Return 'false' if this is not a completion item valid in an interface file.
    let IsValidSignatureFileItem item =
        match item with
        | Item.Types _ | Item.ModuleOrNamespaces _ -> true
        | _ -> false

    let filterIntellisenseCompletionsBasedOnParseContext parseResultsOpt (pos:pos) items = 
        match parseResultsOpt with
        | None -> items
        | Some t ->
            // visitor to see if we are in an "open" declaration in the parse tree
            let visitor = { new AstTraversal.AstVisitorBase<int>() with
                                override this.VisitExpr(_path, _traverseSynExpr, defaultTraverse, expr) = None  // don't need to keep going, 'open' declarations never appear inside Exprs
                                override this.VisitModuleDecl(defaultTraverse, decl) =
                                    match decl with
                                    | SynModuleDecl.Open(_longIdent, m) -> 
                                        // in theory, this means we're "in an open"
                                        // in practice, because the parse tree/walkers do not handle attributes well yet, need extra check below to ensure not e.g. $here$
                                        //     open System
                                        //     [<Attr$
                                        //     let f() = ()
                                        // inside an attribute on the next item
                                        let pos = mkPos pos.Line (pos.Column - 1) // -1 because for e.g. "open System." the dot does not show up in the parse tree
                                        if rangeContainsPos m pos then  
                                            Some 0
                                        else
                                            None
                                    | _ -> defaultTraverse decl }
            match AstTraversal.Traverse(pos, t, visitor) with
            | None -> items
            | Some _ ->
                items |> List.filter (function | Item.ModuleOrNamespaces _ -> true | _ -> false)

    member x.GetDeclarations (parseResultsOpt:ParseFileResults option, line, lineStr, colAtEndOfNamesAndResidue, qualifyingNames, partialName, hasTextChangedSinceLastTypecheck) : DeclarationSet =
        let isInterfaceFile = SourceFileImpl.IsInterfaceFile mainInputFileName
        ErrorScope.Protect 
            Range.range0 
            (fun () -> 
                match GetDeclItemsForNamesAtPosition(parseResultsOpt, Some qualifyingNames, Some partialName, line, lineStr, colAtEndOfNamesAndResidue, ResolveTypeNamesToCtors, ResolveOverloads.Yes, hasTextChangedSinceLastTypecheck) with
                | None -> DeclarationSet.Empty  
                | Some(items,denv,m) -> 
                    let items = items |> filterIntellisenseCompletionsBasedOnParseContext (parseResultsOpt |> Option.bind (fun x -> x.ParseTree)) (mkPos line colAtEndOfNamesAndResidue)
                    let items = if isInterfaceFile then items |> List.filter IsValidSignatureFileItem else items
                    DeclarationSet.Create(infoReader,m,denv,items,syncop,checkAlive))
            (fun msg -> DeclarationSet.Error msg)
            
    member scope.GetReferenceResolutionToolTipText(line,col) : ToolTipText = 
        let pos = mkPos line col
        let lineIfExists(append) =
            if not(String.IsNullOrEmpty(append)) then append.Trim([|' '|])+"\n"
            else ""     
        let isPosMatch(pos, ar:AssemblyReference) : bool = 
            let isRangeMatch = (Range.rangeContainsPos ar.Range pos) 
            let isNotSpecialRange = (ar.Range <> rangeStartup) && (ar.Range <> range0) && (ar.Range <> rangeCmdArgs)
            let isMatch = isRangeMatch && isNotSpecialRange
            isMatch      
        
        let dataTipOfReferences() = 
            let matches =
                match loadClosure with
                | None -> []
                | Some(loadClosure) -> 
                    loadClosure.References
                        |> List.map snd
                        |> List.concat 
                        |> List.filter(fun ar->isPosMatch(pos, ar.originalReference))

            match matches with 
            | resolved::_ // Take the first seen
            | [resolved] -> 
                let originalReferenceName = resolved.originalReference.Text
                let resolvedPath = // Don't show the resolved path if it is identical to what was referenced.
                    if originalReferenceName = resolved.resolvedPath then String.Empty
                    else resolved.resolvedPath
                let tip =                 
                    match resolved.resolvedFrom with 
                    | AssemblyFolders ->
                        lineIfExists(resolvedPath)
                        + lineIfExists(resolved.fusionName)
                        + (FSComp.SR.assemblyResolutionFoundByAssemblyFoldersKey())
                    | AssemblyFoldersEx -> 
                        lineIfExists(resolvedPath)
                        + lineIfExists(resolved.fusionName)
                        + (FSComp.SR.assemblyResolutionFoundByAssemblyFoldersExKey())
                    | TargetFrameworkDirectory -> 
                        lineIfExists(resolvedPath)
                        + lineIfExists(resolved.fusionName)
                        + (FSComp.SR.assemblyResolutionNetFramework())
                    | Unknown ->
                        // Unknown when resolved by plain directory search without help from MSBuild resolver.
                        lineIfExists(resolvedPath)
                        + lineIfExists(resolved.fusionName)
                    | RawFileName -> 
                        lineIfExists(resolved.fusionName)
                    | GlobalAssemblyCache -> 
                        lineIfExists(resolved.fusionName)
                        + (FSComp.SR.assemblyResolutionGAC())+ "\n"
                        + lineIfExists(resolved.redist)
                    | Path _ ->
                        lineIfExists(resolvedPath)
                        + lineIfExists(resolved.fusionName)  
                                                  
                ToolTipText [ToolTipElement(tip.TrimEnd([|'\n'|]) ,XmlCommentNone)]

            | [] -> ToolTipText []
                                    
        ErrorScope.Protect 
            Range.range0 
            dataTipOfReferences
            (fun err -> ToolTipText [ToolTipElementCompositionError err])

    // GetToolTipText: return the "pop up" (or "Quick Info") text given a certain context.
    member x.GetToolTipText line lineStr colAtEndOfNames names : ToolTipText  = 
        use t = Trace.Call("CompilerServices","GetToolTipText", fun _->sprintf " line=%+A,idx=%+A,names=%+A" line colAtEndOfNames names)
        
        let Compute() = 
            ErrorScope.Protect 
                Range.range0 
                (fun () -> 
                    match GetDeclItemsForNamesAtPosition(None,Some(names),None,line,lineStr,colAtEndOfNames,ResolveTypeNamesToCtors,ResolveOverloads.Yes,fun _ -> false) with
                    | None -> ToolTipText []
                    | Some(items,denv,m) ->
                         ToolTipText(items |> List.map (FormatDescriptionOfItem false infoReader m denv )))
                (fun err -> ToolTipText [ToolTipElementCompositionError err])
               
        // See devdiv bug 646520 for rationale behind truncating and caching these quick infos (they can be big!)
        let key = line,colAtEndOfNames,lineStr
        match getToolTipTextCache.TryGet key with 
        | Some res -> res
        | None ->
             let res = Compute()
             getToolTipTextCache.Put(key,res)
             res

    member x.GetF1Keyword (line, lineStr, colAtEndOfNames, names) : string option =
       use t = Trace.Call("CompilerServices","GetF1Keyword", fun _->sprintf " line=%+A,idx=%+A,names=%+A" line colAtEndOfNames names) 
       ErrorScope.Protect
            Range.range0
            (fun () ->
                match GetDeclItemsForNamesAtPosition(None, Some names, None, line, lineStr, colAtEndOfNames, ResolveTypeNamesToCtors, ResolveOverloads.No, fun _ -> false) with // F1 Keywords do not distiguish between overloads
                | None -> None
                | Some(items,_,_) ->
                    match items with
                    | [] -> None
                    | [item] ->
                        GetF1Keyword item                        
                    | _ ->
                        // handle new Type()
                        let allTypes, constr, typ =
                            List.fold 
                                (fun (allTypes,constr,typ) item ->
                                    match item, constr, typ with
                                    |   (Item.Types _) as t, _, None  -> allTypes, constr, Some t
                                    |   (Item.Types _), _, _ -> allTypes, constr, typ
                                    |   (Item.CtorGroup _), None, _ -> allTypes, Some item, typ
                                    |   _ -> false, None, None) 
                                (true,None,None) items
                        match allTypes, constr, typ with
                        |   true, Some (Item.CtorGroup(_, _) as item), _    
                                -> GetF1Keyword item                        
                        |   true, _, Some typ
                                -> GetF1Keyword typ
                        |   _ -> None
            )    
            (fun _ -> None)

    member scope.GetMethods (line, lineStr, colAtEndOfNames, namesOpt) : MethodGroup =
        ErrorScope.Protect 
            Range.range0 
            (fun () -> 
                match GetDeclItemsForNamesAtPosition(None,namesOpt,None,line,lineStr,colAtEndOfNames,ResolveTypeNamesToCtors,ResolveOverloads.No, fun _ -> false) with
                | None -> MethodGroup("",[| |])
                | Some(items,denv,m) -> MethodGroup.Create(infoReader,m,denv,items))
            (fun msg -> 
                MethodGroup(msg,[| |]))

    member scope.GetDeclarationLocation (line, lineStr, colAtEndOfNames, names, isDecl) : FindDeclResult =
          match GetDeclItemsForNamesAtPosition (None,Some(names), None, line, lineStr, colAtEndOfNames, ResolveTypeNamesToCtors,ResolveOverloads.Yes, fun _ -> false) with
          | None
          | Some ([], _, _) -> FindDeclResult.DeclNotFound FindDeclFailureReason.Unknown
          | Some (item :: _ , _, _) -> 

              // For IL-based entities, switch to a different item. This is because
              // rangeOfItem, ccuOfItem don't work on IL methods or fields.
              //
              // Later comment: to be honest, they aren't going to work on these new items either.
              // This is probably old code from when we supported 'go to definition' generating IL metadata.
              let item =
                  match item with
                  | Item.MethodGroup (_, (ILMeth (_,ilinfo,_)) :: _) 
                  | Item.CtorGroup (_, (ILMeth (_,ilinfo,_)) :: _) -> Item.Types ("", [ ilinfo.ApparentEnclosingType ])
                  | Item.ILField (ILFieldInfo (typeInfo, _)) -> Item.Types ("", [ typeInfo.ToType ])
                  | Item.ImplicitOp(_, {contents = Some(TraitConstraintSln.FSMethSln(_, vref, _))}) -> Item.Value(vref)
                  | _                                         -> item

              let fail defaultReason = 
                  match item with            
#if EXTENSIONTYPING
                  | Params.ItemIsTypeWithStaticArguments g (tcref) -> FindDeclResult.DeclNotFound (FindDeclFailureReason.ProvidedType(tcref.DisplayName))
                  | Item.CtorGroup(name, ProvidedMeth(_)::_)
                  | Item.MethodGroup(name, ProvidedMeth(_)::_)
                  | Item.Property(name, ProvidedProp(_)::_) -> FindDeclResult.DeclNotFound (FindDeclFailureReason.ProvidedMember(name))
                  | Item.Event(ProvidedEvent(_) as e) -> FindDeclResult.DeclNotFound (FindDeclFailureReason.ProvidedMember(e.EventName))
                  | Item.ILField(ProvidedField(_) as f) -> FindDeclResult.DeclNotFound (FindDeclFailureReason.ProvidedMember(f.FieldName))
#endif
                  | _ -> FindDeclResult.DeclNotFound defaultReason

              match rangeOfItem g isDecl item with
              | None   -> fail FindDeclFailureReason.Unknown 
              | Some itemRange -> 

                  let projectDir = Filename.directoryName (if projectFileName = "" then mainInputFileName else projectFileName)
                  let filename = fileNameOfItem g (Some projectDir) itemRange item
                  if FileSystem.SafeExists filename then 
                      FindDeclResult.DeclFound (mkRange filename itemRange.Start itemRange.End)
                  else 
                      fail FindDeclFailureReason.NoSourceCode // provided items may have TypeProviderDefinitionLocationAttribute that binds them to some location

    member scope.GetSymbolAtLocation (line, lineStr, colAtEndOfNames, names) =
        match GetDeclItemsForNamesAtPosition (None,Some(names), None, line, lineStr, colAtEndOfNames, ResolveTypeNamesToCtors, ResolveOverloads.Yes, fun _ -> false) with
        | None | Some ([], _, _) -> None
        | Some (item :: _ , _, _) -> Some (FSharpSymbol.Create(g, thisCcu, tcImports, item))

    member scope.PartialAssemblySignature() = FSharpAssemblySignature(g, thisCcu, tcImports, ccuSig)

    member scope.GetReferencedAssemblies() = 
        [ for x in tcImports.GetImportedAssemblies() do 
                yield FSharpAssembly(g, thisCcu, tcImports, x.FSharpViewOfMetadata) ]


    // Not, this does not have to be a SyncOp, it can be called from any thread
    member scope.GetExtraColorizations() = 
         [| for cnr in sResolutions.CapturedNameResolutions do  
               match cnr with 
               // 'seq' in 'seq { ... }' gets colored as keywords
               | CNR(_, (Item.Value vref), ItemOccurence.Use, _, _, _, m) when valRefEq g g.seq_vref vref -> 
                   yield (m, TokenColorKind.Keyword) 
               // custom builders, custom operations get colored as keywords
               | CNR(_, (Item.CustomBuilder _ | Item.CustomOperation _), ItemOccurence.Use, _, _, _, m) -> 
                   yield (m, TokenColorKind.Keyword) 
#if COLORIZE_TYPES
               // types get colored as types when they occur in syntactic types or custom attributes
               // typevariables get colored as types when they occur in syntactic types custom builders, custom operations get colored as keywords
               | CNR(_, (Item.TypeVar  _ | Item.Types _ | Item.UnqualifiedType _) , (ItemOccurence.UseInType | ItemOccurence.UseInAttribute), _, _, _, m) -> 
                   yield (m, TokenColorKind.TypeName) 
#endif
               | _ -> () 
           |]
    member x.ScopeResolutions = sResolutions
    member x.TcGlobals = g
    member x.TcImports = tcImports
    member x.CcuSig = ccuSig
    member x.ThisCcu = thisCcu

module internal Parser = 

        // We'll need number of lines for adjusting error messages at EOF
    let GetFileInfoForLastLineErrors (source: string) = 
        // number of lines in the source file
        let lastLine = (source |> Seq.sumBy (fun c -> if c = '\n' then 1 else 0)) + 1
        // length of the last line
        let lastLineLength = source.Length - source.LastIndexOf("\n",StringComparison.Ordinal) - 1
        lastLine, lastLineLength
         
    let ReportError (tcConfig:TcConfig, allErrors, mainInputFileName, fileInfo, (exn, warn)) = 
        [ let warn = warn && not (ReportWarningAsError tcConfig.globalWarnLevel tcConfig.specificWarnOff tcConfig.specificWarnOn tcConfig.specificWarnAsError tcConfig.specificWarnAsWarn tcConfig.globalWarnAsError exn)                
          if (not warn || ReportWarning tcConfig.globalWarnLevel tcConfig.specificWarnOff tcConfig.specificWarnOn exn) then 
            let oneError trim exn = 
                [ // We use the first line of the file as a fallbackRange for reporting unexpected errors.
                  // Not ideal, but it's hard to see what else to do.
                  let fallbackRange = rangeN mainInputFileName 1
                  let ei = ErrorInfo.CreateFromExceptionAndAdjustEof(exn,warn,trim,fallbackRange,fileInfo)
                  if allErrors || (ei.FileName=mainInputFileName) || (ei.FileName=Microsoft.FSharp.Compiler.Env.DummyFileNameForRangesWithoutASpecificLocation) then
                      yield ei ]
                      
            let mainError,relatedErrors = Build.SplitRelatedErrors exn 
            yield! oneError false mainError
            for e in relatedErrors do 
                yield! oneError true e ]

    let CreateErrorInfos (tcConfig:TcConfig, allErrors, mainInputFileName, fileInfo, errors) = 
        [| for (exn,warn) in errors do 
              yield! ReportError (tcConfig, allErrors, mainInputFileName, fileInfo, (exn, warn)) |]
                            

    /// Error handler for parsing & type checking while processing a single file
    type ErrorHandler(reportErrors, mainInputFileName, tcConfig: TcConfig, source: string) =
        let mutable tcConfig = tcConfig
        let errorsAndWarningsCollector = new ResizeArray<_>()
        let mutable errorCount = 0
         
        // We'll need number of lines for adjusting error messages at EOF
        let fileInfo = GetFileInfoForLastLineErrors source
         
        // This function gets called whenever an error happens during parsing or checking
        let errorSink warn (exn:PhasedError) = 
            // Sanity check here. The phase of an error should be in a phase known to the language service.
            let exn =
                if not(exn.IsPhaseInCompile()) then
                    // Reaching this point means that the error would be sticky if we let it prop up to the language service.
                    // Assert and recover by replacing phase with one known to the language service.
                    System.Diagnostics.Debug.Assert(false, sprintf "The subcategory '%s' seen in an error should not be seen by the language service" (exn.Subcategory()))
                    {exn with Phase=BuildPhase.TypeCheck}
                else exn
            if reportErrors then 
                let report exn = 
                    for ei in ReportError (tcConfig, false, mainInputFileName, fileInfo, (exn, warn)) do
                        errorsAndWarningsCollector.Add ei
                        if not warn then 
                            errorCount <- errorCount + 1
                      
                match exn with
#if EXTENSIONTYPING
                | {Exception = (:? TypeProviderError as tpe)} ->
                    tpe.Iter (fun e ->
                        let newExn = {exn with Exception = e}
                        report newExn
                    )
#endif
                | e -> report e
      
        let errorLogger = 
            { new ErrorLogger("ErrorHandler") with 
                member x.WarnSinkImpl exn = errorSink true exn
                member x.ErrorSinkImpl exn = errorSink false exn
                member x.ErrorCount = errorCount }
      
      
        // Public members
        member x.ErrorLogger = errorLogger
        member x.CollectedErrorsAndWarnings = errorsAndWarningsCollector.ToArray()
        member x.ErrorCount = errorCount
        member x.TcConfig with set tc = tcConfig <- tc
        member x.AnyErrors = errorCount > 0

    /// Report an unexpect (bug) exception.
    let ReportUnexpectedException exn = 
        match exn with
        | WrappedError(wrappedExn,_) ->
            System.Diagnostics.Debug.Assert(false, sprintf "Bug seen in service-level request. Underlying wrapped exception was %s\n"  (wrappedExn.ToString()))
            Trace.PrintLine("CompilerServices", fun _ -> sprintf "Underlying wrapped exception was %s\n" (wrappedExn.ToString()))
        | _ -> ()
        System.Diagnostics.Debug.Assert(false, sprintf "Bug seen in service-level request: %s\n"  (exn.ToString()))
        Trace.PrintLine("CompilerServices", fun _ -> sprintf "Unexpected error %s\n" (exn.ToString()))
                

    /// ParseOneFile builds all the information necessary to report errors, match braces and build scopes 
    ///
    /// projectSourceFiles is only used to compute isLastCompiland, and is ignored if Build.IsScript(mainInputFileName)  is true.
    let ParseOneFile (source: string, matchBracesOnly: bool, reportErrors: bool, mainInputFileName: string, projectSourceFiles: string list, tcConfig: TcConfig) =

      try 
          Trace.PrintLine("CompilerServices", fun _ -> sprintf "Service.parseSource %s, matchBraces = %b, reportErrors = %b" mainInputFileName matchBracesOnly reportErrors)

          // Initialize the error handler 
          let errHandler = new ErrorHandler(reportErrors, mainInputFileName, tcConfig, source)

          // Very old comment: This helps reason=MethodTip to work. reason=MethodTip 
          // calls with only partial text.  Preumably adding this causes the final EndParameters 
          // call to refer to a different line than the StartParameters call we're really interested in 
          // Or something like that.  
          let source = source + "\n\n\n"
          let lexbuf = UnicodeLexing.StringAsLexbuf source

          // Colelctor for parens matching
          let matchPairRef = new ResizeArray<_>()

          use unwindEL = PushErrorLoggerPhaseUntilUnwind(fun _oldLogger -> errHandler.ErrorLogger)
          use unwindBP = PushThreadBuildPhaseUntilUnwind (BuildPhase.Parse)

          // Errors on while parsing project arguments 

          let parseResult = 

              // If we're editing a script then we define INTERACTIVE otherwise COMPILED. Since this parsing for intellisense we always
              // define EDITING
              let conditionalCompilationDefines =
                SourceFileImpl.AdditionalDefinesForUseInEditor(mainInputFileName) @ tcConfig.conditionalCompilationDefines 
        
              let lightSyntaxStatusInital = tcConfig.ComputeLightSyntaxInitialStatus mainInputFileName
              let lightSyntaxStatus = LightSyntaxStatus(lightSyntaxStatusInital,true)

              // Note: we don't really attempt to intern strings across a large scope
              let lexResourceManager = new Lexhelp.LexResourceManager()
              let lexargs = mkLexargs(mainInputFileName,
                                      conditionalCompilationDefines,
                                      lightSyntaxStatus,
                                      lexResourceManager,
                                      ref [],
                                      errHandler.ErrorLogger)
              Lexhelp.usingLexbufForParsing (lexbuf, mainInputFileName) (fun lexbuf -> 
                  try 
                    let skip = true
                    let tokenizer = Lexfilter.LexFilter (lightSyntaxStatus, tcConfig.compilingFslib, Lexer.token lexargs skip, lexbuf)
                    let lexfun = tokenizer.Lexer
                    if matchBracesOnly then 
                        // Quick bracket matching parse  
                        let parenTokensBalance t1 t2 = 
                            match t1,t2 with 
                            | (LPAREN,RPAREN) 
                            | (LPAREN,RPAREN_IS_HERE) 
                            | (LBRACE,RBRACE) 
                            | (LBRACE,RBRACE_IS_HERE) 
                            | (SIG,END) 
                            | (STRUCT,END) 
                            | (LBRACK_BAR,BAR_RBRACK)
                            | (LBRACK,RBRACK)
                            | (LBRACK_LESS,GREATER_RBRACK)
                            | (BEGIN,END) -> true 
                            | (LQUOTE q1,RQUOTE q2) when q1 = q2 -> true 
                            | _ -> false
                        let rec matchBraces stack = 
                            match lexfun lexbuf,stack with 
                            | tok2,((tok1,m1) :: stack') when parenTokensBalance tok1 tok2-> 
                                if matchBracesOnly then 
                                    matchPairRef.Add (m1, lexbuf.LexemeRange)
                                matchBraces stack'
                            | ((LPAREN | LBRACE | LBRACK | LBRACK_BAR | LQUOTE _ | LBRACK_LESS) as tok),_ -> matchBraces ((tok,lexbuf.LexemeRange) :: stack)
                            | (EOF _ | LEX_FAILURE _),_ -> ()
                            | _ -> matchBraces stack

                        matchBraces []
                        None
                    else 
                        let isLastCompiland = 
                            tcConfig.target.IsExe && 
                            projectSourceFiles.Length >= 1 && 
                            System.String.Compare(projectSourceFiles.[projectSourceFiles.Length-1],mainInputFileName,StringComparison.CurrentCultureIgnoreCase)=0
                        let isLastCompiland = isLastCompiland || Build.IsScript(mainInputFileName)  

                        let parseResult = ParseInput(lexfun,errHandler.ErrorLogger,lexbuf,None,mainInputFileName,isLastCompiland)
                        Some parseResult
                  with e -> 
                    Trace.PrintLine("CompilerServices", fun _ -> sprintf "Could not recover from errors while parsing: %s\n" (e.ToString()))
                    errHandler.ErrorLogger.ErrorR(e)
                    None)
                
          Trace.PrintLine("CompilerServices", fun _ -> sprintf "#errors = %d\n" errHandler.CollectedErrorsAndWarnings.Length)

          errHandler.CollectedErrorsAndWarnings,
          matchPairRef.ToArray(),
          parseResult,
          errHandler.AnyErrors
      with exn -> 
          ReportUnexpectedException exn
          reraise()



    /// Indicates if the type check got aborted because it is no longer relevant.
    type TypeCheckAborted = Yes | No of TypeCheckInfo

    // Type check a single file against an initial context, gleaning both errors and intellisense information.
    let TypeCheckOneFile
          (parseResults: ParseFileResults,
           source: string,
           mainInputFileName: string,
           projectFileName: string,
           tcConfig: TcConfig,
           tcGlobals: TcGlobals,
           tcImports: TcImports,
           tcState: TcState,
           loadClosure: LoadClosure option,
           // These are the errors and warnings seen by the background compiler for the entire antecedant 
           backgroundErrors: (PhasedError * bool) list,    
           syncop: (unit->unit)->unit,
           // Used by 'DeclarationSet' to check the IncrementalBuilder is still alive.
           checkAlive : (unit -> bool),
           isResultObsolete: unit->bool,
           textSnapshotInfo : obj option) = 
      try
        match parseResults.ParseTree with 
        // When processing the following cases, we don't need to type-check
        | None -> 
            [| |], TypeCheckAborted.Yes
               
        // Run the type checker...
        | Some parsedMainInput ->

            // Initialize the error handler 
            let errHandler = new ErrorHandler(true,mainInputFileName,tcConfig, source)

            use unwindEL = PushErrorLoggerPhaseUntilUnwind (fun _oldLogger -> errHandler.ErrorLogger)
            use unwindBP = PushThreadBuildPhaseUntilUnwind (BuildPhase.TypeCheck)      
        
            // Apply nowarns to tcConfig (may generate errors, so ensure errorLogger is installed)
            let tcConfig = ApplyNoWarnsToTcConfig tcConfig (parsedMainInput,Path.GetDirectoryName mainInputFileName)
                    
            // update the error handler with the modified tcConfig
            errHandler.TcConfig <- tcConfig

            // Play background errors and warnings for this file.
            backgroundErrors
            |> List.iter(fun (err,iserr) ->if iserr then errorSink err else warnSink err)


            // If additional references were brought in by the preprocessor then we need to process them
            match loadClosure with
            | Some loadClosure ->
                // Play unresolved references for this file.
                tcImports.ReportUnresolvedAssemblyReferences(loadClosure.UnresolvedReferences)

                // If there was a loadClosure, replay the errors and warnings
                loadClosure.RootErrors |> List.iter errorSink
                loadClosure.RootWarnings |> List.iter warnSink
                

                let fileOfBackgroundError err = (match RangeOfError (fst err) with Some m-> m.FileName | None -> null)
                let sameFile file hashLoadInFile = 
                    (0 = String.Compare(fst hashLoadInFile, file, StringComparison.OrdinalIgnoreCase))

                //  walk the list of #loads and keep the ones for this file.
                let hashLoadsInFile = 
                    loadClosure.SourceFiles 
                    |> List.filter(fun (_,ms) -> ms<>[]) // #loaded file, ranges of #load

                let hashLoadBackgroundErrors, otherBackgroundErrors = 
                    backgroundErrors |> List.partition (fun backgroundError -> hashLoadsInFile |> List.exists (sameFile (fileOfBackgroundError backgroundError)))

                // Create single errors for the #load-ed files.
                // Group errors and warnings by file name.
                let hashLoadBackgroundErrorsGroupedByFileName = 
                    hashLoadBackgroundErrors 
                    |> List.map(fun err -> fileOfBackgroundError err,err) 
                    |> List.groupByFirst  // fileWithErrors, error list

                //  Join the sets and report errors. 
                //  It is by-design that these messages are only present in the language service. A true build would report the errors at their
                //  spots in the individual source files.
                for hashLoadInFile in hashLoadsInFile do
                    for errorGroupedByFileName in hashLoadBackgroundErrorsGroupedByFileName do
                        if sameFile (fst errorGroupedByFileName) hashLoadInFile then
                            for rangeOfHashLoad in snd hashLoadInFile do // Handle the case of two #loads of the same file
                                let errorsAndWarnings = snd errorGroupedByFileName |> List.map(fun (pe,f)->pe.Exception,f) // Strip the build phase here. It will be replaced, in total, with TypeCheck
                                let errors,warnings = errorsAndWarnings |> List.partition snd 
                                let errors,warnings = errors |> List.map fst, warnings |> List.map fst
                                
                                let message = HashLoadedSourceHasIssues(warnings,errors,rangeOfHashLoad)
                                if errors=[] then warning(message)
                                else errorR(message)

                // Replay other background errors.
                for (phasedError,isWarning) in otherBackgroundErrors do
                    if isWarning then warning phasedError.Exception else errorR phasedError.Exception

            | None -> 
                // For non-scripts, check for disallow #r and #load.
                ApplyMetaCommandsFromInputToTcConfig tcConfig (parsedMainInput,Path.GetDirectoryName mainInputFileName) |> ignore
                
            if verbose then 
                tcConfig.includes |> List.iter (fun p -> Trace.PrintLine("CompilerServicesVerbose", fun _ -> sprintf "include directory '%s'\n" p)) 
                tcConfig.implicitOpens |> List.iter (fun p -> Trace.PrintLine("CompilerServicesVerbose", fun _ -> sprintf "implicit open '%s'\n" p)) 
                tcConfig.referencedDLLs |> List.iter (fun r -> Trace.PrintLine("CompilerServicesVerbose", fun _ -> sprintf "dll from flags '%s'\n" r.Text)) 
            
            // A problem arises with nice name generation, which really should only 
            // be done in the backend, but is also done in the typechecker for better or worse. 
            // If we don't do this the NNG accumulates data and we get a memory leak. 
            tcState.NiceNameGenerator.Reset()
            
            // Typecheck the real input.  
            let sink = TcResultsSinkImpl(tcGlobals)

            let tcEnvAtEndOpt =
                try
                    let checkForErrors() = (parseResults.ParseHadErrors || errHandler.ErrorCount > 0)
                    // Typecheck is potentially a long running operation. We chop it up here with an Eventually continuation and, at each slice, give a chance
                    // for the client to claim the result as obsolete and have the typecheck abort.
                    let computation = TypecheckSingleInputAndFinishEventually(checkForErrors,tcConfig, tcImports, tcGlobals, None, TcResultsSink.WithSink sink, tcState, parsedMainInput)
                    match computation |> Eventually.forceWhile (fun () -> not (isResultObsolete())) with
                    | Some((tcEnvAtEnd,_,typedImplFiles),tcState) -> Some (tcEnvAtEnd, typedImplFiles, tcState)
                    | None -> None // Means 'aborted'
                with
                | e ->
                    errorR e
                    Some(tcState.TcEnvFromSignatures, [], tcState)
            
            let errors = errHandler.CollectedErrorsAndWarnings
            
            match tcEnvAtEndOpt with
            | Some (tcEnvAtEnd, _typedImplFiles, tcState) ->
                let scope = 
                    TypeCheckInfo(tcConfig, tcGlobals, 
                                tcState.PartialAssemblySignature, 
                                tcState.Ccu,
                                tcImports,
                                //typedImplFiles,
                                projectFileName, 
                                mainInputFileName, 
                                sink.GetTcResolutions(), 
                                tcEnvAtEnd.NameEnv,
                                loadClosure,
                                syncop,
                                checkAlive,
                                textSnapshotInfo)     
                errors, TypeCheckAborted.No scope
            | None -> 
                errors, TypeCheckAborted.Yes
      with 
      | e -> 
        ReportUnexpectedException(e)
        reraise()

type  UnresolvedReferencesSet = UnresolvedReferencesSet of UnresolvedAssemblyReference list

// NOTE: may be better just to move to optional arguments here
type ProjectOptions =
    { 
      ProjectFileName: string
      ProjectFileNames: string[]
      ProjectOptions: string[]
      ReferencedProjects: (string * ProjectOptions)[]
      IsIncompleteTypeCheckEnvironment : bool
      UseScriptResolutionRules : bool      
      LoadTime : System.DateTime
      UnresolvedReferences : UnresolvedReferencesSet option
    }
    /// Whether the two parse options refer to the same project.
    static member AreSameProjectName(options1,options2) =
        options1.ProjectFileName = options2.ProjectFileName          
    /// Compare two options sets with respect to the parts of the options that are important to parsing.
    static member AreSameProjectForParsing(options1,options2) =
        ProjectOptions.AreSameProjectName(options1,options2) &&
        options1.ProjectOptions = options2.ProjectOptions &&
        options1.UnresolvedReferences = options2.UnresolvedReferences

    /// Compare two options sets with respect to the parts of the options that are important to building.
    static member AreSameProjectForBuilding(options1,options2) =
        ProjectOptions.AreSameProjectForParsing(options1,options2) &&
        options1.ProjectFileNames = options2.ProjectFileNames &&
        options1.ProjectOptions = options2.ProjectOptions &&
        options1.ReferencedProjects.Length = options2.ReferencedProjects.Length &&
        Array.forall2 (fun (n1,a) (n2,b) -> n1 = n2 && ProjectOptions.AreSameProjectForBuilding(a,b)) options1.ReferencedProjects options2.ReferencedProjects &&
        options1.LoadTime = options2.LoadTime
    /// Compute the project directory.
    member po.ProjectDirectory = System.IO.Path.GetDirectoryName(po.ProjectFileName)
    override this.ToString() =
        let files =
            let sb = new StringBuilder()
            this.ProjectFileNames |> Array.iter (fun file -> sb.AppendFormat("    {0}\n", file) |> ignore)
            sb.ToString()
        let options =
            let sb = new StringBuilder()
            this.ProjectOptions |> Array.iter (fun op -> sb.AppendFormat("{0} ", op) |> ignore)
            sb.ToString()
        sprintf "ProjectOptions(%s)\n  Files:\n%s  Options: %s" this.ProjectFileName files options
 
type IReactorOperations = 
    abstract RunAsyncOp : (unit -> 'T) -> Async<'T>
    abstract AsyncOp: Reactor.Operation -> unit
    abstract SyncOp: Reactor.Operation -> unit
    abstract RunSyncOp: (unit -> 'T) -> 'T

type [<Sealed>] ProjectContext(assemblies: FSharpAssembly list) =

    /// Get the assemblies referenced
    member __.GetReferencedAssemblies() = assemblies


[<Sealed>]
type FSharpSymbolUse(symbol:FSharpSymbol, itemOcc, range: range) = 
    member __.Symbol  = symbol
    member __.IsDefinition = (match itemOcc with ItemOccurence.Binding | ItemOccurence.Pattern -> true | _ -> false)
    member __.FileName = range.FileName
    member __.Range = Range.toZ range
    member __.RangeAlternate = range

[<Sealed>]
// 'details' is an option because the creation of the tcGlobals etc. for the project may have failed.
type CheckProjectResults(errors: ErrorInfo[], details:(TcGlobals*TcImports*CcuThunk*ModuleOrNamespaceType*TcResolutions list*Build.IRawFSharpAssemblyContents option * ILAssemblyRef) option, reactorOps: IReactorOperations) =

    let getDetails() = 
        match details with 
        | None -> invalidOp ("The project has no results due to critical errors in the project options. Check the HasCriticalErrors before accessing the detaild results. Errors: " + String.concat "\n" [ for e in errors -> e.Message ])
        | Some d -> d

    member info.Errors = errors

    member info.HasCriticalErrors = details.IsNone

    member info.AssemblySignature =  
        let (tcGlobals, tcImports, thisCcu, ccuSig, _tcResolutions, _assembly, _ilAssemRef) = getDetails()
        FSharpAssemblySignature(tcGlobals, thisCcu, tcImports, ccuSig)

    // Not, this does not have to be a SyncOp, it can be called from any thread
    member info.GetUsesOfSymbol(symbol:FSharpSymbol) = 
        let (_tcGlobals, _tcImports, _thisCcu, _ccuSig, tcResolutions, _assembly, _ilAssemRef) = getDetails()
        // This probably doesn't need to be run on the reactor since all data touched by GetUsesOfSymbol is immutable.
        reactorOps.RunSyncOp(fun () -> 
            [| for r in tcResolutions do yield! r.GetUsesOfSymbol(symbol.Item) |] 
            |> Seq.distinct 
            |> Seq.map (fun (itemOcc,m) -> FSharpSymbolUse(symbol, itemOcc, m)) 
            |> Seq.toArray)

    // Not, this does not have to be a SyncOp, it can be called from any thread
    member info.GetAllUsesOfAllSymbols() = 
        let (tcGlobals, tcImports, thisCcu, _ccuSig, tcResolutions, _assembly, _ilAssemRef) = getDetails()
        // This probably doesn't need to be run on the reactor since all data touched by GetAllUsesOfSymbols is immutable.
        reactorOps.RunSyncOp(fun () -> 
            [| for r in tcResolutions do 
                  for (item,itemOcc,m) in r.GetAllUsesOfSymbols() do
                    let symbol = FSharpSymbol.Create(tcGlobals, thisCcu, tcImports, item)
                    yield FSharpSymbolUse(symbol, itemOcc, m) |]) 

    member info.ProjectContext = 
        let (tcGlobals, tcImports, thisCcu, _ccuSig, _tcResolutions, _assembly, _ilAssemRef) = getDetails()
        let assemblies = 
            [ for x in tcImports.GetImportedAssemblies() do 
                yield FSharpAssembly(tcGlobals, thisCcu, tcImports, x.FSharpViewOfMetadata) ]
        ProjectContext(assemblies) 

    member info.RawFSharpAssemblyContents = 
        let (_tcGlobals, _tcImports, _thisCcu, _ccuSig, _tcResolutions, assembly, _ilAssemRef) = getDetails()
        assembly

    member info.AssemblyFullName = 
        let (_tcGlobals, _tcImports, _thisCcu, _ccuSig, _tcResolutions, _assembly, ilAssemRef) = getDetails()
        ilAssemRef.QualifiedName

[<Sealed>]
/// A live object of this type keeps the background corresponding background builder (and type providers) alive (through reference-counting).
//
// There is an important property of all the objects returned by the methods of this type: they do not require 
// the corresponding background builder to be alive. That is, they are simply plain-old-data through pre-formatting of all result text.
type CheckFileResults(errors: ErrorInfo[], scopeOptX: TypeCheckInfo option, builderX: IncrementalFSharpBuild.IncrementalBuilder option, reactorOpsX:IReactorOperations) =

    // This may be None initially, or may be set to None when the object is disposed or finalized
    let mutable details = match scopeOptX with None -> None | Some scopeX -> Some (scopeX, builderX, reactorOpsX)

    let decrementer = 
        match details with 
        | Some (_,Some builder,_) -> 
            // Increment the usage count on the IncrementalBuilder. We want to keep the IncrementalBuilder and all associated
            // resources and type providers alive for the duration of the lifetime of this object.
            builder.IncrementUsageCount()
        | _ -> { new System.IDisposable with member x.Dispose() = () } 

    let mutable disposed = false

    let dispose() = 
       if not disposed then 
           disposed <- true 
           match details with 
           | Some (_,_,reactor) -> 
               // Make sure we run disposal in the reactor thread, since it may trigger type provider disposals etc.
               details <- None
               reactor.AsyncOp (fun () -> decrementer.Dispose())
           | _ -> () 

    let checkBuilder dflt f = 
        match details with
        | None -> 
            dflt
        | Some (_ , Some builder, _) when not builder.IsAlive -> 
            System.Diagnostics.Debug.Assert(false,"unexpected dead builder") 
            dflt
        | Some (scope, builder, ops) -> 
            f(scope, builder, ops)

    // At the moment we only dispose on finalize - we never explicitly dispose these objects. Explicitly disposing is not
    // really worth much since the underlying project builds are likely to still be in the incrementalBuilder cache.
    override info.Finalize() = dispose() 

    member info.Errors = errors

    member info.HasFullTypeCheckInfo = details.IsSome
    
    /// Intellisense autocompletions
    member info.GetDeclarationsAlternate(parseResultsOpt, line, colAtEndOfNamesAndResidue, lineStr, qualifyingNames, partialName, ?hasTextChangedSinceLastTypecheck) = 
        let hasTextChangedSinceLastTypecheck = defaultArg hasTextChangedSinceLastTypecheck (fun _ -> false)
        checkBuilder (async.Return DeclarationSet.Empty) (fun (scope, builderOpt, reactor) -> 
            async { // Ensure the builder doesn't get released while running GetDeclarations asynchronously. In synchronous operations,
                    // the builder is kept alive at least because the CheckFileResults object itself is alive (note it is almsot certain to 
                    // be alive for other reasons too anyway, e.g. in the incrementalBuildersCache).
                    use _unwind = match builderOpt with Some builder -> builder.IncrementUsageCount() | None -> { new System.IDisposable with member __.Dispose() = () }
                    return! reactor.RunAsyncOp(fun () -> scope.GetDeclarations(parseResultsOpt, line, lineStr, colAtEndOfNamesAndResidue, qualifyingNames, partialName, hasTextChangedSinceLastTypecheck))  
                  })

    /// Resolve the names at the given location to give a data tip 
    member info.GetToolTipTextAlternate(line, colAtEndOfNames, lineStr, names, tokenTag) = 
        let dflt = ToolTipText []
        checkBuilder dflt (fun (scope, _builder, reactor) -> 
            match tokenTagToTokenId tokenTag with 
            | TOKEN_IDENT -> 
                reactor.RunSyncOp (fun () -> scope.GetToolTipText line lineStr colAtEndOfNames names)
            | TOKEN_STRING | TOKEN_STRING_TEXT -> 
                reactor.RunSyncOp (fun () -> scope.GetReferenceResolutionToolTipText(line, colAtEndOfNames))        
            | _ -> ToolTipText [])

    member info.GetF1KeywordAlternate (line, colAtEndOfNames, lineStr, names) =
        checkBuilder None (fun (scope, _builder, reactor) -> 
            reactor.RunSyncOp (fun () -> scope.GetF1Keyword (line, lineStr, colAtEndOfNames, names)))

    // Resolve the names at the given location to a set of methods
    member info.GetMethodsAlternate(line, colAtEndOfNames, lineStr, names) =
        let dflt = MethodGroup("",[| |])
        checkBuilder dflt (fun (scope, _builder, reactor) -> 
            reactor.RunSyncOp (fun () -> scope.GetMethods (line, lineStr, colAtEndOfNames, names)))
            
    member info.GetDeclarationLocationAlternate (line, colAtEndOfNames, lineStr, names, flag) = 
        let dflt = FindDeclResult.DeclNotFound FindDeclFailureReason.Unknown
        checkBuilder dflt (fun (scope, _builder, reactor) -> 
            reactor.RunSyncOp (fun () -> scope.GetDeclarationLocation (line, lineStr, colAtEndOfNames, names, flag)))

    member info.GetSymbolAtLocationAlternate (line, colAtEndOfNames, lineStr, names) = 
        checkBuilder None (fun (scope, _builder, reactor) -> 
            reactor.RunSyncOp (fun () -> scope.GetSymbolAtLocation (line, lineStr, colAtEndOfNames, names)))

    member info.GetExtraColorizationsAlternate() = 
        use t = Trace.Call("SyncOp","GetExtraColorizations", fun _->sprintf "")
        checkBuilder [| |] (fun (scope, _builder, _reactor) -> 
            scope.GetExtraColorizations())
     
    member info.PartialAssemblySignature = 
        use t = Trace.Call("SyncOp","PartialAssemblySignature", fun _->sprintf "")
        checkBuilder Unchecked.defaultof<_> (fun (scope, _builder, _reactor) -> 
            scope.PartialAssemblySignature())

    member info.ProjectContext = 
        let assemblies = 
            checkBuilder [] (fun (scope, _builder, _reactor) -> 
                scope.GetReferencedAssemblies()  )
        ProjectContext(assemblies)

    // Not, this does not have to be a SyncOp, it can be called from any thread
    member info.GetAllUsesOfAllSymbolsInFile() = 
        checkBuilder [| |] (fun (scope, _builder, reactor) -> 
            // This probably doesn't need to be run on the reactor since all data touched by GetUsesOfSymbol is immutable.
            reactor.RunSyncOp(fun () -> 
                [|    for (item,itemOcc,m) in scope.ScopeResolutions.GetAllUsesOfSymbols() do
                        let symbol = FSharpSymbol.Create(scope.TcGlobals, scope.ThisCcu, scope.TcImports, item)
                        yield FSharpSymbolUse(symbol,itemOcc, m) |]))

    // Not, this does not have to be a SyncOp, it can be called from any thread
    member info.GetUsesOfSymbolInFile(symbol:FSharpSymbol) = 
        checkBuilder [| |] (fun (scope, _builder, reactor) -> 
            // This probably doesn't need to be run on the reactor since all data touched by GetUsesOfSymbol is immutable.
            reactor.RunSyncOp(fun () -> 
                [|    for (itemOcc,m) in scope.ScopeResolutions.GetUsesOfSymbol(symbol.Item) do
                        yield FSharpSymbolUse(symbol,itemOcc, m) |]))

    
    // Obsolete

    member info.GetDeclarations(parseResultsOpt, line, colAtEndOfNamesAndResidue, lineStr, qualifyingNames, partialName, ?hasTextChangedSinceLastTypecheck) = 
        info.GetDeclarationsAlternate(parseResultsOpt, Line.fromZ line, colAtEndOfNamesAndResidue, lineStr, qualifyingNames, partialName, ?hasTextChangedSinceLastTypecheck=(match hasTextChangedSinceLastTypecheck with None -> None | Some f -> Some (fun (a,b) -> f (a, Range.toZ b))))

    member info.GetExtraColorizations() =  
        info.GetExtraColorizationsAlternate() |> Array.map (fun (a,b) -> (Range.toZ a, b))

    member info.GetToolTipText(line, colAtEndOfNames, lineStr, names, tokenTag) = 
        info.GetToolTipTextAlternate(Line.fromZ line, colAtEndOfNames, lineStr, names, tokenTag)

    member info.GetDataTipText(line, colAtEndOfNames, lineStr, names, tokenTag) = 
        info.GetToolTipText(line, colAtEndOfNames, lineStr, names, tokenTag)

    member info.GetDeclarationLocation (line, colAtEndOfNames, lineStr, names, _tokenTag:int, flag) = 
        info.GetDeclarationLocation (line, colAtEndOfNames, lineStr, names, flag) 

    member info.GetDeclarationLocation (line, colAtEndOfNames, lineStr, names, flag) = 
        info.GetDeclarationLocationAlternate (Line.fromZ line, colAtEndOfNames, lineStr, names, flag) 

    member info.GetSymbolAtLocation (line, colAtEndOfNames, lineStr, names) = 
        info.GetSymbolAtLocationAlternate (Line.fromZ line, colAtEndOfNames, lineStr, names) 

    member info.GetF1Keyword (line,colAtEndOfNames,lineStr,names) =
        info.GetF1KeywordAlternate (Line.fromZ line,colAtEndOfNames,lineStr,names)

    member info.GetMethods (line, colAtEndOfNames,lineStr:string,names:Names option) =
        info.GetMethodsAlternate(Line.fromZ line, colAtEndOfNames,lineStr,names) 

//----------------------------------------------------------------------------
// BackgroundCompiler
//

[<NoComparison>]
type CheckFileAnswer =
    | Aborted
    | Succeeded of CheckFileResults   
        

/// Callback that indicates whether a requested result has become obsolete.    
[<NoComparison;NoEquality>]
type (*internal*) IsResultObsolete = 
    | IsResultObsolete of (unit->bool)


        
// There is only one instance of this type, held in InteractiveChecker
type BackgroundCompiler() as self =
    // STATIC ROOT: LanguageServiceState.InteractiveChecker.backgroundCompiler.reactor: The one and only Reactor
    let reactor = Reactor.Reactor()
    let beforeFileChecked = Event<string>()
    let fileParsed = Event<string>()
    let fileChecked = Event<string>()
    let projectChecked = Event<string>()

    let reactorOps = 
        { new IReactorOperations with 
                member __.RunAsyncOp op = reactor.RunAsyncOp op
                member __.RunSyncOp op = reactor.RunSyncOp op
                member __.SyncOp op = reactor.SyncOp op
                member __.AsyncOp op = reactor.AsyncOp op }

    // STATIC ROOT: LanguageServiceState.InteractiveChecker.backgroundCompiler.scriptClosure 
    /// Information about the derived script closure.
    let scriptClosure = AgedLookup<ProjectOptions,LoadClosure>(buildCacheSize, areSame=ProjectOptions.AreSameProjectForBuilding)

    /// CreateOneIncrementalBuilder (for background type checking). Note that fsc.fs also
    /// creates an incremental builder used by the command line compiler.
    let CreateOneIncrementalBuilder (options:ProjectOptions) = 
        use t = Trace.Call("Reactor","CreateOneIncrementalBuilder", fun () -> sprintf "options = %+A" options)

        let projectReferences =  
            [ for (nm,opts) in options.ReferencedProjects do 
                 let r = self.ParseAndCheckProjectImpl(opts)
                 match r.RawFSharpAssemblyContents with 
                 | Some c -> yield (nm, c)
                 | _ -> () ]

        let builderOpt, errorsAndWarnings = 
            // PROBLEM: This call can currently fail if an error happens while setting up the TcConfig
            // This leaves us completely horked.
            IncrementalFSharpBuild.IncrementalBuilder.TryCreateBackgroundBuilderForProjectOptions
                  (scriptClosure.TryGet options, Array.toList options.ProjectFileNames, 
                   Array.toList options.ProjectOptions, projectReferences, options.ProjectDirectory, 
                   options.UseScriptResolutionRules, options.IsIncompleteTypeCheckEnvironment)

        // We're putting the builder in the cache, so increment its count.
        let decrement = 
            match builderOpt with 
            | None -> { new IDisposable with member x.Dispose() = () }
            | Some builder ->  builder.IncrementUsageCount()

        match builderOpt with 
        | None -> ()
        | Some builder -> 

            // Register the behaviour that responds to CCUs being invalidated because of type
            // provider Invalidate events. This invalidates the configuration in the build.
            builder.ImportedCcusInvalidated.Add (fun msg -> 
                System.Diagnostics.Debugger.Log(100, "service", sprintf "A build cache entry is being invalidated because of a : %s" msg)
                self.InvalidateConfiguration options)

            // Register the callback called just before a file is typechecked by the background builder (without recording
            // errors or intellisense information).
            //
            // This indicates to the UI that the file type check state is dirty. If the file is open and visible then 
            // the UI will sooner or later request a typecheck of the file, recording errors and intellisense information.
            builder.BeforeTypeCheckFile.Add (beforeFileChecked.Trigger)
            builder.FileParsed.Add (fileParsed.Trigger)
            builder.FileChecked.Add (fileChecked.Trigger)
            builder.ProjectChecked.Add (fun () -> projectChecked.Trigger options.ProjectFileName)

        (builderOpt, errorsAndWarnings, decrement)

    // STATIC ROOT: LanguageServiceState.InteractiveChecker.backgroundCompiler.incrementalBuildersCache. This root typically holds more 
    // live information than anything else in the F# Language Service, since it holds up to 3 (buildCacheSize) background project builds
    // strongly.
    // 
    /// Cache of builds keyed by options.        
    let incrementalBuildersCache = 
        MruCache(keepStrongly=buildCacheSize, keepMax=buildCacheSize, compute=CreateOneIncrementalBuilder, 
                 areSame =  ProjectOptions.AreSameProjectForBuilding, 
                 areSameForSubsumption =  ProjectOptions.AreSameProjectName,
                 onDiscard = (fun (_, _, decrement) -> decrement.Dispose()))

    
    let MakeCheckFileResultsEmpty(creationErrors) = 
        CheckFileResults (Array.ofList creationErrors,None, None, reactorOps)

    let MakeCheckFileResults(options:ProjectOptions, builder, scope, creationErrors, parseErrors, tcErrors) = 
        let errors = 
            [| yield! creationErrors 
               yield! parseErrors
               if options.IsIncompleteTypeCheckEnvironment then 
                    yield! Seq.truncate maxTypeCheckErrorsOutOfProjectContext tcErrors
               else 
                    yield! tcErrors |]
                
        CheckFileResults (errors, Some scope, Some builder, reactorOps)

    let MakeCheckFileAnswer(tcFileResult, options:ProjectOptions, builder, creationErrors, parseErrors, tcErrors) = 
        match tcFileResult with 
        | Parser.TypeCheckAborted.Yes  ->  CheckFileAnswer.Aborted                
        | Parser.TypeCheckAborted.No scope -> CheckFileAnswer.Succeeded(MakeCheckFileResults(options, builder, scope, creationErrors, parseErrors, tcErrors))


    /// Parses the source file and returns untyped AST
    member bc.ParseFileInProject(filename:string, source,options:ProjectOptions) =
        reactor.RunAsyncOp <| fun () -> 
        
#if TYPE_PROVIDER_SECURITY
            ExtensionTyping.GlobalsTheLanguageServiceCanPoke.theMostRecentFileNameWeChecked <- Some filename
#endif
            let builderOpt,creationErrors,_ = incrementalBuildersCache.Get(options) // Q: Whis it it ok to ignore creationErrors in the build cache? A: These errors will be appended into the typecheck results
            match builderOpt with
            | None -> ParseFileResults(List.toArray creationErrors, None, true, [])
            | Some builder -> 
            // Do the parsing.
            let parseErrors, _matchPairs, inputOpt, anyErrors = 
               Parser.ParseOneFile (source, false, true, filename, builder.ProjectFileNames, builder.TcConfig)
                 
            ParseFileResults(parseErrors, inputOpt, anyErrors, builder.Dependencies )
     
    /// Fetch the parse information from the background compiler (which checks w.r.t. the FileSystem API)
    member bc.GetBackgroundParseResultsForFileInProject(filename, options) =
        reactor.RunAsyncOp <| fun () -> 
            let builderOpt, creationErrors, _= incrementalBuildersCache.Get(options)
            match builderOpt with
            | None -> ParseFileResults(List.toArray creationErrors, None, true, [])
            | Some builder -> 
            let inputOpt,_,_,parseErrors = builder.GetParseResultsForFile filename            
            let dependencyFiles = builder.Dependencies 
            //let fileInfo = Parser.GetFileInfoForLastLineErrors source
            let fileInfo = (Int32.MaxValue, Int32.MaxValue)
            let errors = [| yield! creationErrors; yield! Parser.CreateErrorInfos (builder.TcConfig, false, filename, fileInfo, parseErrors) |]
            ParseFileResults(errors = errors, input = inputOpt, parseHadErrors = false, dependencyFiles = dependencyFiles)
        
    member bc.MatchBraces(filename:string, source, options)=
        reactor.RunSyncOp <| fun () -> 
            let builderOpt,_,_ = incrementalBuildersCache.Get(options)
            match builderOpt with
            | None -> [| |]
            | Some builder -> 
            let _parseErrors, matchPairs, _inputOpt, _anyErrors = 
               Parser.ParseOneFile (source, true, false, filename, builder.ProjectFileNames, builder.TcConfig)
                 
            matchPairs

    /// Type-check the result obtained by parsing, but only if the antecedent type checking context is available. 
    member bc.CheckFileInProjectIfReady(parseResults:ParseFileResults,filename,source,options,isResultObsolete,textSnapshotInfo:obj option) =
        reactor.RunSyncOp <| fun () -> 
            match incrementalBuildersCache.GetAvailable options with
            | Some(Some builder, creationErrors, _) ->
            
                match builder.GetCheckResultsBeforeFileInProjectIfReady filename with 
                | Some(tcPriorState,tcImports,tcGlobals,tcConfig,_tcEnvAtEnd,backgroundErrors,_tcResolutions,_antecedantTimeStamp) -> 
        
                    // Get additional script #load closure information if applicable.
                    // For scripts, this will have been recorded by GetProjectOptionsFromScript.
                    let loadClosure = scriptClosure.TryGet options 
                
                    // Run the type checking.
                    let tcErrors, tcFileResult = 
                        Parser.TypeCheckOneFile(parseResults,source,filename,options.ProjectFileName,tcConfig,tcGlobals,tcImports,  tcPriorState,
                                                loadClosure,backgroundErrors,reactor.SyncOp,(fun () -> builder.IsAlive),isResultObsolete,textSnapshotInfo)

                    Some(MakeCheckFileAnswer(tcFileResult, options, builder, creationErrors, parseResults.Errors, tcErrors))
                | None -> None
            | _ -> None

    /// Type-check the result obtained by parsing. Force the evaluation of the antecedent type checking context if needed.
    member bc.CheckFileInProject(parseResults:ParseFileResults,filename,source,options,isResultObsolete,textSnapshotInfo) =
        reactor.RunAsyncOp <| fun () -> 
            let builderOpt,creationErrors,_ = incrementalBuildersCache.Get(options) 
            match builderOpt with
            | None -> CheckFileAnswer.Succeeded (MakeCheckFileResultsEmpty(creationErrors))
            | Some builder -> 
            let (tcPriorState,tcImports,tcGlobals,tcConfig,_priorEnvAtEnd,priorErrors,_tcResolutions,_antecedantTimeStamp) = builder.GetCheckResultsBeforeFileInProject filename 
            let loadClosure = scriptClosure.TryGet options 
            let tcErrors, tcFileResult = 
                Parser.TypeCheckOneFile(parseResults,source,filename,options.ProjectFileName,tcConfig,tcGlobals,tcImports,  tcPriorState,
                                        loadClosure,priorErrors,reactor.SyncOp,(fun () -> builder.IsAlive),isResultObsolete,textSnapshotInfo)
            MakeCheckFileAnswer(tcFileResult, options, builder, creationErrors, parseResults.Errors, tcErrors)

    /// Parses the source file and returns untyped AST
    member bc.ParseAndCheckFileInProject(filename:string, source, options:ProjectOptions,isResultObsolete,textSnapshotInfo,cachedResults) =
        reactor.RunAsyncOp <| fun () -> 
        
#if TYPE_PROVIDER_SECURITY
            ExtensionTyping.GlobalsTheLanguageServiceCanPoke.theMostRecentFileNameWeChecked <- Some filename
#endif
            let builderOpt,creationErrors,_ = incrementalBuildersCache.Get(options) // Q: Whis it it ok to ignore creationErrors in the build cache? A: These errors will be appended into the typecheck results
            match builderOpt with
            | None -> 
                let parseResults = ParseFileResults(List.toArray creationErrors, None, true, [])
                parseResults, CheckFileAnswer.Aborted, false
            | Some builder -> 

            // We can use cached results when there is no work to do to bring the background builder up-to-date
            match cachedResults with 
            | Some (parseResults, checkResults,_) when builder.AreCheckResultsBeforeFileInProjectReady(filename) ->  
               parseResults, CheckFileAnswer.Succeeded checkResults, true
            | _ -> 
            let (tcPriorState,tcImports,tcGlobals,tcConfig,_priorEnvAtEnd,priorErrors,_tcResolutions,_antecedantTimeStamp) = builder.GetCheckResultsBeforeFileInProject filename 

            // Do the parsing.
            let parseErrors, _matchPairs, inputOpt, anyErrors = Parser.ParseOneFile (source, false, true, filename, builder.ProjectFileNames, builder.TcConfig)
                 
            let parseResults = ParseFileResults(parseErrors, inputOpt, anyErrors, builder.Dependencies)
            let loadClosure = scriptClosure.TryGet options 
            let tcErrors, tcFileResult = 
                Parser.TypeCheckOneFile(parseResults,source,filename,options.ProjectFileName,tcConfig,tcGlobals,tcImports,  tcPriorState,
                                        loadClosure,priorErrors,reactor.SyncOp,(fun () -> builder.IsAlive),isResultObsolete,textSnapshotInfo)
            let checkAnswer = MakeCheckFileAnswer(tcFileResult, options, builder, creationErrors, parseResults.Errors, tcErrors)
            parseResults, checkAnswer, false

    /// Fetch the check information from the background compiler (which checks w.r.t. the FileSystem API)
    member bc.GetBackgroundCheckResultsForFileInProject(filename,options) =
        reactor.RunAsyncOp <| fun () -> 
            let (builderOpt, creationErrors, _) = incrementalBuildersCache.Get options 
            match builderOpt with
            | None -> 
                let parseResults = ParseFileResults(Array.ofList creationErrors, None, true, [])
                let typedResults = MakeCheckFileResultsEmpty(creationErrors)
                (parseResults, typedResults)
            | Some builder -> 
                let (inputOpt, _, _, untypedErrors) = builder.GetParseResultsForFile filename  
                let (tcState,tcImports,tcGlobals,tcConfig,tcEnvAtEnd,tcErrors,tcResolutions,_antecedantTimeStamp) = builder.GetCheckResultsAfterFileInProject filename 
                let fileInfo = (Int32.MaxValue, Int32.MaxValue)
                let untypedErrors = [| yield! creationErrors; yield! Parser.CreateErrorInfos (builder.TcConfig, false, filename, fileInfo, untypedErrors) |]
                let tcErrors = [| yield! creationErrors; yield! Parser.CreateErrorInfos (builder.TcConfig, false, filename, fileInfo, tcErrors) |]
                let parseResults = ParseFileResults(errors = untypedErrors, input = inputOpt, parseHadErrors = false, dependencyFiles = builder.Dependencies)
                let loadClosure = scriptClosure.TryGet options 
                let scope = 
                    TypeCheckInfo(tcConfig, tcGlobals, tcState.PartialAssemblySignature, tcState.Ccu, tcImports,
                                  options.ProjectFileName, filename, List.last tcResolutions, tcEnvAtEnd.NameEnv,
                                  loadClosure, reactorOps.SyncOp, (fun () -> builder.IsAlive), None)     
                let typedResults = MakeCheckFileResults(options, builder, scope, creationErrors, parseResults.Errors, tcErrors)
                (parseResults, typedResults)


    /// Parse and typecheck the whole project (the implementation, called recursively as project graph is evaluated)
    member private bc.ParseAndCheckProjectImpl(options) : CheckProjectResults =
        let builderOpt,creationErrors,_ = incrementalBuildersCache.Get(options) 
        match builderOpt with 
        | None -> 
            CheckProjectResults (Array.ofList creationErrors, None, reactorOps)
        | Some builder -> 
            let ((tcState,tcImports,tcGlobals,tcConfig,_tcEnvAtEnd,tcErrors,tcResolutions,_antecedantTimeStamp), ilAssemRef, assemblyOpt)  = 
                builder.GetCheckResultsAndImplementationsForProject()
                //.GetCheckResultsAfterLastFileInProject()
            let fileInfo = (Int32.MaxValue, Int32.MaxValue)
            let errors = [| yield! creationErrors; yield! Parser.CreateErrorInfos (tcConfig, true, Microsoft.FSharp.Compiler.Env.DummyFileNameForRangesWithoutASpecificLocation, fileInfo, tcErrors) |]
            CheckProjectResults (errors, Some(tcGlobals, tcImports, tcState.Ccu, tcState.PartialAssemblySignature, tcResolutions, assemblyOpt, ilAssemRef), reactorOps)

    /// Parse and typecheck the whole project.
    member bc.ParseAndCheckProject(options) =
        reactor.RunAsyncOp <| fun () -> bc.ParseAndCheckProjectImpl(options)

    member bc.GetProjectOptionsFromScript(filename, source, ?loadedTimeStamp, ?otherFlags, ?useFsiAuxLib) = 
        reactor.RunSyncOp (fun () -> 
            // Do we add a reference to FSharp.Compiler.Interactive.Settings by default?
            let useFsiAuxLib = defaultArg useFsiAuxLib true
            // Do we use a "FSharp.Core, 4.3.0.0" reference by default?
            let otherFlags = defaultArg otherFlags [| |]
            let useMonoResolution = runningOnMono || otherFlags |> Array.exists (fun x -> x = "--simpleresolution")
            let loadedTimeStamp = defaultArg loadedTimeStamp DateTime.MaxValue // Not 'now', we don't want to force reloading
            let applyCompilerOptions tcConfigB  = 
                let collect _name = ()
                let fsiCompilerOptions = Fscopts.GetCoreFsiCompilerOptions tcConfigB 
                ParseCompilerOptions collect fsiCompilerOptions (Array.toList otherFlags)
            let fas = LoadClosure.ComputeClosureOfSourceText(filename, source, CodeContext.Editing, useMonoResolution, useFsiAuxLib, new Lexhelp.LexResourceManager(), applyCompilerOptions)
            let allFlags = 
                [| yield "--noframework"; yield "--warn:3"; 
                   yield! otherFlags 
                   for r in fas.References do yield "-r:" + fst r
                   for (code,_) in fas.NoWarns do yield "--nowarn:" + code
                |]
            let co = 
                {
                    ProjectFileName = filename + ".fsproj" // Make a name that is unique in this directory.
                    ProjectFileNames = fas.SourceFiles |> List.map fst |> List.toArray
                    ProjectOptions = allFlags 
                    ReferencedProjects= [| |]  
                    IsIncompleteTypeCheckEnvironment = false
                    UseScriptResolutionRules = true 
                    LoadTime = loadedTimeStamp
                    UnresolvedReferences = Some (UnresolvedReferencesSet(fas.UnresolvedReferences))
                }
            scriptClosure.Put(co,fas) // Save the full load closure for later correlation.
            co)
            
    member bc.InvalidateConfiguration(options : ProjectOptions) =
        reactor.AsyncOp <| fun () -> 
            match incrementalBuildersCache.GetAvailable options with
            | None -> ()
            | Some (_oldBuilder, _, _) ->
                    // We do not need to decrement here - the onDiscard function is called each time an entry is pushed out of the build cache,
                    // including by SetAlternate.
                    let builderB, errorsB, decrementB = CreateOneIncrementalBuilder options
                    incrementalBuildersCache.SetAlternate(options, (builderB, errorsB, decrementB))
        bc.StartBuilding(options)

    member bc.NotifyProjectCleaned(options : ProjectOptions) =
        match incrementalBuildersCache.GetAvailable options with
        | None -> ()
        | Some (builderOpt, _, _) ->
#if EXTENSIONTYPING
            builderOpt |> Option.iter (fun builder -> 
                if builder.ThereAreLiveTypeProviders then
                    bc.InvalidateConfiguration(options))
#else
            ()
#endif

    member bc.InvalidateAll() =
        reactor.AsyncOp (fun () -> incrementalBuildersCache.Clear())

    member bc.StartBuilding(options) =
        reactor.StartBuilding(fun () -> 
            let builderOpt,_,_ = incrementalBuildersCache.Get(options)
            match builderOpt with 
            | None -> false
            | Some builder -> builder.Step())

    member bc.StopBuilding() =
        reactor.StopBuilding() 

    // This is for unit testing only
    member bc.WaitForBackgroundCompile() =
        reactor.WaitForBackgroundCompile() 

    member bc.ReactorOps  = reactorOps
    member bc.BeforeBackgroundFileCheck = beforeFileChecked.Publish
    member bc.FileParsed = fileParsed.Publish
    member bc.FileChecked = fileChecked.Publish
    member bc.ProjectChecked = projectChecked.Publish

//----------------------------------------------------------------------------
// InteractiveChecker
//


[<Sealed>]
[<AutoSerializable(false)>]
// There is typically only one instance of this type in a Visual Studio process.
type InteractiveChecker() =
    let backgroundCompiler = BackgroundCompiler()

    static let mutable foregroundParseCount = 0
    static let mutable foregroundTypeCheckCount = 0
    static let globalInstance = InteractiveChecker()
    
    /// Determine whether two sets of sources and parse options are the same.
    let AreSameForParsing((f1: string, s1: string, o1: ProjectOptions), (f2, s2, o2)) =
        (f1 = f2) 
        && ProjectOptions.AreSameProjectForParsing(o1,o2)
        && (s1 = s2)
        
    /// Determine whether two sets of sources and parse options should be subsumed under the same project.
    let AreSubsumableForParsing((_,_,o1:ProjectOptions),(_,_,o2:ProjectOptions)) =
        ProjectOptions.AreSameProjectName(o1,o2)
        
    // Parse using backgroundCompiler
    let ComputeBraceMatching(filename:string,source,options:ProjectOptions) = 
        backgroundCompiler.MatchBraces(filename,source,options)
    
    /// Parse a source code file, returning an information about the untyped results
    /// and the results needed for further processing using 'CheckFileInProject'
    let ComputeUntypedParse(filename: string, source, options) =
        foregroundParseCount <- foregroundParseCount + 1
        backgroundCompiler.ParseFileInProject(filename, source, options)

    // STATIC ROOT: LanguageServiceState.InteractiveChecker.braceMatchMru. Most recently used cache for brace matching. Accessed on the
    // background UI thread, not on the compiler thread.
    let braceMatchMru = MruCache<_,_>(braceMatchCacheSize,ComputeBraceMatching,areSame=AreSameForParsing,areSameForSubsumption=AreSubsumableForParsing,isStillValid=(fun _ -> true)) 

    // STATIC ROOT: LanguageServiceState.InteractiveChecker.untypedCheckMru. Most recently used cache for parsing files.
    let untypedCheckMru = MruCache<_, _>(untypedCheckMruSize, ComputeUntypedParse, areSame=AreSameForParsing,areSameForSubsumption=AreSubsumableForParsing,isStillValid=(fun _ -> true))

    // STATIC ROOT: LanguageServiceState.InteractiveChecker.typeCheckLookup 
    // STATIC ROOT: LanguageServiceState.InteractiveChecker.typeCheckLookup2
    //
    /// Cache which holds recently seen type-checks.
    /// This cache may hold out-of-date entries, in two senses
    ///    - there may be a more recent antecedent state available because the background build has made it available
    ///    - the source for the file may have changed
    
    let typeCheckLookup = 
        AgedLookup<string * ProjectOptions, ParseFileResults * CheckFileResults * int>
            (keepStrongly=recentForgroundTypeCheckLookupSize,
             areSame=fun (x,y)->x=y) 

    // Also keyed on source. This can only be out of date if the antecedent is out of date
    let typeCheckLookup2 = 
        AgedLookup<string * ProjectOptions * string, ParseFileResults * CheckFileResults * int>
            (keepStrongly=recentForgroundTypeCheckLookupSize,
             areSame=fun (x,y)->x=y) 

    /// Instantiate an interactive checker.    
    static member Create() = new InteractiveChecker()

    member ic.MatchBracesAlternate(filename, source, options) =
        braceMatchMru.Get((filename, source, options))

    member ic.ParseFileInProject(filename, source, options) =
        untypedCheckMru.Get((filename, source, options))
        
    member ic.GetBackgroundParseResultsForFileInProject (filename,options) =
        backgroundCompiler.GetBackgroundParseResultsForFileInProject(filename,options)
        
    member ic.GetBackgroundCheckResultsForFileInProject (filename,options) =
        backgroundCompiler.GetBackgroundCheckResultsForFileInProject(filename,options)
        
    /// Try to get recent approximate type check results for a file. 
    member ic.TryGetRecentTypeCheckResultsForFile(filename: string, options:ProjectOptions, ?source) =
        match source with 
        | Some sourceText -> typeCheckLookup2.TryGet((filename,options,sourceText)) 
        | None -> typeCheckLookup.TryGet((filename,options)) 

    /// This function is called when the entire environment is known to have changed for reasons not encoded in the ProjectOptions of any project/compilation.
    /// For example, the type provider approvals file may have changed.
    member ic.InvalidateAll() =
        backgroundCompiler.InvalidateAll()
            
    /// This function is called when the entire environment is known to have changed for reasons not encoded in the ProjectOptions of any project/compilation.
    /// For example, the type provider approvals file may have changed.
    //
    // This is for unit testing only
    member ic.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients() =
        ic.InvalidateAll()
        typeCheckLookup.Clear()
        typeCheckLookup2.Clear()
        braceMatchMru.Clear()
        untypedCheckMru.Clear()
        for i in 0 .. 2 do 
            System.GC.Collect()
            System.GC.WaitForPendingFinalizers() 
            backgroundCompiler.WaitForBackgroundCompile() // flush AsyncOp
            
    /// This function is called when the configuration is known to have changed for reasons not encoded in the ProjectOptions.
    /// For example, dependent references may have been deleted or created.
    member ic.InvalidateConfiguration(options: ProjectOptions) =
        backgroundCompiler.InvalidateConfiguration options

    /// This function is called when a project has been cleaned, and thus type providers should be refreshed.
    member ic.NotifyProjectCleaned(options: ProjectOptions) =
        backgroundCompiler.NotifyProjectCleaned options
              
    member ic.RecordTypeCheckFileInProjectResults(filename,options,parseResults,fileVersion,checkAnswer,source) =        
        match checkAnswer with 
        | None
        | Some CheckFileAnswer.Aborted -> 
            backgroundCompiler.StartBuilding(options) 
        | Some (CheckFileAnswer.Succeeded typedResults) -> 
            foregroundTypeCheckCount <- foregroundTypeCheckCount + 1
            typeCheckLookup.Put((filename,options),(parseResults,typedResults,fileVersion))            
            typeCheckLookup2.Put((filename,options,source),(parseResults,typedResults,fileVersion))            

    /// Typecheck a source code file, returning a handle to the results of the 
    /// parse including the reconstructed types in the file.
    member ic.CheckFileInProjectIfReady(parseResults:ParseFileResults, filename:string, fileVersion:int, source:string, options:ProjectOptions, ?isResultObsolete, ?textSnapshotInfo:obj) =        
        let (IsResultObsolete(isResultObsolete)) = defaultArg isResultObsolete (IsResultObsolete(fun _ -> false))
        let checkAnswer = backgroundCompiler.CheckFileInProjectIfReady(parseResults,filename,source,options,isResultObsolete,textSnapshotInfo)
        ic.RecordTypeCheckFileInProjectResults(filename,options,parseResults,fileVersion,checkAnswer,source)
        checkAnswer
            
    /// Typecheck a source code file, returning a handle to the results of the 
    /// parse including the reconstructed types in the file.
    member ic.CheckFileInProject(parseResults:ParseFileResults, filename:string, fileVersion:int, source:string, options:ProjectOptions, ?isResultObsolete, ?textSnapshotInfo:obj) =        
        let (IsResultObsolete(isResultObsolete)) = defaultArg isResultObsolete (IsResultObsolete(fun _ -> false))
        async {
            let! checkAnswer = backgroundCompiler.CheckFileInProject(parseResults,filename,source,options,isResultObsolete,textSnapshotInfo)
            ic.RecordTypeCheckFileInProjectResults(filename,options,parseResults,fileVersion,Some checkAnswer,source)
            return checkAnswer 
        }
            
    /// Typecheck a source code file, returning a handle to the results of the 
    /// parse including the reconstructed types in the file.
    member ic.ParseAndCheckFileInProject(filename:string, fileVersion:int, source:string, options:ProjectOptions, ?isResultObsolete, ?textSnapshotInfo:obj) =        
        let cachedResults = typeCheckLookup2.TryGet((filename,options,source)) 
        let (IsResultObsolete(isResultObsolete)) = defaultArg isResultObsolete (IsResultObsolete(fun _ -> false))
        async {
            let! parseResults, checkAnswer, usedCachedResults = backgroundCompiler.ParseAndCheckFileInProject(filename,source,options,isResultObsolete,textSnapshotInfo,cachedResults)
            if not usedCachedResults then 
                ic.RecordTypeCheckFileInProjectResults(filename,options,parseResults,fileVersion,Some checkAnswer,source)
            return (parseResults, checkAnswer)
        }
            
    member ic.ParseAndCheckProject(options) =
        backgroundCompiler.ParseAndCheckProject(options)

    /// For a given script file, get the ProjectOptions implied by the #load closure
    member ic.GetProjectOptionsFromScript(filename, source, ?loadedTimeStamp, ?otherFlags, ?useFsiAuxLib) :  ProjectOptions = 
        backgroundCompiler.GetProjectOptionsFromScript(filename,source,?loadedTimeStamp=loadedTimeStamp, ?otherFlags=otherFlags, ?useFsiAuxLib=useFsiAuxLib)
        
    member ic.GetProjectOptionsFromCommandLineArgs(projectFileName, argv, ?loadedTimeStamp) = 
        let loadedTimeStamp = defaultArg loadedTimeStamp DateTime.MaxValue // Not 'now', we don't want to force reloading
        { ProjectFileName = projectFileName
          ProjectFileNames = [| |] // the project file names will be inferred from the ProjectOptions
          ProjectOptions = argv 
          ReferencedProjects= [| |]  
          IsIncompleteTypeCheckEnvironment = false
          UseScriptResolutionRules = false
          LoadTime = loadedTimeStamp
          UnresolvedReferences = None }

    /// Begin background parsing the given project.
    member ic.StartBackgroundCompile(options) = backgroundCompiler.StartBuilding(options) 

    /// Stop the background compile.
    member ic.StopBackgroundCompile() = backgroundCompiler.StopBuilding()

    /// Block until the background compile finishes.
    //
    // This is for unit testing only
    member ic.WaitForBackgroundCompile() = backgroundCompiler.WaitForBackgroundCompile()

    // Publish the ReactorOps from the background compiler for internal use
    member ic.ReactorOps = backgroundCompiler.ReactorOps

    member ic.BeforeBackgroundFileCheck  = backgroundCompiler.BeforeBackgroundFileCheck
    member ic.FileParsed  = backgroundCompiler.FileParsed
    member ic.FileChecked  = backgroundCompiler.FileChecked
    member ic.ProjectChecked = backgroundCompiler.ProjectChecked

    static member GlobalForegroundParseCountStatistic = foregroundParseCount
    static member GlobalForegroundTypeCheckCountStatistic = foregroundTypeCheckCount
          
    // Obsolete
    member ic.MatchBraces(filename, source, options) =
        ic.MatchBracesAlternate(filename, source, options) |> Array.map (fun (a,b) -> Range.toZ a, Range.toZ b)

    member bc.UntypedParse(filename, source, options) = bc.ParseFileInProject(filename, source, options) |> Async.RunSynchronously
    member bc.TypeCheckSource(parseResults, filename, fileVersion, source, options, isResultObsolete, textSnapshotInfo:obj) = bc.CheckFileInProjectIfReady(parseResults, filename, fileVersion, source, options, isResultObsolete, textSnapshotInfo)
    member ic.GetCheckOptionsFromScriptRoot(filename, source, loadedTimeStamp) = ic.GetProjectOptionsFromScript(filename, source, loadedTimeStamp, [| |]) 
    member ic.GetCheckOptionsFromScriptRoot(filename, source, loadedTimeStamp, otherFlags) = ic.GetProjectOptionsFromScript(filename, source, loadedTimeStamp, otherFlags) 
    member ic.GetProjectOptionsFromScriptRoot(filename, source, ?loadedTimeStamp, ?otherFlags, ?useFsiAuxLib) = ic.GetProjectOptionsFromScript(filename, source, ?loadedTimeStamp=loadedTimeStamp, ?otherFlags=otherFlags, ?useFsiAuxLib=useFsiAuxLib)
    member ic.FileTypeCheckStateIsDirty  = backgroundCompiler.BeforeBackgroundFileCheck

    static member Instance = globalInstance

type FsiInteractiveChecker(reactorOps: IReactorOperations, tcConfig, tcGlobals, tcImports, tcState, loadClosure) =

    member __.ParseAndCheckInteraction (source) =

        let mainInputFileName = "stdin.fsx" 
        // Note: projectSourceFiles is only used to compute isLastCompiland, and is ignored if Build.IsScript(mainInputFileName) is true (which it is in this case).
        let projectSourceFiles = [ ]
        let parseErrors, _matchPairs, inputOpt, anyErrors = Parser.ParseOneFile (source, false, true, mainInputFileName, projectSourceFiles, tcConfig)
        let dependencyFiles = [] // interactions have no dependencies
        let parseResults = ParseFileResults(parseErrors, inputOpt, parseHadErrors = anyErrors, dependencyFiles = dependencyFiles)

        let backgroundErrors = []
        let tcErrors, tcFileResult = 
            Parser.TypeCheckOneFile(parseResults,source,mainInputFileName,"project",tcConfig,tcGlobals,tcImports,  tcState,
                                    loadClosure,backgroundErrors,reactorOps.SyncOp,(fun () -> true),(fun _ -> false),None)

        match tcFileResult with 
        | Parser.TypeCheckAborted.No scope ->
            let errors = [|  yield! parseErrors; yield! tcErrors |]
            let typeCheckResults = CheckFileResults (errors,Some scope, None, reactorOps)   
            let projectResults = CheckProjectResults (errors, Some(tcGlobals, tcImports, scope.ThisCcu, scope.CcuSig, [scope.ScopeResolutions], None, mkSimpleAssRef "stdin"), reactorOps)
            parseResults, typeCheckResults, projectResults
        | _ -> 
            failwith "unexpected aborted"
                
//----------------------------------------------------------------------------
// CompilerEnvironment, DebuggerEnvironment
//


/// Information about the compilation environment    
module CompilerEnvironment =
    /// These are the names of assemblies that should be referenced for .fs, .ml, .fsi, .mli files that
    /// are not asscociated with a project
    let DefaultReferencesForOrphanSources = DefaultBasicReferencesForOutOfProjectSources
    
    /// Publish compiler-flags parsing logic. Must be fast because its used by the colorizer.
    let GetCompilationDefinesForEditing(filename:string, compilerFlags : string list) =
        let defines = ref(SourceFileImpl.AdditionalDefinesForUseInEditor(filename))
        let MatchAndExtract(flag:string,prefix:string) =
            if flag.StartsWith(prefix) then 
                let sub = flag.Substring(prefix.Length)
                let trimmed = sub.Trim()
                defines := trimmed :: !defines
        let rec QuickParseDefines = function
            | hd :: tail ->
               MatchAndExtract(hd,"-d:")
               MatchAndExtract(hd,"--define:")
               QuickParseDefines tail
            | _ -> ()
        QuickParseDefines compilerFlags
        !defines
            
    /// Return true if this is a subcategory of error or warning message that the language service can emit
    let IsCheckerSupportedSubcategory(subcategory:string) =
        // Beware: This code logic is duplicated in DocumentTask.cs in the language service
        PhasedError.IsSubcategoryOfCompile(subcategory)

/// Information about the debugging environment
module DebuggerEnvironment =
    /// Return the language ID, which is the expression evaluator id that the
    /// debugger will use.
    let GetLanguageID() =
#if SILVERLIGHT
        System.Guid(0xAB4F38C9, 0xB6E6s, 0x43bas, 0xBEuy, 0x3Buy, 0x58uy, 0x08uy, 0x0Buy, 0x2Cuy, 0xCCuy, 0xE3uy)
#else
        System.Guid(0xAB4F38C9u, 0xB6E6us, 0x43baus, 0xBEuy, 0x3Buy, 0x58uy, 0x08uy, 0x0Buy, 0x2Cuy, 0xCCuy, 0xE3uy)
#endif
        
module PrettyNaming =
    let IsIdentifierPartCharacter     x = Microsoft.FSharp.Compiler.PrettyNaming.IsIdentifierPartCharacter x
    let IsLongIdentifierPartCharacter x = Microsoft.FSharp.Compiler.PrettyNaming.IsLongIdentifierPartCharacter x
    let GetLongNameFromString         x = Microsoft.FSharp.Compiler.PrettyNaming.SplitNamesForILPath x
    let FormatAndOtherOverloadsString remainingOverloads = FSComp.SR.typeInfoOtherOverloads(remainingOverloads)

//----------------------------------------------------------------------------
// Obsolete
//

        
[<Obsolete("This type has been renamed to MethodGroupItemParameter")>]
type Param = MethodGroupItemParameter

[<Obsolete("This type has been renamed to MethodGroupItem")>]
type Method = MethodGroupItem

[<Obsolete("This type has been renamed to ProjectOptions")>]
type CheckOptions = ProjectOptions

[<Obsolete("This type has been renamed to CheckFileAnswer")>]
type TypeCheckAnswer = CheckFileAnswer

[<Obsolete("This type has been renamed to CheckFileResults")>]
type TypeCheckResults = CheckFileResults

[<Obsolete("This type has been renamed to ParseFileResults")>]
type UntypedParseInfo = ParseFileResults

/// This file has become eligible to be re-typechecked.
[<Obsolete("NotifyFileTypeCheckStateIsDirty has been replaced by the FileTypeCheckStateIsDirty event on the InteractiveChecker type")>]
type NotifyFileTypeCheckStateIsDirty = NotifyFileTypeCheckStateIsDirty of (string -> unit)
        

#if EXTENSIBLE_DUMPER
#if DEBUG

namespace Internal.Utilities.Diagnostic
open Microsoft.FSharp.Compiler.Env
open Microsoft.FSharp.Compiler.Tastops 
open Microsoft.FSharp.Compiler.Infos
open Microsoft.FSharp.Compiler
open System.Text

type internal typDumper(dumpTarget:Microsoft.FSharp.Compiler.Tast.TType) =
    override self.ToString() = 
        match !global_g with
        | Some g -> 
            let denv = DisplayEnv.Empty g
            let sb = StringBuilder()
            NicePrint.outputTy denv sb dumpTarget
            sb.ToString()
        | None -> "No global environment"
    
#endif    
#endif
