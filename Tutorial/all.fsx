﻿#load "hol.fsx"

open NHol.lib
open NHol.fusion
open NHol.basics
open NHol.nets
open NHol.printer
open NHol.preterm
open NHol.parser
open NHol.equal
//open NHol.bool
//open NHol.drule
//open NHol.tactics
//open NHol.itab
//open NHol.simp
//open NHol.theorems
//open NHol.ind_defs
//open NHol.``class``
//open NHol.trivia
//open NHol.canon
//open NHol.meson
//open NHol.quot
//open NHol.pair
//open NHol.nums
//open NHol.recursion
//open NHol.arith
//open NHol.wf
//open NHol.calc_num
//open NHol.normalizer
//open NHol.grobner
//open NHol.ind_types
//open NHol.lists
//open NHol.realax
//open NHol.calc_int
//open NHol.realarith
//open NHol.real
//open NHol.int
//open NHol.sets
//open NHol.iterate
//open NHol.cart
//open NHol.define
//open NHol.help
//open NHol.database

let b = can (assoc "x") [];;

let x = parse_term "x:bool"
let x1 = parse_term "x + 1"
