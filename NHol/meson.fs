﻿(*

Copyright 1998 University of Cambridge
Copyright 1998-2007 John Harrison
Copyright 2013 Jack Pappas, Eric Taucher

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

*)

#if USE
#else
/// First order automation: MESON (model elimination).
module NHol.meson

open FSharp.Compatibility.OCaml
open FSharp.Compatibility.OCaml.Num

open ExtCore.Control
open ExtCore.Control.Collections

open NHol
open system
open lib
open fusion
open fusion.Hol_kernel
open basics
open nets
open printer
open preterm
open parser
open equal
open bool
open drule
open tactics
open itab
open simp
open theorems
open ind_defs
open ``class``
open trivia
open canon
#endif

infof "Entering meson.fs"

(* ------------------------------------------------------------------------- *)
(* Some parameters controlling MESON behaviour.                              *)
(* ------------------------------------------------------------------------- *)

/// Make MESON's search algorithm work by proof depth rather than size.
let meson_depth = ref false   (* Use depth not inference bound.            *)

/// Makes MESON apply Plaisted's positive refinement.
let meson_prefine = ref true  (* Use Plaisted's positive refinement.       *)

/// Determines cut-in point for divide-and-conquer refinement in MESON.
let meson_dcutin = ref 1      (* Min size for d-and-c optimization cut-in. *)

/// Determines skew in MESON proof tree search limits.
let meson_skew = ref 3        (* Skew proof bias (one side is <= n / skew) *)

/// Makes MESON handle equations using Brand's transformation.
let meson_brand = ref false   (* Use Brand transformation                  *)

/// Limit initial case splits before MESON proper is applied.
let meson_split_limit = ref 8 (* Limit of case splits before MESON proper  *)

/// Make MESON's output more verbose and detailed.
let meson_chatty = ref false  (* Old-style verbose MESON output            *)

(* ------------------------------------------------------------------------- *)
(* Prolog exception.                                                         *)
(* ------------------------------------------------------------------------- *)

exception Cut

(* ------------------------------------------------------------------------- *)
(* Shadow syntax for FOL terms in NNF. Functions and predicates have         *)
(* numeric codes, and negation is done by negating the predicate code.       *)
(* ------------------------------------------------------------------------- *)

type fol_term = 
    | Fvar of int
    | Fnapp of int * fol_term list

type fol_atom = int * fol_term list

type fol_form = 
    | Atom of fol_atom
    | Conj of fol_form * fol_form
    | Disj of fol_form * fol_form
    | Forallq of int * fol_form

(* ------------------------------------------------------------------------- *)
(* Type for recording a MESON proof tree.                                    *)
(* ------------------------------------------------------------------------- *)

type fol_goal = 
    | Subgoal of fol_atom * fol_goal list * (int * Protected<thm0>) * int * (fol_term * int) list

(* ------------------------------------------------------------------------- *)
(* General MESON procedure, using assumptions and with settable limits.      *)
(* ------------------------------------------------------------------------- *)

/// Holds the internal values and private functions lifted out of GEN_MESON_TAC.
module internal GenMesonTac =
    let offinc = 10000
    let inferences = ref 0

    (* ----------------------------------------------------------------------- *)
    (* Like partition, but with short-circuiting for special situation.        *)
    (* ----------------------------------------------------------------------- *)

    let qpartition p m = 
        let rec qpartition l = 
            if l == m then None
            else 
                match l with
                | [] -> None
                | (h :: t) -> 
                    if p h then 
                        match qpartition t with
                        | Some (yes, no) ->
                            Some (h :: yes, no)
                        | None -> Some ([h], t)
                    else
                        qpartition t
                        |> Option.map (fun (yes, no) -> yes, h :: no)
        fun l ->
            qpartition l
            |> Option.fillWith (fun () -> [], l)

    (* ----------------------------------------------------------------------- *)
    (* Translate a term (in NNF) into the shadow syntax.                       *)
    (* ----------------------------------------------------------------------- *)

    let reset_vars, fol_of_var, hol_of_var = 
        let vstore = ref []
        let gstore = ref []
        let vcounter = ref 0

        let inc_vcounter() = 
            let n = !vcounter
            let m = n + 1
            if m >= offinc then 
                Choice.failwith "inc_vcounter: too many variables"
            else 
                vcounter := m
                Choice.result n

        let reset_vars() = 
            vstore := []
            gstore := []
            vcounter := 0

        let fol_of_var v = 
            choice {
                let currentvars = !vstore
                match assoc v currentvars with
                | Some x -> 
                    return x
                | None ->
                    let! n = inc_vcounter()
                    vstore := (v, n) :: currentvars
                    return n
            }

        let hol_of_var v =
            match rev_assoc v !vstore with
            | Some x -> Choice.result x
            | None ->
                rev_assoc v !gstore
                |> Option.toChoiceWithError "find"

        let hol_of_bumped_var v = 
            hol_of_var v            
            |> Choice.bindError (function
                | Failure _ ->
                    choice {
                        let v' = v % offinc
                        let! hv' = hol_of_var v'
                        let! gv = Choice.map genvar (type_of hv')
                        gstore := (gv, v) :: (!gstore)
                        return gv
                    }
                | e -> Choice.error e)

        reset_vars, fol_of_var, hol_of_bumped_var

    let reset_consts, fol_of_const, hol_of_const = 
        let false_tm = (parse_term @"F")
        let cstore = ref([] : (term * int) list)
        let ccounter = ref 2

        let reset_consts() = 
            cstore := [false_tm, 1]
            ccounter := 2

        let fol_of_const c = 
            let currentconsts = !cstore
            match assoc c currentconsts with
            | Some x -> x
            | None ->
                let n = !ccounter
                ccounter := n + 1
                cstore := (c, n) :: currentconsts
                n

        let hol_of_const c =
            rev_assoc c !cstore
            |> Option.toChoiceWithError "find"

        reset_consts, fol_of_const, hol_of_const

    let rec fol_of_term env consts tm = 
        choice {
            if is_var tm && not(mem tm consts) then
                let! fov = fol_of_var tm
                return Fvar fov
            else 
                let f, args = strip_comb tm
                if mem f env then 
                    return! Choice.failwith "fol_of_term: higher order"
                else 
                    let ff = fol_of_const f
                    let! fts = Choice.List.map (fol_of_term env consts) args
                    return Fnapp(ff, fts)
        }

    let fol_of_atom env consts tm = 
        choice {
            let f, args = strip_comb tm
            if mem f env then 
                return! Choice.failwith "fol_of_atom: higher order"
            else 
                let ff = fol_of_const f
                let! fts = Choice.List.map (fol_of_term env consts) args
                return ff, fts
        }

    let fol_of_literal env consts tm = 
        choice { 
            let! tm' = dest_neg tm
            let! p, a = fol_of_atom env consts tm'
            return -p, a
        }
        |> Choice.bindError (function 
            | Failure _ -> fol_of_atom env consts tm 
            | e -> Choice.error e)

    let rec fol_of_form env consts tm = 
        choice { 
            let! v, bod = dest_forall tm
            let! fv = fol_of_var v
            let! fbod = fol_of_form (v :: env) (subtract consts [v]) bod
            return Forallq(fv, fbod)
        }
        |> Choice.bindError (function
            | Failure _ -> 
                choice { 
                    let! l, r = dest_conj tm
                    let! fl = fol_of_form env consts l
                    let! fr = fol_of_form env consts r
                    return Conj(fl, fr)
                }
                |> Choice.bindError (function
                    | Failure _ ->
                        choice { 
                            let! l, r = dest_disj tm
                            let! fl = fol_of_form env consts l
                            let! fr = fol_of_form env consts r
                            return Disj(fl, fr)
                        }
                        |> Choice.bindError (function 
                            | Failure _ -> 
                                choice {
                                    let! atm = fol_of_literal env consts tm
                                    return Atom atm
                                } 
                            | e -> Choice.error e)
                    | e -> Choice.error e)
            | e -> Choice.error e)

    (* ----------------------------------------------------------------------- *)
    (* Further translation functions for HOL formulas.                         *)
    (* ----------------------------------------------------------------------- *)

    let rec hol_of_term tm = 
        choice {
            match tm with
            | Fvar v -> 
                return! hol_of_var v
            | Fnapp(f, args) -> 
                let! hc = hol_of_const f
                let! hts = Choice.List.map hol_of_term args
                return! list_mk_comb(hc, hts)
        }

    let hol_of_atom(p, args) = 
        choice {
            let! hc = hol_of_const p
            let! hts = Choice.List.map hol_of_term args
            return! list_mk_comb(hc, hts)
        }

    let hol_of_literal(p, args) = 
        choice {
            if p < 0 then 
                let! tm = hol_of_atom(-p, args)
                return! mk_neg tm
            else 
                return! hol_of_atom (p, args)
        }

    (* ----------------------------------------------------------------------- *)
    (* Versions of shadow syntax operations with variable bumping.             *)
    (* ----------------------------------------------------------------------- *)

    let rec fol_free_in v tm = 
        match tm with
        | Fvar x -> x = v
        | Fnapp(_, lis) -> exists (fol_free_in v) lis

    let rec fol_subst theta tm = 
        match tm with
        | Fvar v -> rev_assocd v theta tm
        | Fnapp(f, args) -> 
            let args' = qmap (fol_subst theta) args
            if args' == args then tm
            else Fnapp(f, args')

    let fol_inst theta ((p, args) as at : fol_atom) = 
        let args' = qmap (fol_subst theta) args
        if args' == args then at
        else p, args'

    let rec fol_subst_bump offset theta tm = 
        match tm with
        | Fvar v -> 
            if v < offinc then 
                let v' = v + offset
                rev_assocd v' theta (Fvar(v'))
            else rev_assocd v theta tm
        | Fnapp(f, args) -> 
            let args' = qmap (fol_subst_bump offset theta) args
            if args' == args then tm
            else Fnapp(f, args')

    let fol_inst_bump offset theta ((p, args) as at : fol_atom) = 
        let args' = qmap (fol_subst_bump offset theta) args
        if args' == args then at
        else p, args'

    (* ----------------------------------------------------------------------- *)
    (* Main unification function, maintaining a "graph" instantiation.         *)
    (* We implicitly apply an offset to variables in the second term, so this  *)
    (* is not symmetric between the arguments.                                 *)
    (* ----------------------------------------------------------------------- *)

    let rec istriv env x t = 
        choice {
            match t with
            | Fvar y -> 
                if y = x then
                    return true
                else
                    return! 
                        choice { 
                            let! t' =
                                rev_assoc y env
                                |> Option.toChoiceWithError "find"
                            return! istriv env x t'
                        }
                        // We only recover on a specific kind of error
                        |> Choice.bindError (function Failure "find" -> Choice.result false | e -> Choice.error e)

            | Fnapp(_, args) -> 
                let! b = Choice.List.exists (istriv env x) args 
                if b then 
                    return! Choice.failwith "cyclic"
                else
                    return false
        }

    let rec fol_unify offset tm1 tm2 sofar = 
        choice {
            match tm1, tm2 with
            | Fnapp(f, fargs), Fnapp(g, gargs) -> 
                if f <> g then 
                    return! Choice.failwith ""
                else
                    return! Choice.List.foldBack2 (fol_unify offset) fargs gargs sofar
            | _, Fvar(x) -> 
               let x' = x + offset
               return!
                 choice { 
                     let! tm2' = rev_assoc x' sofar |> Option.toChoiceWithError "find"
                     return! fol_unify 0 tm1 tm2' sofar
                 }
                 |> Choice.bindError (function 
                        | Failure "find" -> 
                            choice {
                                let! b = istriv sofar x' tm1
                                if b then 
                                    return sofar 
                                else 
                                    return (tm1, x') :: sofar
                            }
                        | e -> Choice.error e)
            | Fvar(x), _ ->
               return!  
                 choice { 
                     let! tm1' = rev_assoc x sofar |> Option.toChoiceWithError "find"
                     return! fol_unify offset tm1' tm2 sofar
                 }
                 |> Choice.bindError (function 
                        | Failure "find" -> 
                            choice {
                                let tm2' = fol_subst_bump offset [] tm2
                                let! b = istriv sofar x tm2'
                                if b then 
                                    return sofar 
                                else 
                                    return (tm2', x) :: sofar
                            }
                        | e -> Choice.error e)
        }

    (* ----------------------------------------------------------------------- *)
    (* Test for equality under the pending instantiations.                     *)
    (* ----------------------------------------------------------------------- *)

    let rec fol_eq insts tm1 tm2 =
        tm1 == tm2 ||
        match tm1, tm2 with
        | Fnapp(f, fargs), Fnapp(g, gargs) ->
            f = g && forall2 (fol_eq insts) fargs gargs
        | _, Fvar(x) ->
            match rev_assoc x insts with
            | Some tm2' ->
                 fol_eq insts tm1 tm2'
            | None ->
                 istriv insts x tm1
                 |> Choice.fill false

        | Fvar(x), _ ->
            match rev_assoc x insts with
            | Some tm1' ->
                 fol_eq insts tm1' tm2
            | None ->
                 istriv insts x tm2
                 |> Choice.fill false

    let fol_atom_eq insts (p1, args1) (p2, args2) =
        p1 = p2 && forall2 (fol_eq insts) args1 args2

    (* ----------------------------------------------------------------------- *)
    (* Cacheing continuations. Very crude, but it works remarkably well.       *)
    (* ----------------------------------------------------------------------- *)

    let cacheconts f =
        let cacheconts_memory : ('a * ('b * 'c * 'd)) list ref = ref []
        fun (_, (insts, _, size) as input) ->
            if exists (fun (_, (insts', _, size')) -> insts = insts' && (size <= size' || !meson_depth)) !cacheconts_memory then
                Choice.error (Choice.failwith "cacheconts")
            else 
                cacheconts_memory := input :: !cacheconts_memory
                f input

    (* ----------------------------------------------------------------------- *)
    (* Check ancestor list for repetition.                                     *)
    (* ----------------------------------------------------------------------- *)

    let checkan insts (p, a) ancestors =         
        let p' = -p
        let t' = (p', a)
        choice { 
            let! ours = assoc p' ancestors |> Option.toChoiceWithError "find"
            if exists (fun u -> fol_atom_eq insts t' (snd(fst u))) ours then 
                return! Choice.failwith "checkan"
            else 
                return ancestors
        }
        |> Choice.bindError (function Failure "find" -> Choice.result ancestors | e -> Choice.error e)

    (* ----------------------------------------------------------------------- *)
    (* Insert new goal's negation in ancestor clause, given refinement.        *)
    (* ----------------------------------------------------------------------- *)

    let insertan insts (p, a) ancestors = 
        let p' = -p
        let t' = (p', a)
        let ourancp, otheranc = 
            remove (fun (pr, _) -> pr = p') ancestors 
            |> Option.fill((p', []), ancestors)
        let ouranc = snd ourancp
        if exists (fun u -> fol_atom_eq insts t' (snd(fst u))) ouranc then 
            Choice.failwith "insertan: loop"
        else 
            Choice.result ((p', (([], t'), (0, TRUTH)) :: ouranc) :: otheranc)

    (* ----------------------------------------------------------------------- *)
    (* Apply a multi-level "graph" instantiation.                              *)
    (* ----------------------------------------------------------------------- *)

    let rec fol_subst_partial insts tm = 
        match tm with
        | Fvar v ->
            rev_assoc v insts
            |> Option.map (fun t ->
                fol_subst_partial insts t)
            |> Option.fill tm
        | Fnapp(f, args) -> 
            Fnapp(f, map (fol_subst_partial insts) args)

    (* ----------------------------------------------------------------------- *)
    (* Tease apart local and global instantiations.                            *)
    (* At the moment we also force a full evaluation; should eliminate this.   *)
    (* ----------------------------------------------------------------------- *)

    let separate_insts offset oldinsts newinsts = 
        let locins, globins = qpartition (fun (_, v) -> offset <= v) oldinsts newinsts
        if globins = oldinsts then 
            map (fun (t, x) -> fol_subst_partial newinsts t, x) locins, oldinsts
        else 
            map (fun (t, x) -> fol_subst_partial newinsts t, x) locins, 
            map (fun (t, x) -> fol_subst_partial newinsts t, x) globins

    (* ----------------------------------------------------------------------- *)
    (* Perform basic MESON expansion.                                          *)
    (* ----------------------------------------------------------------------- *)

    let meson_single_expand loffset rule ((g, ancestors), (insts, offset, size)) = 
        choice {
            let (hyps, conc), _ = rule
            // Convert from rev_itlist2
            let! allins = Choice.List.fold2 (fun acc x y -> fol_unify loffset x y acc) insts (snd g) (snd conc)
            let locin, globin = separate_insts offset insts allins
            let mk_ihyp h = 
                choice {
                    let h' = fol_inst_bump offset locin h
                    let! fts = checkan insts h' ancestors
                    return h', fts
                }
            let! newhyps = Choice.List.map mk_ihyp hyps
            inferences := !inferences + 1
            return newhyps, (globin, offset + offinc, size - length hyps)
        }

    (* ----------------------------------------------------------------------- *)
    (* Perform first basic expansion which allows continuation call.           *)
    (* ----------------------------------------------------------------------- *)

    type Protected3<'T> = Choice<'T, Choice<exn, exn>>

    let meson_expand_cont loffset rules state (cont : _ -> _ -> Protected3<_>) : Protected3<_> = 
        Choice.List.tryPick (fun r -> 
            choice {
                match meson_single_expand loffset r state with
                | Success r' ->
                    match cont (snd r) r' with
                    | Success r ->
                        return Some r
                    | Error e ->
                        return! Choice.error e
                | Error _ ->
                    return None
            }) rules
        |> function
            | Success (Some r) ->
                Choice.result r
            | Success None ->
                Choice.error (Choice.failwith "find")
            | Error e ->
                Choice.error e

    (* ----------------------------------------------------------------------- *)
    (* Try expansion and continuation call with ancestor or initial rule.      *)
    (* ----------------------------------------------------------------------- *)

    let meson_expand rules ((g, ancestors), ((insts, offset, size) as tup)) cont : Protected3<_> = 
        choice { 
            let pr = fst g
            match insertan insts g ancestors with
            | Success newancestors ->
                let newstate = (g, newancestors), tup
                let! v =
                    choice { 
                        if !meson_prefine && pr > 0 then 
                            return! Choice.error (Choice.failwith "meson_expand")
                        else 
                            let arules = assoc pr ancestors |> Option.toChoiceWithError "find"
                            match arules with
                            | Success arules ->
                                match meson_expand_cont 0 arules newstate cont with
                                | Success r -> 
                                    return r
                                | Error _ ->
                                    return! Choice.error (Choice.failwith "meson_expand")
                            | Error _ ->                    
                                return! Choice.error (Choice.failwith "meson_expand")
                    }
                    |> function
                        | Success r ->
                            Choice.result r
                        // Cut exception
                        | Error (Success _) ->
                            Choice.error (Choice.failwith "meson_expand")
                        | Error _ -> 
                            choice { 
                                let res = assoc pr rules |> Option.toChoiceWithError "find"
                                match res with
                                | Success res ->
                                    let crules = filter (fun ((h, _), _) -> length h <= size) res
                                    match meson_expand_cont offset crules newstate cont with
                                    | Success r ->
                                        return r
                                    | Error _ ->
                                        return! Choice.error (Choice.failwith "meson_expand")
                                | Error _ ->
                                    return! Choice.error (Choice.failwith "meson_expand")  
                            }
                            |> function
                                | Success r ->
                                    Choice.result r
                                | Error _ ->
                                    Choice.error (Choice.failwith "meson_expand")
                return v 
            | Error _ -> 
                return! Choice.error (Choice.failwith "meson_expand")
        }

    (* ----------------------------------------------------------------------- *)
    (* Simple Prolog engine organizing search and backtracking.                *)
    (* ----------------------------------------------------------------------- *)

    // NOTE: we change cont to Choice<'T, Choice<exn, exn>> where the second case is for Cut exceptions
    let expand_goal rules = 
        let rec expand_goal depth ((g, _), (insts, offset, size) as state) (cont : _ -> Protected3<_>) = 
            if depth < 0 then
                Choice.failwith "expand_goal: too deep"
            else
                (meson_expand rules state <| fun apprule (_, (pinsts, _, _) as newstate) -> 
                    expand_goals (depth - 1) newstate (cacheconts <| fun (gs, (newinsts, newoffset, newsize)) -> 
                        let locin, globin = separate_insts offset pinsts newinsts
                        let g' = Subgoal(g, gs, apprule, offset, locin)
                        if globin = insts && List.isEmpty gs then 
                            cont(g', (globin, newoffset, size))
                            |> function
                                | Success r -> Choice.result r
                                | Error (Error (Failure _)) ->
                                    Choice.error (Choice.result Cut)
                                | Error e -> Choice.error e
                        else 
                            cont(g', (globin, newoffset, newsize))
                            |> function
                                | Success r -> Choice.result r
                                | Error _ ->
                                    Choice.error (Choice.failwith "expand_goal")
                    ))
                 |> function
                    | Success r -> Choice.result r
                    | Error (Success e | Error e) ->
                        Choice.error e

        and expand_goals depth (gl, (insts, offset, size as tup)) cont : Protected3<_> = 
            match gl with
            | [] ->
                cont ([], tup)
            | [g] ->
                expand_goal depth (g, tup) (fun (g', stup) -> cont([g'], stup))
                |> function
                    | Success r -> Choice.result r 
                    | Error e when (e :? Cut) -> 
                        Choice.error (Choice.result e)
                    | Error e -> 
                        Choice.error (Choice.error e)
            | gl -> 
                if size >= !meson_dcutin then 
                    let lsize = size / !meson_skew
                    let rsize = size - lsize
                    let lgoals, rgoals = chop_list (length gl / 2) gl

                    expand_goals depth (lgoals, (insts, offset, lsize)) 
                        (cacheconts
                                (fun (lg', (i, off, n)) -> 
                                    expand_goals depth (rgoals, (i, off, n + rsize)) 
                                        (cacheconts(fun (rg', ztup) -> cont(lg' @ rg', ztup)))))
                    |> function
                        | Success r ->
                            Choice.result r
                        | Error (Error (Failure _)) -> 
                            expand_goals depth (rgoals, (insts, offset, lsize)) 
                                (cacheconts(fun (rg', (i, off, n)) -> 
                                         expand_goals depth (lgoals, (i, off, n + rsize)) (cacheconts(fun (lg', ((_, _, fsize) as ztup)) -> 
                                            if n + rsize <= lsize + fsize then 
                                                Choice.error (Choice.failwith "repetition of demigoal pair")
                                            else cont(lg' @ rg', ztup)))))
                        | Error e ->
                            Choice.error e
                else 
                    match gl with
                    | g :: gs ->
                        expand_goal depth (g, tup)
                            (cacheconts
                                 (fun (g', stup) ->
                                    expand_goals depth (gs, stup) (cacheconts(fun (gs', ftup) -> cont(g' :: gs', ftup)))))
                        |> function
                            | Success r -> Choice.result r 
                            | Error e when (e :? Cut) -> 
                                Choice.error (Choice.result e)
                            | Error e -> 
                                Choice.error (Choice.error e)
                    | _ ->
                        Choice.error (Choice.failwith "expand_goals: Unhandled case")
        
        fun g maxdep maxinf cont ->
            expand_goal maxdep (g, ([], 2 * offinc, maxinf)) cont

    (* ----------------------------------------------------------------------- *)
    (* With iterative deepening of inferences or depth.                        *)
    (* ----------------------------------------------------------------------- *)

    let solve_goal rules incdepth min max incsize =
        let rec solve n g =
            if n > max then
                Choice.failwith "solve_goal: Too deep"
            else
                if !verbose then
                    if !meson_chatty then
                        Format.print_string ((string !inferences) + " inferences so far. " + "Searching with maximum size " + (string n) + ".")
                        Format.print_newline ()
                
                    Format.print_string ((string !inferences) + "..")
                    Format.print_flush ()
                 
                let gi =
                    if incdepth then
                        expand_goal rules g n 100000 Choice.result
                    else
                        expand_goal rules g 100000 n Choice.result

                if !verbose then
                    if !meson_chatty then
                        Format.print_string ("Goal solved with " + (string !inferences) + " inferences.")
                        Format.print_newline ()
                
                    Format.print_string ("solved at " + string !inferences)
                    Format.print_newline ()

                // NOTE : We manually pattern-match on the value of 'gi' here; calling Choice.bindError
                // to continue recursing would make this function non-tail-recursive.
                match gi with
                | Success _ as result ->
                    result
                | Error _ ->
                    solve (n + incsize) g

        fun g ->
            solve min (g, [])

    (* ----------------------------------------------------------------------- *)
    (* Creation of tagged contrapositives from a HOL clause.                   *)
    (* This includes any possible support clauses (1 = falsity).               *)
    (* The rules are partitioned into association lists.                       *)
    (* ----------------------------------------------------------------------- *)

    let fol_of_hol_clauses =
        let eqt (a1, (b1, c1)) (a2, (b2, c2)) =
            ((a1 = a2) && (b1 = b2) && (equals_thm c1 c2))

        let mk_negated(p, a) = -p, a

        let rec mk_contraposes n th used unused sofar =
            match unused with
            | [] -> sofar
            | h :: t ->
                let nw = (map mk_negated (used @ t), h), (n, th)
                mk_contraposes (n + 1) th (used @ [h]) t (nw :: sofar)

        let fol_of_hol_clause th =
            choice {
                let! th = th
                let lconsts = freesl <| hyp th
                let tm = concl th
                let hlits = disjuncts tm
                let! flits = Choice.List.map (fol_of_literal [] lconsts) hlits
                let basics = mk_contraposes 0 (Choice.result th) [] flits []
                if forall (fun (p, _) -> p < 0) flits then
                    return ((map mk_negated flits, (1, [])), (-1, Choice.result th)) :: basics
                else
                    return basics
            }

        fun thms ->
            let rawrules = itlist (union' eqt << Choice.get << fol_of_hol_clause) thms []
            let prs = setify(map (fst << snd << fst) rawrules)
            let prules = map (fun t -> t, filter ((=) t << fst << snd << fst) rawrules) prs
            let srules = sort (fun (p, _) (q, _) -> abs(p) <= abs(q)) prules
            srules

    (* ----------------------------------------------------------------------- *)
    (* Optimize set of clauses; changing literal order complicates HOL stuff.  *)
    (* ----------------------------------------------------------------------- *)

    let optimize_rules =
        let optimize_clause_order cls =
            sort (fun ((l1, _), _) ((l2, _), _) -> length l1 <= length l2) cls
        map(fun (a, b) -> a, optimize_clause_order b)

    (* ----------------------------------------------------------------------- *)
    (* Create a HOL contrapositive on demand, with a cache.                    *)
    (* ----------------------------------------------------------------------- *)

    let clear_contrapos_cache, make_hol_contrapos =
        let DISJ_AC = AC DISJ_ACI
        let imp_CONV = REWR_CONV(TAUT(parse_term @"a \/ b <=> ~b ==> a"))
        let push_CONV =
            GEN_REWRITE_CONV TOP_SWEEP_CONV [TAUT (parse_term @"~(a \/ b) <=> ~a /\ ~b");
                                             TAUT (parse_term @"~(~a) <=> a")]
        let pull_CONV =
            GEN_REWRITE_CONV DEPTH_CONV 
                [TAUT(parse_term @"~a \/ ~b <=> ~(a /\ b)")]

        let imf_CONV = REWR_CONV(TAUT(parse_term @"~p <=> p ==> F"))
        let memory = ref []
        let clear_contrapos_cache() = memory := []

        let make_hol_contrapos(n, th) = 
            choice {
                let! th = th
                let tm = concl th
                let key = (n, tm)
                match assoc key !memory with
                | Some th' -> return! th'
                | None ->
                    if n < 0 then 
                        return! CONV_RULE (pull_CONV
                                           |> THENC <| imf_CONV) (Choice.result th)
                    else 
                        let djs = disjuncts tm
                        let! acth =
                            choice { 
                                if n = 0 then 
                                    return th
                                else 
                                    let ldjs, rdjs = chop_list n djs
                                    let ndjs = (hd rdjs) :: (ldjs @ (tl rdjs))
                                    let! tm0 = list_mk_disj ndjs
                                    let! tm1 = mk_eq(tm, tm0)
                                    return! EQ_MP (DISJ_AC tm1) (Choice.result th)
                            }
                        let! fth = 
                            if length djs = 1 then 
                                Choice.result acth
                            else CONV_RULE (imp_CONV
                                            |> THENC <| push_CONV) (Choice.result acth)
                        memory := (key, Choice.result fth) :: (!memory)
                        return fth
            }
        clear_contrapos_cache, make_hol_contrapos

    (* ----------------------------------------------------------------------- *)
    (* Translate back the saved proof into HOL.                                *)
    (* ----------------------------------------------------------------------- *)

    let meson_to_hol = 
        let hol_negate tm = 
            dest_neg tm
            |> Choice.bindError (function Failure _ -> mk_neg tm | e -> Choice.error e)

        let merge_inst (t, x) current = (fol_subst current t, x) :: current

        let finish_RULE = 
            GEN_REWRITE_RULE I [TAUT(parse_term @"(~p ==> p) <=> p");
                                TAUT(parse_term @"(p ==> ~p) <=> ~p")]

        let rec meson_to_hol insts (Subgoal(g, gs, (n, th), _, locin)) = 
            choice {
                let newins = itlist merge_inst locin insts
                let g' = fol_inst newins g
                let! hol_g = hol_of_literal g'
                let ths = map (meson_to_hol newins) gs
                let! hth = 
                    choice {
                        if equals_thm th TRUTH then 
                            return! ASSUME hol_g
                        else 
                            let! cth = make_hol_contrapos(n, th)
                            if ths = [] then 
                                return cth
                            else 
                                return! MATCH_MP (Choice.result cth) (end_itlist CONJ ths)
                    }
                let! ith = PART_MATCH Choice.result (Choice.result hth) hol_g
                let! tm1 = hol_negate <| concl ith
                return! finish_RULE (DISCH tm1 (Choice.result ith))
            }
        meson_to_hol

    (* ----------------------------------------------------------------------- *)
    (* Create equality axioms for all the function and predicate symbols in    *)
    (* a HOL term. Not very efficient (but then neither is throwing them into  *)
    (* automated proof search!)                                                *)
    (* ----------------------------------------------------------------------- *)

    let create_equality_axioms = 
        let eq_thms = 
            (CONJUNCTS << prove)((parse_term @"(x:A = x) /\
        (~(x:A = y) \/ ~(x = z) \/ (y = z))"), REWRITE_TAC []
                                               |> THEN <| ASM_CASES_TAC (parse_term @"x:A = y")
                                               |> THEN <| ASM_REWRITE_TAC []
                                               |> THEN <| CONV_TAC TAUT)

        let imp_elim_CONV = REWR_CONV(TAUT(parse_term @"(a ==> b) <=> ~a \/ b"))
        let eq_elim_RULE = MATCH_MP(TAUT(parse_term @"(a <=> b) ==> b \/ ~a"))
        let veq_tm = (Choice.bind rator << Choice.bind rator << Choice.map concl) (hd eq_thms)

        let create_equivalence_axioms(eq, _) = 
            choice {
                let! ty1 = Choice.bind type_of veq_tm
                let! ty2 = type_of eq  
                let! tyins = type_match ty1 ty2 []
                return map (INST_TYPE tyins) eq_thms
            }

        let rec tm_consts tm acc = 
            let fn, args = strip_comb tm
            if args = [] then acc
            else itlist tm_consts args (insert (fn, length args) acc)

        (* OPTIMIZE :   Modify the code below to use option instead of try/catch. *)
        let rec fm_consts tm ((preds, funs) as acc) = 
            choice {
                let! (_, tm1) = dest_forall tm
                return! fm_consts tm1 acc
            }
            |> Choice.bindError (fun _ ->
                choice {
                    let! (_, tm1) = dest_exists tm 
                    return! fm_consts tm1 acc
                }
                |> Choice.bindError (fun _ ->
                    choice { 
                        let! l, r = dest_conj tm
                        let! r' = fm_consts r acc
                        return! fm_consts l r'
                    }
                    |> Choice.bindError (fun _ ->
                        choice { 
                            let! l, r = dest_disj tm
                            let! r' = fm_consts r acc
                            return! fm_consts l r'
                        }
                        |> Choice.bindError (fun _ ->
                            choice { 
                                let! l, r = dest_imp tm
                                let! r' = fm_consts r acc
                                return! fm_consts l r'
                            }
                            |> Choice.bindError (fun _ ->
                                choice { 
                                    let! tm1 = dest_neg tm
                                    return! fm_consts tm1 acc
                                }
                                |> Choice.bindError (fun _ ->
                                    choice { 
                                        let! l, r = dest_eq tm
                                        let! ty1 = type_of l
                                        if ty1 = bool_ty then
                                            let! l' = fm_consts l acc
                                            return! fm_consts r l'
                                        else 
                                            return! Choice.failwith "atomic equality"
                                    }
                                    |> Choice.bindError (fun _ ->
                                        let pred, args = strip_comb tm
                                        if args = [] then 
                                            Choice.result acc
                                        else
                                            Choice.result(insert (pred, length args) preds, itlist tm_consts args funs))))))))

        let create_congruence_axiom pflag (tm, len) = 
            choice {
                let! ty1 = type_of tm
                let atys, _ = 
                    splitlist (fun ty -> 
                        choice {
                            let! op, l = dest_type ty
                            if op = "fun" then 
                                return (hd l, hd(tl l))
                            else
                                return! Choice.fail()
                        } |> Choice.toOption) ty1

                let ctys = fst(chop_list len atys)
                let largs = map genvar ctys
                let rargs = map genvar ctys
                let! th0 = REFL tm
                let! tms = Choice.List.map (Choice.bind ASSUME << mk_eq) (zip largs rargs)
                let! th1 = Choice.List.fold (fun acc x -> MK_COMB(Choice.result acc, Choice.result x)) th0 tms
                let! th2 = if pflag then eq_elim_RULE (Choice.result th1) else Choice.result th1
                let tms' = hyp th2
                return! itlist (fun e th -> CONV_RULE imp_elim_CONV (DISCH e th)) tms' (Choice.result th2)
            }

        fun tms -> 
            choice {
                let! preds, funs = Choice.List.foldBack (fun x acc -> fm_consts x acc) tms ([], [])
                // NOTE: review this function
                let! eqs0, noneqs = Choice.List.partition (fun (t, _) -> 
                                        choice {
                                            let! (s, _) = dest_const t 
                                            return is_const t && s = "="
                                        }) preds
                if eqs0 = [] then 
                    return []
                else 
                    let pcongs = map (create_congruence_axiom true) noneqs
                    let fcongs = map (create_congruence_axiom false) funs
                    let! tms1 = Choice.List.map (Choice.map concl) (pcongs @ fcongs)
                    let! preds1, _ = Choice.List.foldBack (fun x acc -> fm_consts x acc) tms1 ([], [])
                    let! eqs1 = Choice.List.filter (fun (t, _) -> 
                                    choice {
                                        let! (s, _) = dest_const t
                                        return is_const t && s = "="
                                    }) preds1

                    let eqs = union eqs0 eqs1
                    let equivs = itlist (union' equals_thm << Choice.get << create_equivalence_axioms) eqs []
                    return equivs @ pcongs @ fcongs
            }

    (* ----------------------------------------------------------------------- *)
    (* Brand's transformation.                                                 *)
    (* ----------------------------------------------------------------------- *)

    let perform_brand_modification = 
        let rec subterms_irrefl lconsts tm acc = 
            if is_var tm || is_const tm then acc
            else 
                let _, args = strip_comb tm
                itlist (subterms_refl lconsts) args acc

        and subterms_refl lconsts tm acc = 
            if is_var tm then 
                if mem tm lconsts then insert tm acc
                else acc
            elif is_const tm then insert tm acc
            else 
                let _, args = strip_comb tm
                itlist (subterms_refl lconsts) args (insert tm acc)

        let CLAUSIFY = CONV_RULE(REWR_CONV(TAUT(parse_term @"a ==> b <=> ~a \/ b")))

        let rec BRAND tms th = 
            choice {
                let! th = th
                if tms = [] then 
                    return th
                else 
                    let tm = hd tms
                    let! gv = Choice.map genvar (type_of tm)
                    let! eq = mk_eq(gv, tm)
                    let! th' = CLAUSIFY(DISCH eq (SUBS [SYM(ASSUME eq)] (Choice.result th)))
                    let! tms' = Choice.List.map (subst [gv, tm]) (tl tms)
                    return! BRAND tms' (Choice.result th')
            }

        let BRAND_CONGS th = 
            choice {
                let! th = th
                let lconsts = freesl <| hyp th
                let lits = disjuncts <| concl th
                let! atoms = 
                    Choice.List.map (fun t -> 
                        dest_neg t
                        |> Choice.bindError (function Failure _ -> Choice.result t | e -> Choice.error e)) lits

                let! eqs, noneqs = 
                    Choice.List.partition (fun t -> 
                        choice {
                            let! (s, _) = dest_const(fst(strip_comb t)) 
                            return s = "="
                        }) atoms

                let acc = itlist (subterms_irrefl lconsts) noneqs []
                let uts = itlist (itlist(subterms_irrefl lconsts) << snd << strip_comb) eqs acc
                let sts = sort (fun s t -> not(Choice.get <| free_in s t)) uts
                return! BRAND sts (Choice.result th)
            }

        let BRANDE th = 
            choice {
                let! th = th
                let tm = concl th
                let! l, r = dest_eq tm
                let! ty1 = type_of l
                let gv = genvar ty1
                let! eq = mk_eq(r, gv)
                let! tm1 = rator tm
                return! CLAUSIFY(DISCH eq (EQ_MP (AP_TERM tm1 (ASSUME eq)) (Choice.result th)))
            }

        let LDISJ_CASES th lth rth = 
            choice {
                let! rth = rth
                let! lth = lth
                let! th = th
                let tm1 = concl rth
                let tm2 = concl lth
                return! DISJ_CASES (Choice.result th) (DISJ1 (Choice.result lth) tm1) (DISJ2 tm2 (Choice.result rth))
            }

        let ASSOCIATE = CONV_RULE(REWR_CONV(GSYM DISJ_ASSOC))

        let rec BRAND_TRANS th = 
            choice {
                let! th = th
                let tm = concl th
                return! 
                    choice { 
                        let! l, r = dest_disj tm
                        if is_eq l then 
                            let! lth = ASSUME l
                            let! lth1 = BRANDE (Choice.result lth)
                            let! lth2 = BRANDE(SYM (Choice.result lth))
                            let! rth = BRAND_TRANS(ASSUME r)
                            return map (ASSOCIATE << LDISJ_CASES (Choice.result th) (Choice.result lth1)) rth
                                   @ map (ASSOCIATE << LDISJ_CASES (Choice.result th) (Choice.result lth2)) rth
                        else 
                            let! rth = BRAND_TRANS(ASSUME r)
                            return map (LDISJ_CASES (Choice.result th) (ASSUME l)) rth
                    }
                    |> Choice.bindError (function
                        | Failure _ ->
                            if is_eq tm then 
                                Choice.result [BRANDE (Choice.result th); BRANDE(SYM (Choice.result th))]
                            else Choice.result [Choice.result th]
                        | e -> Choice.error e)
            }

        let find_eqs = 
            find_terms(fun t -> 
                choice {
                    let! (s, _) = dest_const t
                    return s = "="
                })

        let REFLEXATE ths = 
            choice {
                let eqs = itlist (union << Choice.get << find_eqs << concl << Choice.get) ths []
                let tys = map (hd << snd << Choice.get << dest_type << snd << Choice.get << dest_const) eqs
                let gvs = map genvar tys
                return itlist (fun v acc -> (REFL v) :: acc) gvs ths
            }

        fun ths -> 
            // NOTE: review this
            if exists (function Success th -> (Choice.isResult << find_term (Choice.result << is_eq) << concl) th
                              | Error _ -> false) ths then 
                let ths' = map BRAND_CONGS ths
                let ths'' = itlist (union' equals_thm << Choice.get << BRAND_TRANS) ths' []
                Choice.get <| REFLEXATE ths''
            else ths

    (* ----------------------------------------------------------------------- *)
    (* Push duplicated copies of poly theorems to match existing assumptions.  *)
    (* ----------------------------------------------------------------------- *)

    let (POLY_ASSUME_TAC : Protected<thm0> list -> tactic) = 
        let rec uniq' eq = 
            fun l -> 
            match l with
            | x :: (y :: _ as t) -> 
                let t' = uniq' eq t
                if eq x y then t'
                elif t' == t then l
                else x :: t'
            | _ -> l

        let setify' le eq s = uniq' eq (sort le s)

        let rec grab_constants tm acc = 
            choice {
                if is_forall tm || is_exists tm then
                    let! tm0 = rand tm
                    let! tm1 = body tm0
                    return! grab_constants tm1 acc
                elif is_iff tm || is_imp tm || is_conj tm || is_disj tm then
                    let! tm1 = lhand tm
                    let! r1 = grab_constants tm1 acc
                    let! tm2 = rand tm
                    return! grab_constants tm2 r1
                elif is_neg tm then
                    let! tm1 = rand tm
                    return! grab_constants tm1 acc
                else
                    let! tms = find_terms (Choice.result << is_const) tm
                    return union tms acc
            }

        let match_consts(tm1, tm2) = 
            choice {
                let! s1, ty1 = dest_const tm1
                let! s2, ty2 = dest_const tm2
                if s1 = s2 then 
                    return! type_match ty1 ty2 []
                else
                    return! Choice.failwith "match_consts"
            }

        let polymorph mconsts th = 
            choice {
                let! th = th
                let! tys1 = type_vars_in_term <| concl th
                let tms2 = hyp th
                let! tys3 = Choice.List.map type_vars_in_term tms2
                let tvs = subtract tys1 (unions tys3)
                if List.isEmpty tvs then 
                    return [Choice.result th]
                else
                    let tm1 = concl th
                    let! pconsts = grab_constants tm1 []
                    let tyins = mapfilter (Choice.toOption << match_consts) (allpairs (fun x y -> x, y) pconsts mconsts)
                    let ths' = 
                        // NOTE: it doesn't make sense to compare two exceptions
                        setify' (fun th th' -> 
                                match th, th' with
                                | Success th, Success th' -> dest_thm th <= dest_thm th'
                                | _ -> false) 
                            equals_thm 
                            (mapfilter (Choice.toOption << Choice.map Choice.result << C INST_TYPE (Choice.result th)) tyins)
                    if List.isEmpty ths' then 
                        warn true "No useful-looking instantiations of lemma"
                        return [Choice.result th]
                    else 
                        return ths'
            }

        let rec polymorph_all mconsts ths acc = 
            choice {
                if ths = [] then 
                    return acc
                else 
                    let! ths' = polymorph mconsts (hd ths)
                    let! tms1 = Choice.List.map (Choice.map concl) ths'
                    let! mconsts' = Choice.List.foldBack grab_constants tms1 mconsts
                    return! polymorph_all mconsts' (tl ths) (union' equals_thm ths' acc)
            }

        fun ths (asl, _ as gl) -> 
            choice {
                let! mconsts = Choice.List.foldBack (fun (_, x) acc -> 
                                    choice {
                                        let! th = x
                                        let tm = concl th
                                        return! grab_constants tm acc
                                    }) asl []
                let! ths' = polymorph_all mconsts ths []
                return! MAP_EVERY ASSUME_TAC ths' gl
            }

    (* ----------------------------------------------------------------------- *)
    (* Basic HOL MESON procedure.                                              *)
    (* ----------------------------------------------------------------------- *)

    let SIMPLE_MESON_REFUTE min max inc ths : Protected<thm0> = 
        choice {
            clear_contrapos_cache()
            inferences := 0
            let old_dcutin = !meson_dcutin
            if !meson_depth then meson_dcutin := 100001
            else ()
            let! ths' = 
                choice {
                    if !meson_brand then 
                        return perform_brand_modification ths
                    else
                        let! tms1 = Choice.List.map (Choice.map concl) ths
                        let! tms2 = create_equality_axioms tms1
                        return ths @ tms2
                }
            let rules = optimize_rules(fol_of_hol_clauses ths')
            let! proof, (insts, _, _) = solve_goal rules (!meson_depth) min max inc (1, [])
            meson_dcutin := old_dcutin
            return! meson_to_hol insts proof
        }

    let CONJUNCTS_THEN' ttac cth = ttac(CONJUNCT1 cth)
                                   |> THEN <| ttac(CONJUNCT2 cth)

    let PURE_MESON_TAC min max inc gl = 
        reset_vars()
        reset_consts()
        (FIRST_ASSUM CONTR_TAC
         |> ORELSE <| W(ACCEPT_TAC << SIMPLE_MESON_REFUTE min max inc << map snd << fst)) gl

    let QUANT_BOOL_CONV = 
        PURE_REWRITE_CONV 
            [FORALL_BOOL_THM; EXISTS_BOOL_THM; COND_CLAUSES; NOT_CLAUSES; 
             IMP_CLAUSES; AND_CLAUSES; OR_CLAUSES; EQ_CLAUSES; FORALL_SIMP; 
             EXISTS_SIMP]

    let rec SPLIT_TAC n g = 
        ((FIRST_X_ASSUM(CONJUNCTS_THEN' ASSUME_TAC)
          |> THEN <| SPLIT_TAC n)
         |> ORELSE <| (if n > 0
                       then FIRST_X_ASSUM DISJ_CASES_TAC
                            |> THEN <| SPLIT_TAC(n - 1)
                       else NO_TAC)
         |> ORELSE <| ALL_TAC) g


/// First-order proof search with specified search limits and increment.
let GEN_MESON_TAC min max step ths =
    REFUTE_THEN ASSUME_TAC
    |> THEN <| GenMesonTac.POLY_ASSUME_TAC(map GEN_ALL ths)
    |> THEN <| W(MAP_EVERY(UNDISCH_TAC << concl << Choice.get << snd) << fst)
    |> THEN <| SELECT_ELIM_TAC
    |> THEN <| W(fun (_, w) -> MAP_EVERY (fun v -> SPEC_TAC(v, v)) (frees w))
    |> THEN <| CONV_TAC(PRESIMP_CONV
                        |> THENC <| TOP_DEPTH_CONV BETA_CONV
                        |> THENC <| LAMBDA_ELIM_CONV
                        |> THENC <| CONDS_CELIM_CONV
                        |> THENC <| GenMesonTac.QUANT_BOOL_CONV)
    |> THEN <| REPEAT(GEN_TAC
                        |> ORELSE <| DISCH_TAC)
    |> THEN <| REFUTE_THEN ASSUME_TAC
    |> THEN <| RULE_ASSUM_TAC(CONV_RULE(NNF_CONV
                                        |> THENC <| SKOLEM_CONV))
    |> THEN <| REPEAT(FIRST_X_ASSUM CHOOSE_TAC)
    |> THEN <| ASM_FOL_TAC
    |> THEN <| GenMesonTac.SPLIT_TAC(!meson_split_limit)
    |> THEN <| RULE_ASSUM_TAC(CONV_RULE(PRENEX_CONV
                                        |> THENC <| WEAK_CNF_CONV))
    |> THEN <| RULE_ASSUM_TAC
                (repeat
                    (fun th -> 
                        choice {
                            let! th = th
                            let! (tm1, _) = dest_forall <| concl th
                            let! ty1 = type_of tm1
                            return SPEC (genvar ty1) (Choice.result th)
                        }
                        |> Choice.toOption))
    |> THEN <| REPEAT(FIRST_X_ASSUM(GenMesonTac.CONJUNCTS_THEN' ASSUME_TAC))
    |> THEN <| RULE_ASSUM_TAC(CONV_RULE(ASSOC_CONV DISJ_ASSOC))
    |> THEN <| REPEAT(FIRST_X_ASSUM SUBST_VAR_TAC)
    |> THEN <| GenMesonTac.PURE_MESON_TAC min max step

(* ------------------------------------------------------------------------- *)
(* Common cases.                                                             *)
(* ------------------------------------------------------------------------- *)

/// Automated first-order proof search tactic using assumptions of goal.
let ASM_MESON_TAC = GEN_MESON_TAC 0 50 1

/// Automated first-order proof search tactic.
let MESON_TAC ths = POP_ASSUM_LIST(K ALL_TAC)
                    |> THEN <| ASM_MESON_TAC ths

(* ------------------------------------------------------------------------- *)
(* Also introduce a rule.                                                    *)
(* ------------------------------------------------------------------------- *)

/// Attempt to prove a term by first-order proof search.
let MESON ths tm = prove(tm, MESON_TAC ths)
