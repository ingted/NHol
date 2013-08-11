﻿(*

Copyright 1998 University of Cambridge
Copyright 1998-2007 John Harrison
Copyright 2013 Jack Pappas, Anh-Dung Phan, Eric Taucher

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
/// Boolean theory including (intuitionistic) defs of logical connectives.
module NHol.bool

open System
open FSharp.Compatibility.OCaml

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
#endif

logger.Trace("Entering bool.fs")

(* ------------------------------------------------------------------------- *)
(* Set up parse status of basic and derived logical constants.               *)
(* ------------------------------------------------------------------------- *)

parse_as_prefix "~"
parse_as_binder "\\"
parse_as_binder "!"
parse_as_binder "?"
parse_as_binder "?!"
parse_as_infix("==>", (4, "right"))
parse_as_infix("\\/", (6, "right"))
parse_as_infix("/\\", (8, "right"))

(* ------------------------------------------------------------------------- *)
(* Set up more orthodox notation for equations and equivalence.              *)
(* ------------------------------------------------------------------------- *)

parse_as_infix("<=>", (2, "right"))
override_interface("<=>", parse_term @"(=):bool->bool->bool") |> Choice.ignoreOrRaise
parse_as_infix("=", (12, "right"))

(* ------------------------------------------------------------------------- *)
(* Special syntax for Boolean equations (IFF).                               *)
(* ------------------------------------------------------------------------- *)

/// Tests if a term is an equation between Boolean terms (iff / logical equivalence).
let is_iff tm = 
    match tm with
    | Comb(Comb(Const("=", Tyapp("fun", [Tyapp("bool", []); _])), l), r) -> true
    | _ -> false

/// Term destructor for logical equivalence.
let dest_iff tm : Protected<_> =
    choice {
    match tm with
    | Comb(Comb(Const("=", Tyapp("fun", [Tyapp("bool", []); _])), l), r) -> 
        return l, r
    | _ -> 
        return! Choice.failwith "dest_iff"
    }

/// Constructs a logical equivalence (Boolean equation).
let mk_iff : term * term -> Protected<_> =
    let eq_tm = parse_term @"(<=>)"
    fun (l, r) ->
        choice {
        let! l' = mk_comb(eq_tm, l)
        return! mk_comb(l', r)
        }

(* ------------------------------------------------------------------------- *)
(* Rule allowing easy instantiation of polymorphic proformas.                *)
(* ------------------------------------------------------------------------- *)

/// Instantiate types and terms in a theorem.
let PINST tyin tmin : Protected<thm0> -> Protected<thm0> = 
    let tmin' =
        tmin
        |> Choice.List.map (fun (tm1, tm2) -> 
            choice {
            let! tm2' = inst tyin tm2
            return tm1, tm2'
            })

    fun th -> 
        choice {
        let! tmin' = tmin'
        let! th = th
        let! foo1 = INST_TYPE tyin (Choice.result th)
        return! INST tmin' (Choice.result foo1)
        }
        |> Choice.mapError (fun e -> nestedFailure e "PINST")

(* ------------------------------------------------------------------------- *)
(* Useful derived deductive rule.                                            *)
(* ------------------------------------------------------------------------- *)

/// Eliminates a provable assumption from a theorem.
let PROVE_HYP (ath : Protected<thm0>) (bth : Protected<thm0>) : Protected<thm0> = 
    choice {
    let! ath = ath
    let! bth = bth
    let t = concl ath
    let ts = hyp bth
    if exists (aconv t) ts then
        let! foo1 = DEDUCT_ANTISYM_RULE (Choice.result ath) (Choice.result bth)
        return! EQ_MP (Choice.result foo1) (Choice.result ath)
    else
        return bth
    }

(* ------------------------------------------------------------------------- *)
(* Rules for T                                                               *)
(* ------------------------------------------------------------------------- *)

let T_DEF : Protected<thm0> =
    new_basic_definition <| parse_term @"T = ((\p:bool. p) = (\p:bool. p))"

let TRUTH : Protected<thm0> =
    choice {
    // CLEAN : Rename the values below to something sensible.
    let tm = parse_term @"\p:bool. p"
    let! T_DEF = T_DEF
    let! foo1 = REFL tm
    let! foo2 = SYM (Choice.result T_DEF)
    return! EQ_MP (Choice.result foo2) (Choice.result foo1)
    }

/// Eliminates equality with T.
let EQT_ELIM (th : Protected<thm0>) : Protected<thm0> =
    choice {
    let! th = th
    let! TRUTH = TRUTH
    // CLEAN : Rename the values below to something sensible.
    let! foo1 = SYM (Choice.result th)
    return! EQ_MP (Choice.result foo1) (Choice.result TRUTH)
    }
    |> Choice.mapError (fun e -> nestedFailure e "EQT_ELIM")

/// Introduces equality with T.
let EQT_INTRO =
    let t = parse_term @"t:bool"
    let pth =
        choice {
        // CLEAN : Rename the values below to something sensible.
        let! foo1 = ASSUME t
        let! TRUTH = TRUTH
        let! th1 = DEDUCT_ANTISYM_RULE (Choice.result foo1) (Choice.result TRUTH)
        let tm = concl th1
        let! foo2 = ASSUME tm
        let! th2 = EQT_ELIM (Choice.result foo2)
        return! DEDUCT_ANTISYM_RULE (Choice.result th2) (Choice.result th1)
        }
    fun (th : Protected<thm0>) ->
        choice {
        let! pth = pth
        let! th = th
        let! foo1 = INST [concl th, t] (Choice.result pth)
        return! EQ_MP (Choice.result foo1) (Choice.result th)
        } : Protected<thm0>

(* ------------------------------------------------------------------------- *)
(* Rules for /\                                                              *)
(* ------------------------------------------------------------------------- *)

let AND_DEF = new_basic_definition <| parse_term @"(/\) = \p q. (\f:bool->bool->bool. f p q) = (\f. f T T)"

/// Constructs a conjunction.
let mk_conj = mk_binary "/\\"

/// Constructs the conjunction of a list of terms.
let list_mk_conj : term list -> Protected<term> =
    Choice.List.reduceBack (curry mk_conj)

/// Introduces a conjunction.
let CONJ =
    let f = parse_term @"f:bool->bool->bool"
    let p = parse_term @"p:bool"
    let q = parse_term @"q:bool"
    let pth =
        choice {
        let! pth = ASSUME p
        let! qth = ASSUME q
        // CLEAN : Rename the values below to something sensible.
        let! foo1 = EQT_INTRO (Choice.result pth)
        let! foo2 = EQT_INTRO (Choice.result qth)
        let! foo3 = AP_TERM f (Choice.result foo1)
        let! th1 = MK_COMB(Choice.result foo3, Choice.result foo2)
        let! th2 = ABS f (Choice.result th1)
        let! AND_DEF = AND_DEF
        let! foo4 = AP_THM (Choice.result AND_DEF) p
        let! foo5 = AP_THM (Choice.result foo4) q
        let! th3 = BETA_RULE (Choice.result foo5)
        let! foo6 = SYM (Choice.result th3)
        return! EQ_MP (Choice.result foo6) (Choice.result th2)
        }
    fun (th1 : Protected<thm0>) (th2 : Protected<thm0>) ->
        choice {
        let! pth = pth
        let! th1 = th1
        let! th2 = th2
        let! th = INST [concl th1, p; concl th2, q] (Choice.result pth)
        let! foo1 = PROVE_HYP (Choice.result th1) (Choice.result th)
        return! PROVE_HYP (Choice.result th2) (Choice.result foo1)
        }

/// Extracts left conjunct of theorem.
let CONJUNCT1 =
    let P = parse_term @"P:bool"
    let Q = parse_term @"Q:bool"
    let pth =
        choice {
        // CLEAN : Rename the values below to something sensible.
        let! AND_DEF = AND_DEF
        let! foo1 = AP_THM (Choice.result AND_DEF) P
        let! th1 = CONV_RULE (RAND_CONV BETA_CONV) (Choice.result foo1)
        let! foo2 = AP_THM (Choice.result th1) Q
        let! th2 = CONV_RULE (RAND_CONV BETA_CONV) (Choice.result foo2)
        let! foo3 = ASSUME <| parse_term @"P /\ Q"
        let! th3 = EQ_MP (Choice.result th2) (Choice.result foo3)
        let! foo4 = AP_THM (Choice.result th3) <| parse_term @"\(p:bool) (q:bool). p"
        let! foo5 = BETA_RULE(Choice.result foo4)
        return! EQT_ELIM (Choice.result foo5)
        }
    fun (th : Protected<thm0>) ->
        choice {
        let! pth = pth
        let! th = th
        let tm = concl th
        let! l, r = dest_conj tm
        let! foo1 = INST [l, P; r, Q] (Choice.result pth)
        return! PROVE_HYP (Choice.result th) (Choice.result foo1)
        }
        |> Choice.mapError (fun e -> nestedFailure e "CONJUNCT1") : Protected<thm0>

/// Extracts right conjunct of theorem.
let CONJUNCT2 = 
    let P = parse_term @"P:bool"
    let Q = parse_term @"Q:bool"
    let pth = 
        let th1 = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM AND_DEF <| parse_term @"P:bool")
        let th2 = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM th1 <| parse_term @"Q:bool")
        let th3 = EQ_MP th2 (ASSUME <| parse_term @"P /\ Q")
        EQT_ELIM(BETA_RULE(AP_THM th3 <| parse_term @"\(p:bool) (q:bool). q"))
    fun (th : Protected<thm0>) -> 
        choice {
            let! tm = Choice.map concl th
            let! l, r = dest_conj tm
            return! PROVE_HYP th (INST [l, P; r, Q] pth)
        }
        |> Choice.mapError (fun e -> nestedFailure e "CONJUNCT2") : Protected<thm0>

/// Extracts both conjuncts of a conjunction.
let CONJ_PAIR th = 
    // TODO: this doesn't seem correct
    CONJUNCT1 th, CONJUNCT2 th

/// Recursively splits conjunctions into a list of conjuncts.
let CONJUNCTS = striplist (fun th ->
                    match CONJ_PAIR th with
                    | (Success th1, Success th2) as th' -> Some th'
                    | _ -> None)

(* ------------------------------------------------------------------------- *)
(* Rules for ==>                                                             *)
(* ------------------------------------------------------------------------- *)

let IMP_DEF = new_basic_definition <| parse_term @"(==>) = \p q. p /\ q <=> p"

/// Constructs an implication.
let mk_imp = mk_binary "==>"

/// Implements the Modus Ponens inference rule.
let MP = 
    let p = parse_term @"p:bool"
    let q = parse_term @"q:bool"
    let pth =
        let th1 = BETA_RULE(AP_THM (AP_THM IMP_DEF p) q)
        let th2 = EQ_MP th1 (ASSUME <| parse_term @"p ==> q")
        CONJUNCT2(EQ_MP (SYM th2) (ASSUME <| parse_term @"p:bool"))
    fun (ith : Protected<thm0>) (th : Protected<thm0>) -> 
        choice {
            let! tm = Choice.map concl ith
            let! ant, con = dest_imp tm
            let! tm' = Choice.map concl th
            if aconv ant tm' then 
                return! PROVE_HYP th (PROVE_HYP ith (INST [ant, p; con, q] pth))
            else 
                return! Choice.failwith "MP: theorems do not agree"
        } : Protected<thm0>

/// Discharges an assumption.
let DISCH = 
    let p = parse_term @"p:bool"
    let q = parse_term @"q:bool"
    let pth =
        
        SYM(BETA_RULE(AP_THM (AP_THM IMP_DEF p) q))
    fun a th -> 
        choice {
            let th1 = CONJ (ASSUME a) th
            let! tm1 = Choice.map concl th1
            let th2 = CONJUNCT1(ASSUME tm1)
            let th3 = DEDUCT_ANTISYM_RULE th1 th2
            let! tm = Choice.map concl th
            let th4 = INST [a, p; tm, q] pth
            return! EQ_MP th4 th3
        } : Protected<thm0>

/// Discharges all hypotheses of a theorem.
let rec DISCH_ALL (th : Protected<thm0>) : Protected<thm0> = 
    choice {
        let! th' = th
        match hyp th' with
        | t :: _ ->
            return! DISCH_ALL(DISCH t th) |> Choice.bindError (function Failure _ -> th | e -> Choice.error e) 
        | _ -> 
            return! th
    }

/// Undischarges the antecedent of an implicative theorem.
let UNDISCH (th : Protected<thm0>) : Protected<thm0> =
    choice {
    let! th = th
    // TODO : Rename these values to something sensible.
    let! foo1 = rator(concl th)
    let! foo2 = rand foo1
    let! foo3 = ASSUME foo2
    return! MP (Choice.result th) (Choice.result foo3)
    }
    |> Choice.mapError (fun e -> nestedFailure e "UNDISCH")

/// Iteratively undischarges antecedents in a chain of implications.
let rec UNDISCH_ALL (th : Protected<thm0>) : Protected<thm0> =
    choice {
    let! th = th
    let tm = concl th

    if is_imp tm then
        let! foo1 = UNDISCH (Choice.result th)
        return! UNDISCH_ALL (Choice.result foo1)
    else
        return th
    }

/// Deduces equality of boolean terms from forward and backward implications.
let IMP_ANTISYM_RULE (th1 : Protected<thm0>) (th2 : Protected<thm0>) =
    choice {
    let! th1 = th1
    let! th2 = th2
    let! UNDISCH_th2 = UNDISCH (Choice.result th2)
    let! UNDISCH_th1 = UNDISCH (Choice.result th1)
    return! DEDUCT_ANTISYM_RULE (Choice.result UNDISCH_th2) (Choice.result UNDISCH_th1)
    }

/// Adds an assumption to a theorem.
let ADD_ASSUM tm th : Protected<thm0> =
    choice {
    let! th = th
    let! foo1 = DISCH tm (Choice.result th)
    let! foo2 = ASSUME tm
    return! MP (Choice.result foo1) (Choice.result foo2)
    }

/// Derives forward and backward implication from equality of boolean terms.
let EQ_IMP_RULE : Protected<thm0> -> Protected<thm0> * Protected<thm0> = 
    let peq = parse_term @"p <=> q"
    let pq =
        dest_iff peq
        |> Choice.mapError (fun e -> nestedFailure e "EQ_IMP_RULE")
    let pth1 p =
        choice {
        let! foo1 = ASSUME peq
        let! foo2 = ASSUME p
        let! foo3 = EQ_MP (Choice.result foo1) (Choice.result foo2)
        let! foo4 = DISCH p (Choice.result foo3)
        return! DISCH peq (Choice.result foo4)
        }
    let pth2 q =
        choice {
        let! foo1 = ASSUME peq
        let! foo2 = SYM (Choice.result foo1)
        let! foo3 = ASSUME q
        let! foo4 = EQ_MP (Choice.result foo2) (Choice.result foo3)
        let! foo5 = DISCH q (Choice.result foo4)
        return! DISCH peq (Choice.result foo5)
        }
    fun (th : Protected<thm0>) ->
        choice {
        let! p, q = pq
        let! th = th
        let! l, r =
            concl th
            |> dest_iff
            |> Choice.mapError (fun e -> nestedFailure e "EQ_IMP_RULE")

        let! foo1 = pth1 p
        let! foo2 = INST [l, p; r, q] (Choice.result foo1)
        let! foo3 = MP (Choice.result foo2) (Choice.result th)

        let! foo4 = pth2 q
        let! foo5 = INST [l, p; r, q] (Choice.result foo4)
        let! foo6 = MP (Choice.result foo5) (Choice.result th)

        return foo3, foo6
        } //: Protected<thm0 * thm0>
        // TEMP : This is necessary until uses of this rule can be adapted to use the
        // _correct_ monadic return type for this function (Protected<thm0 * thm0>).
        |> function
            | Choice1Of2 (x, y) ->
                Choice1Of2 x, Choice1Of2 y
            | Choice2Of2 err ->
                Choice2Of2 err, Choice2Of2 err

/// Implements the transitivity of implication.
let IMP_TRANS = 
    let pq = parse_term @"p ==> q"
    let qr = parse_term @"q ==> r"
    let p_imp_q = dest_imp pq
    let r = rand qr
    let pth p = itlist DISCH [pq; qr; p] (MP (ASSUME qr) (MP (ASSUME pq) (ASSUME p)))
    fun (th1 : Protected<thm0>) (th2 : Protected<thm0>) -> 
        choice {
        let! p, q = p_imp_q
        let! r = r
        let! th1 = th1
        let! th2 = th2
        let tm1 = concl th1
        let tm2 = concl th2
        let! x, y = dest_imp tm1
        let! y', z = dest_imp tm2
        if y <> y' then
            return! Choice.failwith "IMP_TRANS"
        else
            let! foo1 = pth p
            let! foo2 = INST [x, p; y, q; z, r] (Choice.result foo1)
            let! foo3 = MP (Choice.result foo2) (Choice.result th1)
            return! MP (Choice.result foo3) (Choice.result th2)
        } : Protected<thm0>

(* ------------------------------------------------------------------------- *)
(* Rules for !                                                               *)
(* ------------------------------------------------------------------------- *)

let FORALL_DEF = new_basic_definition <| parse_term @"(!) = \P:A->bool. P = \x. T"

/// Term constructor for universal quantification.
let mk_forall = mk_binder "!"

/// Iteratively constructs a universal quantification.
let list_mk_forall(vs, bod) : Protected<term> = 
    Choice.List.fold (fun acc x -> mk_forall(x, acc)) bod vs

/// Specializes the conclusion of a theorem.
let SPEC =
    let P = parse_term @"P:A->bool"
    let x = parse_term @"x:A"
    let pth =
        choice {
        let! FORALL_DEF = FORALL_DEF
        let! foo1 = AP_THM (Choice.result FORALL_DEF) <| parse_term @"P:A->bool"
        let! foo2 = ASSUME <| parse_term @"(!)(P:A->bool)"
        let! th1 = EQ_MP (Choice.result foo1) (Choice.result foo2)
        let! foo3 = CONV_RULE BETA_CONV (Choice.result th1)
        let! th2 = AP_THM (Choice.result foo3) <| parse_term @"x:A"
        let! th3 = CONV_RULE (RAND_CONV BETA_CONV) (Choice.result th2)
        let! foo4 = EQT_ELIM (Choice.result th3)
        return! DISCH_ALL (Choice.result foo4)
        }
    fun tm (th : Protected<thm0>) ->
        choice {
        let! pth = pth
        let! th = th
        let tm' = concl th
        let! abs = rand tm'
        let! ba = bndvar abs
        let! db = dest_var ba
        let! foo1 = PINST [snd db, aty] [abs, P; tm, x] (Choice.result pth)
        let! foo2 = MP (Choice.result foo1) (Choice.result th)
        return! CONV_RULE BETA_CONV (Choice.result foo2)
        }
        |> Choice.mapError (fun e -> nestedFailure e "SPEC") : Protected<thm0>

/// Specializes zero or more variables in the conclusion of a theorem.
let SPECL tms th : Protected<thm0> =
    rev_itlist SPEC tms th
    |> Choice.mapError (fun e -> nestedFailure e "SPECL")

/// Specializes the conclusion of a theorem, returning the chosen variant.
let SPEC_VAR (th : Protected<thm0>) : Protected<term * thm0> =
    choice {
    let! th = th
    let ts = thm_frees th
    let tm = concl th
    let! rand_tm = rand tm
    let! t = bndvar rand_tm
    let! bv = variant ts t
    let! spec_bv = SPEC bv (Choice.result th)
    return bv, spec_bv
    }

/// Specializes the conclusion of a theorem with its own quantified variables.
let rec SPEC_ALL (th : Protected<thm0>) : Protected<thm0> = 
    choice {
    let! th = th
    let tm = concl th
    if is_forall tm then
        let! _, spec_var_th = SPEC_VAR (Choice.result th)
        return! SPEC_ALL (Choice.result spec_var_th)
    else
        return th
    }

/// Specializes a theorem, with type instantiation if necessary.
let ISPEC t (th : Protected<thm0>) : Protected<thm0> = 
    Choice.bind (dest_forall << concl) th
    |> Choice.mapError (fun e -> nestedFailure e "ISPEC: input theorem not universally quantified")
    |> Choice.bind (fun (x, _) ->
        choice {
        let! (_, ty) = dest_var x
        let! ty' = type_of t
        return! type_match ty ty' []
        }
        |> Choice.mapError (fun e ->
            nestedFailure e "ISPEC can't type-instantiate input theorem")
        |> Choice.bind (fun tyins ->
            SPEC t (INST_TYPE tyins th)))
    |> Choice.mapError (fun e -> nestedFailure e "ISPEC: type variable(s) free in assumptions")

/// Specializes a theorem zero or more times, with type instantiation if necessary.
let ISPECL tms (th : Protected<thm0>) : Protected<thm0> =
    choice {
    let! th = th
    if List.isEmpty tms then
        return th
    else
        let avs =
            let tm = concl th
            fst <| chop_list (length tms) (fst <| strip_forall tm)
            
        let! tyins =
            // NOTE: rewrite this
            match Choice.List.map (Choice.map snd << dest_var) avs, Choice.List.map type_of tms with
            | Success avs, Success tms ->
                Choice.List.fold (fun acc (x, y) -> type_match x y acc) [] (zip avs tms)
            | _ -> Choice.failwith "ISPECL.tyins"

        let! foo1 = INST_TYPE tyins (Choice.result th)
        return! SPECL tms (Choice.result foo1)
    }
    |> Choice.mapError (fun e -> nestedFailure e "ISPECL")

/// Generalizes the conclusion of a theorem.
let GEN =
    let pth =
        choice {
        let! FORALL_DEF = FORALL_DEF
        let! foo1 = AP_THM (Choice.result FORALL_DEF) <| parse_term @"P:A->bool"
        let! foo2 = CONV_RULE (RAND_CONV BETA_CONV) (Choice.result foo1)
        return! SYM (Choice.result foo2)
        }
    fun x (th : Protected<thm0>) ->
        choice {
        let! th = th
        let! _, ty = dest_var x
        let! qth = INST_TYPE [ty, aty] pth
        let qth_tm = concl qth
        let! ptm = Choice.compose rand rand <| qth_tm
        let! foo1 = EQT_INTRO (Choice.result th)
        let! th' = ABS x (Choice.result foo1)
        let! phi = lhand <| concl th'
        let! rth = INST [phi, ptm] (Choice.result qth)
        return! EQ_MP (Choice.result rth) (Choice.result th')
        } : Protected<thm0>

/// Generalizes zero or more variables in the conclusion of a theorem.
let GENL = itlist GEN

/// Generalizes the conclusion of a theorem over its own free variables.
let GEN_ALL (th : Protected<thm0>) : Protected<thm0> =
    choice {
    let! th = th
    let asl, c = dest_thm th
    let vars = subtract (frees c) (freesl asl)
    return! GENL vars (Choice.result th)
    }

(* ------------------------------------------------------------------------- *)
(* Rules for ?                                                               *)
(* ------------------------------------------------------------------------- *)

let EXISTS_DEF = new_basic_definition <| parse_term @"(?) = \P:A->bool. !q. (!x. P x ==> q) ==> q"

/// Term constructor for existential quantification.
let mk_exists = mk_binder "?"

/// Multiply existentially quantifies both sides of an equation using the given variables.
let list_mk_exists(vs, bod) : Protected<term> = 
    Choice.List.fold (fun acc x -> mk_exists(x, acc)) bod vs

/// Introduces existential quantification given a particular witness.
let EXISTS = 
    let P = parse_term @"P:A->bool"
    let x = parse_term @"x:A"
    let pth =
        let th1 = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM EXISTS_DEF P)
        let th2 = SPEC (parse_term @"x:A") (ASSUME <| parse_term @"!x:A. P x ==> Q")
        let th3 = DISCH (parse_term @"!x:A. P x ==> Q") (MP th2 (ASSUME <| parse_term @"(P:A->bool) x"))
        EQ_MP (SYM th1) (GEN (parse_term @"Q:bool") th3)

    fun (etm, stm) (th : Protected<thm0>) -> 
        choice {
        let! pth = pth
        let! qf, abs = dest_comb etm
        let bth = Choice.bind BETA_CONV (mk_comb(abs, stm))
        let! sty = type_of stm
        let cth : Protected<thm0> = PINST [sty, aty] [abs, P; stm, x] (Choice.result pth)
        return! PROVE_HYP (EQ_MP (SYM bth) th) cth
        }
        |> Choice.mapError (fun e -> nestedFailure e "EXISTS") : Protected<thm0>

/// Introduces an existential quantifier over a variable in a theorem.
let SIMPLE_EXISTS v (th : Protected<thm0>) : Protected<thm0> = 
    choice {
        let! tm = Choice.map concl th
        let! tm' = mk_exists(v, tm)
        return! EXISTS (tm', v) th
    }

/// Eliminates existential quantification using deduction from a particular witness.
let CHOOSE = 
    let P = parse_term @"P:A->bool"
    let Q = parse_term @"Q:bool"
    let pth = 
        let (th1 : Protected<thm0>) = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM EXISTS_DEF P)
        let (th2 : Protected<thm0>) = SPEC (parse_term @"Q:bool") (UNDISCH(fst(EQ_IMP_RULE th1)))
        DISCH_ALL(DISCH (parse_term @"(?) (P:A->bool)") (UNDISCH th2))
    fun (v, th1 : Protected<thm0>) (th2 : Protected<thm0>) -> 
        choice {
            let! pth = pth
            let! th1 = th1
            let! th2 = th2
            let! abs = rand <| concl th1
            let! bv, bod = dest_abs abs
            let! cmb = mk_comb(abs, v)
            let! pat = vsubst [v, bv] bod
            let th3 = CONV_RULE BETA_CONV (ASSUME cmb)
            let th4 = GEN v (DISCH cmb (MP (DISCH pat (Choice.result th2)) th3))
            let! (_, ty) = dest_var v
            let! tm2 = Choice.map concl (Choice.result th2)
            let th5 = 
                PINST [ty, aty] [abs, P; tm2, Q] (Choice.result pth)
            return! MP (MP th5 th4) (Choice.result th1)
        }
        |> Choice.mapError (fun e -> nestedFailure e "CHOOSE") : Protected<thm0>

/// Existentially quantifies a hypothesis of a theorem.
let SIMPLE_CHOOSE v (th : Protected<thm0>) : Protected<thm0> = 
    choice {
        let! th = th
        let! ts = Choice.map hyp (Choice.result th)
        let! t = mk_exists(v, hd ts)
        return! CHOOSE (v, ASSUME t) (Choice.result th)
    }

(* ------------------------------------------------------------------------- *)
(* Rules for \/                                                              *)
(* ------------------------------------------------------------------------- *)

let OR_DEF = new_basic_definition <| parse_term @"(\/) = \p q. !r. (p ==> r) ==> (q ==> r) ==> r"

/// Constructs a disjunction.
let mk_disj = mk_binary "\\/"

/// Constructs the disjunction of a list of terms.
let list_mk_disj : term list -> Protected<term> =
    Choice.List.reduceBack (curry mk_disj)

/// Introduces a right disjunct into the conclusion of a theorem.
let DISJ1 = 
    let P = parse_term @"P:bool"
    let Q = parse_term @"Q:bool"
    let pth = 
        let (th1 : Protected<thm0>) = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM OR_DEF <| parse_term @"P:bool")
        let (th2 : Protected<thm0>) = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM th1 <| parse_term @"Q:bool")
        let (th3 : Protected<thm0>) = MP (ASSUME <| parse_term @"P ==> t") (ASSUME <| parse_term @"P:bool")
        let (th4 : Protected<thm0>) = GEN (parse_term @"t:bool") (DISCH (parse_term @"P ==> t") (DISCH (parse_term @"Q ==> t") th3))
        EQ_MP (SYM th2) th4
    fun (th : Protected<thm0>) tm -> 
        Choice.map concl th
        |> Choice.bind (fun tm' -> PROVE_HYP th (INST [tm', P; tm, Q] pth))
        |> Choice.mapError (fun e -> nestedFailure e "DISJ1") : Protected<thm0>

/// Introduces a left disjunct into the conclusion of a theorem.
let DISJ2 = 
    let P = parse_term @"P:bool"
    let Q = parse_term @"Q:bool"
    let pth = 
        let (th1 : Protected<thm0>) = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM OR_DEF <| parse_term @"P:bool")
        let (th2 : Protected<thm0>) = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM th1 <| parse_term @"Q:bool")
        let (th3 : Protected<thm0>) = MP (ASSUME <| parse_term @"Q ==> t") (ASSUME <| parse_term @"Q:bool")
        let (th4 : Protected<thm0>) = GEN (parse_term @"t:bool") (DISCH (parse_term @"P ==> t") (DISCH (parse_term @"Q ==> t") th3))
        EQ_MP (SYM th2) th4
    fun tm (th : Protected<thm0>) -> 
        Choice.map concl th
        |> Choice.bind (fun tm' -> PROVE_HYP th (INST [tm, P; tm', Q] pth))
        |> Choice.mapError (fun e -> nestedFailure e "DISJ2") : Protected<thm0>

/// Eliminates disjunction by cases.
let DISJ_CASES = 
    let P = parse_term @"P:bool"
    let Q = parse_term @"Q:bool"
    let R = parse_term @"R:bool"
    let pth = 
        let (th1 : Protected<thm0>) = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM OR_DEF <| parse_term @"P:bool")
        let (th2 : Protected<thm0>) = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM th1 <| parse_term @"Q:bool")
        let (th3 : Protected<thm0>) = SPEC (parse_term @"R:bool") (EQ_MP th2 (ASSUME <| parse_term @"P \/ Q"))
        UNDISCH(UNDISCH th3)
    fun (th0 : Protected<thm0>) (th1 : Protected<thm0>) (th2 : Protected<thm0>) -> 
        choice {
            let! c1 = Choice.map concl th1
            let! c2 = Choice.map concl th2
            if not(aconv c1 c2) then 
                return! Choice.failwith "DISJ_CASES"
            else 
                let! l, r = Choice.bind (dest_disj << concl) th0
                let th = INST [l, P; r, Q; c1, R] pth
                return! PROVE_HYP (DISCH r th2) (PROVE_HYP (DISCH l th1) (PROVE_HYP th0 th))                        
        }
        |> Choice.mapError (fun e -> nestedFailure e "DISJ_CASES") : Protected<thm0>

/// Disjoins hypotheses of two theorems with same conclusion.
let SIMPLE_DISJ_CASES (th1 : Protected<thm0>) (th2 : Protected<thm0>) : Protected<thm0> = 
    choice {
    let! th1 = th1
    let! th2 = th2
    let tl1 = hyp th1
    let tl2 = hyp th2
    let! tm = mk_disj(hd tl1, hd tl2)
    let! assumption = ASSUME tm
    return! DISJ_CASES (Choice.result assumption) (Choice.result th1) (Choice.result th2)
    }

(* ------------------------------------------------------------------------- *)
(* Rules for negation and falsity.                                           *)
(* ------------------------------------------------------------------------- *)

let F_DEF = new_basic_definition <| parse_term @"F = !p:bool. p"
let NOT_DEF = new_basic_definition <| parse_term @"(~) = \p. p ==> F"

/// Constructs a logical negation.
let mk_neg : term -> Protected<_> = 
    let neg_tm = parse_term @"(~)"
    fun tm ->
        mk_comb(neg_tm, tm)
        |> Choice.mapError (fun e -> nestedFailure e "mk_neg")

/// <summary>
/// Transforms <c>|- ~t</c> into <c>|- t ==> F</c>.
/// </summary>
let NOT_ELIM = 
    let P = parse_term @"P:bool"
    let pth =
        choice {
        // CLEAN : Rename this value to something sensible.
        let! foo1 = AP_THM NOT_DEF P
        return! CONV_RULE (RAND_CONV BETA_CONV) (Choice.result foo1)
        }

    fun (th : Protected<thm0>) ->
        choice {
        let! foo1 = pth
        let! th = th
        let! tm = rand <| concl th
        // CLEAN : Rename these values to something sensible.
        let! foo2 = INST [tm, P] (Choice.result foo1)
        return! EQ_MP (Choice.result foo2) (Choice.result th)
        }
        |> Choice.mapError (fun e -> nestedFailure e "NOT_ELIM") : Protected<thm0>

/// <summary>
/// Transforms <c>|- t ==> F</c> into <c>|- ~t</c>.
/// </summary>
let NOT_INTRO = 
    let P = parse_term @"P:bool"
    let pth =
        choice {
        // CLEAN : Rename these values to something sensible.
        let! foo1 = AP_THM NOT_DEF P
        let! foo2 = CONV_RULE (RAND_CONV BETA_CONV) (Choice.result foo1)
        return! SYM (Choice.result foo2)
        }
    fun (th : Protected<thm0>) ->
        choice {
        let! foo3 = pth
        let! th = th
        let tm = concl th
        // CLEAN : Rename these values to something sensible.
        let! foo1 = rator tm
        let! foo2 = rand foo1
        let! foo4 = INST [tm, P] (Choice.result foo3)
        return! EQ_MP (Choice.result foo4) (Choice.result th)
        }
        |> Choice.mapError (fun e -> nestedFailure e "NOT_INTRO") : Protected<thm0>

/// Converts negation to equality with F.
let EQF_INTRO = 
    let P = parse_term @"P:bool"
    let pth =
        choice {
        // CLEAN : Rename these values to something sensible.
        let! assume_not_p = ASSUME <| parse_term @"~ P"
        let! th1 = NOT_ELIM (Choice.result assume_not_p)
        let! assume_f = ASSUME <| parse_term @"F"
        let! foo1 = EQ_MP F_DEF (Choice.result assume_f)
        let! foo2 = SPEC P (Choice.result foo1)
        let! th2 = DISCH (parse_term @"F") (Choice.result foo2)
        let! foo3 = IMP_ANTISYM_RULE (Choice.result th1) (Choice.result th2)
        return! DISCH_ALL (Choice.result foo3)
        }
    fun (th : Protected<thm0>) ->
        choice {
        let! th = th
        let! tm = rand <| concl th
        // CLEAN : Rename these values to something sensible.
        let! foo1 = pth
        let! foo2 = INST [tm, P] (Choice.result foo1)
        return! MP (Choice.result foo2) (Choice.result th)
        }
        |> Choice.mapError (fun e -> nestedFailure e "EQF_INTRO") : Protected<thm0>

/// Replaces equality with F by negation.
let EQF_ELIM =
    let P = parse_term @"P:bool"
    let pth =
        choice {
        // CLEAN : Rename these values to something sensible.
        let! foo1 = ASSUME <| parse_term @"P = F"
        let! foo2 = ASSUME <| parse_term @"P:bool"
        let! th1 = EQ_MP (Choice.result foo1) (Choice.result foo2)
        let! foo3 = EQ_MP F_DEF (Choice.result th1)
        let! foo4 = SPEC (parse_term @"F") (Choice.result foo3)
        let! th2 = DISCH P (Choice.result foo4)
        let! foo5 = NOT_INTRO (Choice.result th2)
        return! DISCH_ALL (Choice.result foo5)
        }
    fun (th : Protected<thm0>) ->
        choice {
        let! th = th
        let tm = concl th
        // CLEAN : Rename these values to something sensible.
        let! foo1 = rator tm
        let! foo2 = rand foo1
        let! foo3 = pth
        let! foo4 = INST [tm, P] (Choice.result foo3)
        return! MP (Choice.result foo4) (Choice.result th)
        }
        |> Choice.mapError (fun e -> nestedFailure e "EQF_ELIM") : Protected<thm0>

/// Implements the intuitionistic contradiction rule.
let CONTR =
    let P = parse_term @"P:bool"
    let f_tm = parse_term @"F"
    let pth =
        choice {
        // CLEAN : Rename these values to something sensible.
        let! foo1 = ASSUME f_tm
        let! foo2 = EQ_MP F_DEF (Choice.result foo1)
        return! SPEC P (Choice.result foo2)
        }
    fun tm (th : Protected<thm0>) ->
        choice {
        let! th = th
        let tm' = concl th
        if tm' <> f_tm then
            return! Choice.failwith "CONTR"
        else
            // CLEAN : Rename these values to something sensible.
            let! foo1 = pth
            let! foo2 = INST [tm, P] (Choice.result foo1)
            return! PROVE_HYP (Choice.result th) (Choice.result foo2)
        } : Protected<thm0>

(* ------------------------------------------------------------------------- *)
(* Rules for unique existence.                                               *)
(* ------------------------------------------------------------------------- *)

let EXISTS_UNIQUE_DEF =
    new_basic_definition <| parse_term @"(?!) = \P:A->bool. ((?) P) /\ (!x y. P x /\ P y ==> x = y)"

/// Term constructor for unique existence.
let mk_uexists = mk_binder "?!"

/// Deduces existence from unique existence.
let EXISTENCE = 
    let P = parse_term @"P:A->bool"
    let pth = 
        let th1 = CONV_RULE (RAND_CONV BETA_CONV) (AP_THM EXISTS_UNIQUE_DEF P)
        let th2 = UNDISCH(fst(EQ_IMP_RULE th1))
        DISCH_ALL(CONJUNCT1 th2)
    fun (th : Protected<thm0>) -> 
        choice {
            let! abs = Choice.bind (rand << concl) th
            let! tm = bndvar abs
            let! (_, ty) = dest_var tm
            return! MP (PINST [ty, aty] [abs, P] pth) th
        }
        |> Choice.mapError (fun e -> nestedFailure e "EXISTENCE") : Protected<thm0>
