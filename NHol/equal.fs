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

#if INTERACTIVE
#else
/// Basic equality reasoning including conversionals.
module NHol.equal

open System
open FSharp.Compatibility.OCaml

open NHol
open lib
open fusion
open fusion.Hol_kernel
open basics
open nets
open printer
open preterm
open parser
#endif

(* ------------------------------------------------------------------------- *)
(* Type abbreviation for conversions.                                        *)
(* ------------------------------------------------------------------------- *)

type conv = term -> thm

(* ------------------------------------------------------------------------- *)
(* A bit more syntax.                                                        *)
(* ------------------------------------------------------------------------- *)

/// Take left-hand argument of a binary operator.
let lhand = Choice.get << rand << Choice.get << rator
/// Returns the left-hand side of an equation.
let lhs = fst << Choice.get << dest_eq
/// Returns the right-hand side of an equation.
let rhs = snd << Choice.get << dest_eq

(* ------------------------------------------------------------------------- *)
(* Similar to Choice.get <| variant, but even avoids constants, and ignores types.         *)
(* ------------------------------------------------------------------------- *)

/// Rename variable to avoid specied names and constant names.
let mk_primed_var =
    let rec svariant avoid s = 
        if mem s avoid || (Choice.isResult <| get_const_type s && not(is_hidden s)) then svariant avoid (s + "'")
        else s
    fun avoid v -> 
        let s, ty = Choice.get <| dest_var v
        let s' = svariant (mapfilter (fst << Choice.get << dest_var) avoid) s
        mk_var(s', ty)

(* ------------------------------------------------------------------------- *)
(* General case of beta-conversion.                                          *)
(* ------------------------------------------------------------------------- *)

/// General case of beta-conversion.
let BETA_CONV tm = 
    BETA tm
    |> Choice.bindError (fun _ -> 
        let f, arg = Choice.get <| dest_comb tm
        let v = Choice.get <| bndvar f
        INST [arg, v] (BETA(Choice.get <| mk_comb(f, v))))
    |> Choice.mapError (fun _ -> Exception "BETA_CONV: Not a beta-redex")

(* ------------------------------------------------------------------------- *)
(* A few very basic derived equality rules.                                  *)
(* ------------------------------------------------------------------------- *)

/// This function is currently unsafe
let concl thm =
    concl (Choice.get thm)

/// Applies a function to both sides of an equational theorem.
let AP_TERM tm th = 
    MK_COMB (REFL tm, th)
    |> Choice.mapError (fun _ -> Exception "AP_TERM")

/// Proves equality of equal functions applied to a term.
let AP_THM th tm = 
    MK_COMB (th, REFL tm)
    |> Choice.mapError (fun _ -> Exception "AP_THM")

/// Swaps left-hand and right-hand sides of an equation.
let SYM th = 
    let tm = concl th
    let l, r = Choice.get <| dest_eq tm
    let lth = REFL l
    EQ_MP (MK_COMB(AP_TERM (Choice.get <| rator(Choice.get <| rator tm)) th, lth)) lth

/// Proves equality of alpha-equivalent terms.
let ALPHA tm1 tm2 = 
    TRANS (REFL tm1) (REFL tm2)
    |> Choice.mapError (fun _ -> Exception "ALPHA")

/// Renames the bound variable of a lambda-abstraction.
let ALPHA_CONV v tm = 
    let res = alpha v tm
    ALPHA tm res

/// Renames the bound variable of an abstraction or binder.
let GEN_ALPHA_CONV v tm = 
    if is_abs tm then ALPHA_CONV v tm
    else 
        let b, abs = Choice.get <| dest_comb tm
        AP_TERM b (ALPHA_CONV v abs)

/// Compose equational theorems with binary operator.
let MK_BINOP op (lth, rth) = MK_COMB(AP_TERM op lth, rth)

(* ------------------------------------------------------------------------- *)
(* Terminal conversion combinators.                                          *)
(* ------------------------------------------------------------------------- *)

/// Conversion that always fails.
let NO_CONV : conv = fun tm -> Choice2Of2 <| Exception "NO_CONV"

/// Conversion that always succeeds and leaves a term unchanged.
let ALL_CONV : conv = REFL

(* ------------------------------------------------------------------------- *)
(* Combinators for sequencing, trying, repeating etc. conversions.           *)
(* ------------------------------------------------------------------------- *)

/// Applies two conversions in sequence.
let THENC : conv -> conv -> conv = 
    fun conv1 conv2 t -> 
        let th1 = conv1 t
        let th2 = conv2 (Choice.get <| rand(concl th1))
        TRANS th1 th2

/// Applies the first of two conversions that succeeds.
let ORELSEC : conv -> conv -> conv = 
    fun conv1 conv2 t -> 
        conv1 t
        |> Choice.bindError (fun _ -> conv2 t)

/// Apply the first of the conversions in a given list that succeeds.
let FIRST_CONV : conv list -> conv = end_itlist (fun c1 c2 -> ORELSEC c1 c2)

/// Applies in sequence all the conversions in a given list of conversions.
let EVERY_CONV : conv list -> conv = fun l -> itlist THENC l ALL_CONV

/// Repeatedly apply a conversion (zero or more times) until it fails.
let REPEATC : conv -> conv = 
    let rec REPEATC conv t = 
        (ORELSEC (THENC conv (REPEATC conv)) ALL_CONV) t
    REPEATC

/// Makes a conversion fail if applying it leaves a term unchanged.
let CHANGED_CONV : conv -> conv = 
    fun conv tm -> 
        let th = conv tm
        let l, r = Choice.get <| dest_eq(concl th)
        if aconv l r then Choice2Of2 <| Exception "CHANGED_CONV"
        else th

/// Attempts to apply a conversion; applies identity conversion in case of failure.
let TRY_CONV conv = ORELSEC conv ALL_CONV

(* ------------------------------------------------------------------------- *)
(* Subterm conversions.                                                      *)
(* ------------------------------------------------------------------------- *)

/// Applies a conversion to the operator of an application.
let RATOR_CONV : conv -> conv = 
    fun conv tm -> 
        let l, r = Choice.get <| dest_comb tm
        AP_THM (conv l) r

/// Applies a conversion to the operand of an application.
let RAND_CONV : conv -> conv = 
    fun conv tm -> 
        let l, r = Choice.get <| dest_comb tm
        AP_TERM l (conv r)

/// Apply a conversion to left-hand argument of binary operator.
let LAND_CONV = RATOR_CONV << RAND_CONV

/// Applies two conversions to the two sides of an application.
let COMB2_CONV : conv -> conv -> conv = 
    fun lconv rconv tm -> 
        let l, r = Choice.get <| dest_comb tm
        MK_COMB(lconv l, rconv r)

/// Applies a conversion to the two sides of an application.
let COMB_CONV = W COMB2_CONV

/// Applies a conversion to the Choice.get <| body of an abstraction.
let ABS_CONV : conv -> conv = 
    fun conv tm -> 
        let v, bod = Choice.get <| dest_abs tm
        let th = conv bod
        ABS v th
        |> Choice.bindError(fun _ ->
            let gv = genvar(Choice.get <| type_of v)
            let gbod = Choice.get <| vsubst [gv, v] bod
            let gth = ABS gv (conv gbod)
            let gtm = concl gth
            let l, r = Choice.get <| dest_eq gtm
            let v' = Choice.get <| variant (frees gtm) v
            let l' = alpha v' l
            let r' = alpha v' r
            EQ_MP (ALPHA gtm (Choice.get <| mk_eq(l', r'))) gth)

/// Applies conversion to the Choice.get <| body of a binder.
let BINDER_CONV conv tm = 
    if is_abs tm then ABS_CONV conv tm
    else RAND_CONV (ABS_CONV conv) tm

/// Applies a conversion to the top-level subterms of a term.
let SUB_CONV conv tm = 
    match tm with
    | Comb(_, _) -> COMB_CONV conv tm
    | Abs(_, _) -> ABS_CONV conv tm
    | _ -> REFL tm

/// Applies a conversion to both arguments of a binary operator.
let BINOP_CONV conv tm = 
    let lop, r = Choice.get <| dest_comb tm
    let op, l = Choice.get <| dest_comb lop
    MK_COMB(AP_TERM op (conv l), conv r)

(* ------------------------------------------------------------------------- *)
(* Depth conversions; internal use of a failure-propagating `Boultonized'    *)
(* version to avoid a great deal of reuilding of terms.                      *)
(* ------------------------------------------------------------------------- *)

let rec private THENQC conv1 conv2 tm = 
    try 
        let th1 = conv1 tm
        try 
            let th2 = conv2(Choice.get <| rand(concl th1))
            TRANS th1 th2
        with
        | Failure _ -> th1
    with
    | Failure _ -> conv2 tm

and private THENCQC conv1 conv2 tm = 
    let th1 = conv1 tm
    try 
        let th2 = conv2(Choice.get <| rand(concl th1))
        TRANS th1 th2
    with
    | Failure _ -> th1

and private COMB_QCONV conv tm = 
    let l, r = Choice.get <| dest_comb tm
    try 
        let th1 = conv l
        try 
            let th2 = conv r
            MK_COMB(th1, th2)
        with
        | Failure _ -> AP_THM th1 r
    with
    | Failure _ -> AP_TERM l (conv r)

let rec private REPEATQC conv tm = THENCQC conv (REPEATQC conv) tm

let private SUB_QCONV conv tm = 
    if is_abs tm then ABS_CONV conv tm
    else COMB_QCONV conv tm

let rec private ONCE_DEPTH_QCONV conv tm = 
    (ORELSEC conv (SUB_QCONV(ONCE_DEPTH_QCONV conv))) tm

and private DEPTH_QCONV conv tm = 
    THENQC (SUB_QCONV(DEPTH_QCONV conv)) (REPEATQC conv) tm

and private REDEPTH_QCONV conv tm = 
    THENQC (SUB_QCONV(REDEPTH_QCONV conv)) (THENCQC conv (REDEPTH_QCONV conv)) tm

and private TOP_DEPTH_QCONV conv tm = 
    THENQC (REPEATQC conv) (THENCQC (SUB_QCONV(TOP_DEPTH_QCONV conv)) (THENCQC conv (TOP_DEPTH_QCONV conv))) tm

and private TOP_SWEEP_QCONV conv tm = 
    THENQC (REPEATQC conv) (SUB_QCONV(TOP_SWEEP_QCONV conv)) tm

/// Applies a conversion once to the first suitable sub-term(s) encountered in top-down order.
let ONCE_DEPTH_CONV (c : conv) : conv = TRY_CONV (ONCE_DEPTH_QCONV c)

/// Applies a conversion repeatedly to all the sub-terms of a term, in bottom-up order.
let DEPTH_CONV (c : conv) : conv = TRY_CONV (DEPTH_QCONV c)

/// Applies a conversion bottom-up to all subterms, retraversing changed ones.
let REDEPTH_CONV (c : conv) : conv = TRY_CONV (REDEPTH_QCONV c)

/// Applies a conversion top-down to all subterms, retraversing changed ones.
let TOP_DEPTH_CONV (c : conv) : conv = TRY_CONV (TOP_DEPTH_QCONV c)

/// Repeatedly applies a conversion top-down at all levels,
/// but after descending to subterms, does not return to higher ones.
let TOP_SWEEP_CONV (c : conv) : conv = TRY_CONV (TOP_SWEEP_QCONV c)

(* ------------------------------------------------------------------------- *)
(* Apply at leaves of op-tree; NB any failures at leaves cause failure.      *)
(* ------------------------------------------------------------------------- *)

/// Applied a conversion to the leaves of a tree of binary operator expressions.
let rec DEPTH_BINOP_CONV op conv tm = 
    match tm with
    | Comb(Comb(op', l), r) when op' = op -> 
        let l, r = dest_binop op tm
        let lth = DEPTH_BINOP_CONV op conv l
        let rth = DEPTH_BINOP_CONV op conv r
        MK_COMB(AP_TERM op' lth, rth)
    | _ -> conv tm

(* ------------------------------------------------------------------------- *)
(* Follow a path.                                                            *)
(* ------------------------------------------------------------------------- *)

/// Follow a path.
let PATH_CONV = 
    let rec path_conv s cnv = 
        match s with
        | [] -> cnv
        | "l" :: t -> RATOR_CONV(path_conv t cnv)
        | "r" :: t -> RAND_CONV(path_conv t cnv)
        | _ :: t -> ABS_CONV(path_conv t cnv)
    fun s cnv -> path_conv (explode s) cnv

(* ------------------------------------------------------------------------- *)
(* Follow a pattern                                                          *)
(* ------------------------------------------------------------------------- *)

/// Follow a pattern.
let PAT_CONV = 
    let rec PCONV xs pat conv = 
        if mem pat xs then conv
        elif not(exists (fun x -> free_in x pat) xs) then ALL_CONV
        elif is_comb pat then COMB2_CONV (PCONV xs (Choice.get <| rator pat) conv) (PCONV xs (Choice.get <| rand pat) conv)
        else ABS_CONV(PCONV xs (Choice.get <| body pat) conv)
    fun pat -> 
        let xs, pbod = strip_abs pat
        PCONV xs pbod

(* ------------------------------------------------------------------------- *)
(* Symmetry conversion.                                                      *)
(* ------------------------------------------------------------------------- *)

/// Symmetry conversion.
let SYM_CONV tm = 
    let th1 = SYM(ASSUME tm)
    let tm' = concl th1
    let th2 = SYM(ASSUME tm')
    DEDUCT_ANTISYM_RULE th2 th1
    |> Choice.mapError (fun _ -> Exception "SYM_CONV")

(* ------------------------------------------------------------------------- *)
(* Conversion to a rule.                                                     *)
(* ------------------------------------------------------------------------- *)

/// Conversion to a rule.
let CONV_RULE (conv : conv) th = EQ_MP (conv(concl th)) th

(* ------------------------------------------------------------------------- *)
(* Substitution conversion.                                                  *)
(* ------------------------------------------------------------------------- *)

/// Substitution conversion.
let SUBS_CONV ths tm = 
    let tm = 
        if ths = [] then REFL tm
        else 
            let lefts = map (lhand << concl) ths
            let gvs = map (genvar << Choice.get << type_of) lefts
            let pat = Choice.get <| subst (zip gvs lefts) tm
            let abs = list_mk_abs(gvs, pat)
            let th = 
                rev_itlist (fun y x -> CONV_RULE (THENC (RAND_CONV BETA_CONV) (LAND_CONV BETA_CONV)) (MK_COMB(x, y))) 
                    ths (REFL abs)
            if Choice.get <| rand(concl th) = tm then REFL tm
            else th
    tm |> Choice.mapError (fun _ -> Exception "SUBS_CONV")

(* ------------------------------------------------------------------------- *)
(* Get a few rules.                                                          *)
(* ------------------------------------------------------------------------- *)

/// Beta-reduces all the beta-redexes in the conclusion of a theorem.
let BETA_RULE = CONV_RULE(REDEPTH_CONV BETA_CONV)
/// Reverses the first equation(s) encountered in a top-down search.
let GSYM = CONV_RULE(ONCE_DEPTH_CONV SYM_CONV)
/// Makes simple term substitutions in a theorem using a given list of theorems.
let SUBS ths = CONV_RULE(SUBS_CONV ths)

(* ------------------------------------------------------------------------- *)
(* A cacher for conversions.                                                 *)
(* ------------------------------------------------------------------------- *)

let private ALPHA_HACK th = 
    let tm' = lhand(concl th)
    fun tm -> 
        if tm' = tm then th
        else TRANS (ALPHA tm tm') th

/// A cacher for conversions.
let CACHE_CONV (conv : conv) : conv = 
    // NOTE : This is not thread-safe!
    let net = ref empty_net
    fun tm -> 
        try 
            tryfind (fun f -> Some <| f tm) (lookup tm (!net))
            |> Option.getOrFailWith "tryfind"
        with
        | Failure _ -> 
            let th = conv tm
            net := enter [] (tm, ALPHA_HACK th) (!net)
            th
