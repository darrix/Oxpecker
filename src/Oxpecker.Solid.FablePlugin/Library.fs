﻿namespace Oxpecker.Solid

open System
open Fable
open Fable.AST
open Fable.AST.Fable

[<assembly: ScanForPlugins>]
do ()

module internal rec AST =
    type PropInfo = string * Expr
    type Props = PropInfo list



    type TagCall =
        | WithChildren of string * CallInfo * SourceLocation option
        | NoChildren of string * Expr list * SourceLocation option

    let (|TagConstructor|_|) (expr: Expr) =
        match expr with
        | Call (Import (importInfo, LambdaType (_, DeclaredType (typ, [])), _), _, _, range)
                when importInfo.Selector.EndsWith("_$ctor")
                && typ.FullName.StartsWith("Oxpecker.Solid") ->
            let tagName = typ.FullName.Split('.') |> Seq.last
            Some (tagName, range)
        | _ ->
            None

    let (|TagBuilder|_|) (expr: Expr) =
        match expr with
        | Call (Import (importInfo, _, _), callInfo, _, range)
                when importInfo.Selector.StartsWith("HtmlContainerExtensions_Run")  ->
            match callInfo.Args.Head with
            | TagConstructor (tagName, _)->
                Some (tagName, callInfo, range)
            | Let ({ Name = name }, TagConstructor (tagName, _), _)
                    when name.StartsWith("returnVal") ->
                Some (tagName, callInfo, range)
            | _ -> None
        | _ -> None

    let jsxElementType =
        Type.DeclaredType (
            ref =
                {
                    FullName = "Fable.Core.JSX.Element"
                    Path = EntityPath.CoreAssemblyName "Fable.Core"
                },
            genericArgs = []
        )

    let importJsxCreate =
        Import (
            info =
                {
                    Selector = "create"
                    Path = "@fable-org/fable-library-js/JSX.js"
                    Kind =
                        ImportKind.LibraryImport
                            {
                                IsInstanceMember = false
                                IsModuleMember = true
                            }
                },
            typ = Type.Any,
            range = None
        )

    let rec collectProps (seqExpr: Expr list) =
        match seqExpr with
        | [] -> []
        | Sequential expressions :: rest ->
            collectProps rest @ collectProps expressions
        | Call (Import (importInfo, _, _), callInfo, _, range) :: rest ->
            match importInfo.Kind with
            | ImportKind.MemberImport (MemberRef(_, memberRefInfo)) when memberRefInfo.CompiledName.StartsWith("HtmlTag.set_") ->
                let propName =
                    // TODO: handle specific tag properties
                    match memberRefInfo.CompiledName.Substring("HtmlTag.set_".Length) with
                    | "class'" -> "class"
                    | "type'" -> "type"
                    | name -> name
                let propValue = callInfo.Args.Head
                [(propName, propValue)]
            | _ ->
                []
        | _ :: rest ->
            collectProps rest

    let getProps (callInfo: CallInfo) tagName : Props =
        let setPropertiesSeq = callInfo.Args |> List.tryPick (
            function
            | Let ({ Name = name }, _, Sequential expr)
                when name.StartsWith("returnVal")
                -> Some expr
            | _ -> None
        )
        match setPropertiesSeq with
        | None -> []
        | Some seq -> collectProps seq

    let getChildren currentList (expr: Expr): Expr list =
        match expr with
        | Let ({ Name = name }, TagBuilder (tagName, callInfo, range), _)
                when name.StartsWith("element") ->
           let newExpr = handleTagCall <| TagCall.WithChildren (tagName, callInfo, range)
           newExpr :: currentList
        | Let ({ Name = name }, Let (_, TagConstructor (tagName, range), Sequential exprs), _)
                when name.StartsWith("element") ->
           let newExpr = handleTagCall <| TagCall.NoChildren (tagName, exprs, range)
           newExpr :: currentList
        | Let ({ Name = first }, Let ({ Name = element }, expr, _), Let ({ Name = second }, next, _))
                when first.StartsWith("first")
                     && second.StartsWith("second")
                     && element.StartsWith("element") ->
            match expr with
            | Let (_, TagConstructor (tagName, range), Sequential exprs) ->
                let newExpr = handleTagCall (TagCall.NoChildren (tagName, exprs, range))
                getChildren (newExpr :: currentList) next
            | TagConstructor (tagName, range) ->
                let newExpr = handleTagCall (TagCall.NoChildren (tagName, [], range))
                getChildren (newExpr :: currentList) next
            | TagBuilder callInfo ->
                let newExpr = handleTagCall (TagCall.WithChildren callInfo)
                getChildren (newExpr :: currentList) next
            | expr ->
                Console.WriteLine(expr)
                currentList
        | _ ->
            currentList

    let getText currentList (expr: Expr) : Expr list =
        match expr with
        | Lambda ({ Name = "txt" }, TypeCast (body, Unit) , None) ->
            body:: currentList
        | _ ->
            currentList

    let listItemType =
        Type.Tuple (genericArgs = [ Type.String ; Type.Any ], isStruct = false)
    let emptyList =
        Value (kind = NewList (headAndTail = None, typ = listItemType), range = None)

    let convertExprListToExpr (exprs: Expr list) =
        (emptyList, exprs)
        ||> List.fold (fun acc prop ->
            Value (
                kind = NewList (headAndTail = Some (prop, acc), typ = listItemType),
                range = None
            )
        )

    let handleTagCall (tagCall: TagCall) : Expr =
        let tagName, props, children, range =
            match tagCall with
            | WithChildren (tagName, callInfo, range) ->
                let props = getProps callInfo tagName
                let childrenList = callInfo.Args |> List.fold getChildren []
                let textList = callInfo.Args |> List.fold getText []
                let childrenList = childrenList @ textList
                tagName, props, childrenList, range
            | NoChildren (tagName, propList, range) ->
                let props = collectProps propList
                let childrenList = []
                tagName, props, childrenList, range

        let propsXs =
            props
            |> List.map (fun (name, expr) ->
                Value (
                    kind =
                        NewTuple (
                            values =
                                [
                                    // property name
                                    Value (kind = StringConstant name, range = None)
                                    // property value
                                    TypeCast (expr, Type.Any)
                                ],
                            isStruct = false
                        ),
                    range = None
                )
            )
        let childrenExpression = convertExprListToExpr children
        let childrenXs =
            Value (
                kind =
                    NewTuple (
                        values =
                            [
                                // property name
                                Value (kind = StringConstant "children", range = None)
                                // property value
                                TypeCast (childrenExpression, Type.Any)
                            ],
                        isStruct = false
                    ),
                range = None
            )
        let finalList = childrenXs :: propsXs
        let propsExpr = convertExprListToExpr finalList

        Call (
            callee = importJsxCreate,
            info =
                {
                    ThisArg = None
                    Args = [ Value(StringConstant tagName, None); propsExpr ]
                    SignatureArgTypes = []
                    GenericArgs = []
                    MemberRef = None
                    Tags = [ "jsx" ]
                },
            typ = jsxElementType,
            range = range
        )

    let transform (expr: Expr) =
        match expr with
        | Let (_, TagConstructor (tagName, range), Sequential exprs) ->
            handleTagCall (TagCall.NoChildren (tagName, exprs, range))
        | Let (name, value, expr) ->
            Let (name, value, (transform expr))
        | Sequential expressions ->
            // transform only the last expression
            Sequential (expressions |> List.mapi (
                fun i expr ->
                    if i = expressions.Length - 1 then
                        transform expr
                    else
                        expr
            ))
        | TagConstructor (tagName, range) ->
            handleTagCall (TagCall.NoChildren (tagName, [], range))
        | TagBuilder callInfo ->
            handleTagCall (TagCall.WithChildren callInfo)
        | _ ->
            expr


type SolidComponentAttribute() =
    inherit MemberDeclarationPluginAttribute()

    override _.FableMinimumVersion = "4.0"

    override this.Transform(compiler: PluginHelper, file: File, memberDecl: MemberDecl) =
        // Console.WriteLine("!Start! MemberDecl")
        // Console.WriteLine(memberDecl.Body)
        // Console.WriteLine("!End! MemberDecl")
        let newBody = AST.transform memberDecl.Body
        { memberDecl with Body = newBody }

    override _.TransformCall(_ph: PluginHelper, _mb: MemberFunctionOrValue, expr: Expr) : Expr = expr
