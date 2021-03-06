﻿//----------------------------------------------------------------------------
//
// Copyright (c) 2014
//
//    Ryan Riley (@panesofglass) and Andrew Cherry (@kolektiv)
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
//----------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module internal Freya.Router.Execution

open Aether
open Aether.Operators
open Arachne.Http
open Arachne.Uri
open Arachne.Uri.Template
open FParsec
open Freya.Core
open Freya.Core.Operators
open Freya.Lenses.Http
open Hekate

(* Types

   Types representing the potential outcome of a router execution,
   as well as the intermediate state tracked throughout a traversal
   of the compiled routing graph.

   We take a n-furcating exhaustive search over the routing space,
   closing edges as we go, producing a set of all possible matches and the
   data captured, and then select the highest precedence match. *)

(* Result *)

type private ExecutionResult =
    | Matched of UriTemplateData * FreyaPipeline
    | Unmatched

(* Traversal *)

type private Traversal =
    | Traversal of TraversalInvariant * TraversalState

    static member state_ =
        (fun (Traversal (_, s)) -> s), (fun s (Traversal (i, _)) -> Traversal (i, s))

 and private TraversalInvariant =
    | Invariant of Method

 and private TraversalState =
    | State of TraversalPosition * TraversalData

    static member position_ =
        (fun (State (p, _)) -> p), (fun p (State (_, d)) -> State (p, d))

    static member data_ =
        (fun (State (_, d)) -> d), (fun d (State (p, _)) -> State (p, d))

 and private TraversalPosition =
    | Position of string * Compilation.CompilationKey

    static member pathAndQuery_ =
        (fun (Position (p, _)) -> p), (fun p (Position (_, k)) -> Position (p, k))

    static member key_ =
        (fun (Position (_, k)) -> k), (fun k (Position (p, _)) -> Position (p, k))

 and private TraversalData =
    | Data of UriTemplateData

    static member data_ =
        (fun (Data d) -> d), (fun d (Data (_)) -> Data (d))

(* Constructors

   Construction functions for common types, in this case a simple default
   traversal, starting at the tree root and capturing no data, with
   a starting path, and an invariant method on which to match. *)

let private traversal meth path query =
    let pathAndQuery =
        match query with
        | "" -> path
        | query -> sprintf "%s?%s" path query

    Traversal (
        Invariant meth,
        State (
            Position (pathAndQuery, Compilation.Root),
            Data (UriTemplateData (Map.empty))))

(* Lenses

   Lenses in to aspects of the traversal, chiefly the traversal state
   elements, taking an immutable approach to descending state capture
   throughout the graph traversal. *)

(* Traversal *)

let private traversalData_ =
        Traversal.state_
    >-> TraversalState.data_
    >-> TraversalData.data_

let private traversalKey_ =
        Traversal.state_
    >-> TraversalState.position_
    >-> TraversalPosition.key_

let private traversalPathAndQuery_ =
        Traversal.state_
    >-> TraversalState.position_
    >-> TraversalPosition.pathAndQuery_

(* Patterns

   Patterns used to match varying states throughout the traversal process,
   beginning with the high level states that a traversal may occupy, i.e.
   working with a candidate match (when the path is exhausted) or working
   with a progression (the continuation of the current traversal).

   A pattern for matching paths against the current parser, with data captured
   and the result paths returned follows, before a filtering pattern to only
   return candidate endpoints which match the invariant method stored as part
   of the traversal. *)

(* Traversal *)

let private (|Candidate|_|) =
    function | Traversal (Invariant m, State (Position ("", k), Data d)) -> Some (k, m, d)
             | _ -> None

let private (|Progression|_|) =
    function | Traversal (Invariant _, State (Position (p, k), Data _)) -> Some (k, p)

let private (|Successors|_|) key (Compilation.Graph graph) =
    match Graph.successors key graph with
    | Some x -> Some x
    | _ -> None

(* Matching *)

let private (|Match|_|) parser pathAndQuery =
    match run parser pathAndQuery with
    | Success (data, _, p) -> Some (data, pathAndQuery.Substring (int p.Index))
    | _ -> None

(* Filtering *)

let private (|Endpoints|_|) key meth (Compilation.Graph graph) =
    match Graph.tryFindNode key graph with
    | Some (_, Compilation.Endpoints endpoints) ->
        endpoints
        |> List.filter (
           function | Compilation.Endpoint (_, All, _) -> true
                    | Compilation.Endpoint (_, Methods ms, _) when List.exists ((=) meth) ms -> true
                    | _ -> false)
        |> function | [] -> None
                    | endpoints -> Some endpoints
    | _ -> None

(* Traversal

   Traversal of the compiled routing graph, finding all matches for the
   path and method in the traversal state. The search is exhaustive, as a
   search which only finds the first match may not find the match which has
   the highest declared precendence.

   The exhaustive approach also allows for potential secondary selection
   strategies in addition to simple precedence selection in future. *)

let private emptyM =
    Freya.init []

let private foldM f xs state =
    List.foldBack (fun x (xs, state) ->
        Async.RunSynchronously (f x state) ||> fun x state ->
            (x :: xs, state)) xs ([], state)

let private  mapM f xs =
        foldM f xs <!> Freya.State.get
    >>= fun (xs, state) ->
                Freya.State.set state
             *> Freya.init xs

let rec private traverse graph traversal =
    match traversal with
    | Candidate (key, meth, data) ->
        match graph with
        | Endpoints key meth endpoints ->
            Freya.init (
                endpoints
                |> List.map (fun (Compilation.Endpoint (precedence, _, pipe)) ->
                    precedence, data, pipe))
        | _ ->
            emptyM
    | Progression (key, pathAndQuery) ->
        match graph with
        | Successors (key) successors ->
                List.concat
            <!> mapM (fun (key', Compilation.Edge parser) ->
                match pathAndQuery with
                | Match parser (data', pathAndQuery') ->
                    traversal
                    |> Optic.map traversalData_ ((+) data')
                    |> Optic.set traversalPathAndQuery_ pathAndQuery'
                    |> Optic.set traversalKey_ key'
                    |> traverse graph
                | _ ->
                    emptyM) successors
        | _ -> emptyM
    | _ -> emptyM

(* Selection

   Select the highest precedence data and pipeline pair from the given set of
   candidates, using the supplied precedence value. *)

let private select =
    function | [] ->
                Freya.init (
                    Unmatched)
             | endpoints ->
                Freya.init (
                    Matched (
                        endpoints
                        |> List.minBy (fun (precedence, _, _) -> precedence)
                        |> fun (_, data, pipe) -> data, pipe))

(* Search

   Combine a list of all possible route matches and associated captured data,
   produced by a traversal of the compiled routing graph, with a selection of
   the matched route (data and pipeline pair) with the highest precedence,
   as measured by the order in which the routes were declared in the compilation
   phase. *)

let private search graph =
        traversal <!> !. Request.method_ <*> !. Request.path_ <*> !. (Request.query_ >-> Query.raw_)
    >>= traverse graph
    >>= select

(* Execution

   Run a search on the routing graph. In the case of a match, write
   any captured data to the state to be interrogated later through
   the routing lenses, and return the value of executing the matched
   pipeline.

   In the case of a non-match, fall through to whatever follows the
   router instance. *)

let execute graph =
        search graph
    >>= function | Matched (data, pipe) -> (Route.data_ .= data) *> pipe
                 | Unmatched -> Freya.Pipeline.next