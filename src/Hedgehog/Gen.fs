namespace Hedgehog

open System

/// A generator for values and shrink trees of type 'a.
[<Struct>]
type Gen<'a> =
    | Gen of Random<Tree<'a>>

module Gen =

    let ofRandom (r : Random<Tree<'a>>) : Gen<'a> =
        Gen r

    let toRandom (Gen r : Gen<'a>) : Random<Tree<'a>> =
        r

    let delay (f : unit -> Gen<'a>) : Gen<'a> =
        Random.delay (toRandom << f) |> ofRandom

    let tryFinally (after : unit -> unit) (m : Gen<'a>) : Gen<'a> =
        toRandom m |> Random.tryFinally after |> ofRandom

    let tryWith (k : exn -> Gen<'a>) (m : Gen<'a>) : Gen<'a> =
        toRandom m |> Random.tryWith (toRandom << k) |> ofRandom

    let create (shrink : 'a -> seq<'a>) (random : Random<'a>) : Gen<'a> =
        random |> Random.map (Tree.unfold id shrink) |> ofRandom

    let constant (x : 'a) : Gen<'a> =
        Tree.singleton x |> Random.constant |> ofRandom

    let mapRandom (f : Random<Tree<'a>> -> Random<Tree<'b>>) (g : Gen<'a>) : Gen<'b> =
        toRandom g |> f |> ofRandom

    let mapTree (f : Tree<'a> -> Tree<'b>) (g : Gen<'a>) : Gen<'b> =
        mapRandom (Random.map f) g

    let map (f : 'a -> 'b) (g : Gen<'a>) : Gen<'b> =
        mapTree (Tree.map f) g

    let private bindRandom (k : 'a -> Random<Tree<'b>>) (m : Random<Tree<'a>>) : Random<Tree<'b>> =
        Hedgehog.Random (fun seed0 size ->
            let seed1, seed2 =
                Seed.split seed0

            let run (seed : Seed) (random : Random<'x>) : 'x =
                Random.run seed size random

            Tree.bind (k >> run seed2) (run seed1 m))

    let bind (k : 'a -> Gen<'b>) (m : Gen<'a>) : Gen<'b> =
        toRandom m |> bindRandom (toRandom << k) |> ofRandom

    let private applyRandom (rta : Random<Tree<'a>>) (rtf : Random<Tree<'a -> 'b>>) : Random<Tree<'b>> =
        rtf |> Random.bind (fun tf ->
        rta |> Random.map  (fun ta -> Tree.apply ta tf))

    let apply (ga : Gen<'a>) (gf : Gen<'a -> 'b>) : Gen<'b> =
        applyRandom (toRandom ga) (toRandom gf) |> ofRandom

    let map2 (f : 'a -> 'b -> 'c) (ga : Gen<'a>) (gb : Gen<'b>) : Gen<'c> =
        constant f
        |> apply ga
        |> apply gb

    let map3 (f : 'a -> 'b -> 'c -> 'd) (ga : Gen<'a>) (gb : Gen<'b>) (gc : Gen<'c>) : Gen<'d> =
        constant f
        |> apply ga
        |> apply gb
        |> apply gc

    let map4 (f : 'a -> 'b -> 'c -> 'd -> 'e) (ga : Gen<'a>) (gb : Gen<'b>) (gc : Gen<'c>) (gd : Gen<'d>) : Gen<'e> =
        constant f
        |> apply ga
        |> apply gb
        |> apply gc
        |> apply gd

    let zip (ga : Gen<'a>) (gb : Gen<'b>) : Gen<'a * 'b> =
        map2 (fun a b -> a, b) ga gb

    let zip3 (ga : Gen<'a>) (gb : Gen<'b>) (gc : Gen<'c>) : Gen<'a * 'b * 'c> =
        map3 (fun a b c -> a, b, c) ga gb gc

    let zip4 (ga : Gen<'a>) (gb : Gen<'b>) (gc : Gen<'c>) (gd : Gen<'d>) : Gen<'a * 'b * 'c * 'd> =
        map4 (fun a b c d -> a, b, c, d) ga gb gc gd

    let tuple  (g : Gen<'a>) : Gen<'a * 'a> =
        zip g g

    let tuple3 (g : Gen<'a>) : Gen<'a * 'a * 'a> =
        zip3 g g g

    let tuple4 (g : Gen<'a>) : Gen<'a * 'a * 'a * 'a> =
        zip4 g g g g

    type Builder internal () =
        let rec loop p m =
            if p () then
                m |> bind (fun _ -> loop p m)
            else
                constant ()
        member __.Return(a) : Gen<'a> = constant a
        member __.ReturnFrom(g) : Gen<'a> = g
        member __.BindReturn(g, f) = map f g
        member __.MergeSources(ga, gb) = zip ga gb
        member __.Bind(g, f) = g |> bind f
        member __.For(xs, k) =
            let xse = (xs :> seq<'a>).GetEnumerator ()
            using xse (fun xse ->
                let mv = xse.MoveNext
                let kc = delay (fun () -> k xse.Current)
                loop mv kc)
        member __.Combine(m, n) = m |> bind (fun () -> n)
        member __.Delay(f) = delay f
        member __.Zero() = constant ()

    let private gen = Builder ()

    //
    // Combinators - Shrinking
    //

    /// Prevent a 'Gen' from shrinking.
    let noShrink (g : Gen<'a>) : Gen<'a> =
        let drop (Node (x, _)) =
            Node (x, Seq.empty)
        mapTree drop g

    /// Apply an additional shrinker to all generated trees.
    let shrinkLazy (f : 'a -> seq<'a>) (g : Gen<'a>) : Gen<'a> =
        mapTree (Tree.expand f) g

    /// Apply an additional shrinker to all generated trees.
    let shrink (f : 'a -> List<'a>) (g : Gen<'a>) : Gen<'a>  =
        shrinkLazy (Seq.ofList << f) g

    //
    // Combinators - Size
    //

    /// Used to construct generators that depend on the size parameter.
    let sized (f : Size -> Gen<'a>) : Gen<'a> =
        Random.sized (toRandom << f) |> ofRandom

    /// Overrides the size parameter. Returns a generator which uses the
    /// given size instead of the runtime-size parameter.
    let resize (n : int) (g : Gen<'a>) : Gen<'a> =
        mapRandom (Random.resize n) g

    /// Adjust the size parameter, by transforming it with the given
    /// function.
    let scale (f : int -> int) (g : Gen<'a>) : Gen<'a> =
        sized (fun n ->
            resize (f n) g)

    //
    // Combinators - Numeric
    //

    /// Generates a random number in the given inclusive range.
    let inline integral (range : Range<'a>) : Gen<'a> =
        range
        |> Random.integral
        |> create (range |> Range.origin |> Shrink.towards)
        // The code below was added in
        // https://github.com/hedgehogqa/fsharp-hedgehog/pull/239
        // It is more efficient than the code above in that the shrink tree is duplicate free.
        // However, such a tree does not work well when combining applicatively.
        // The advantage of a duplicate-free shrink tree is less time spent shrinking.
        // The advantage of applicatively combining tree is a potentially smaller shrunken value.
        // The latter is better, so reverting to the previous shrink tree for now.
        // Maybe it is possible to achieve the best of both.
        //range
        //|> Random.integral
        //|> Random.map (range |> Range.origin |> Shrink.createTree)
        //|> ofRandom

    //
    // Combinators - Choice
    //

    let private crashEmpty (arg : string) : 'b =
        invalidArg arg (sprintf "'%s' must have at least one element" arg)

    /// Randomly selects one of the values in the list.
    /// <i>The input list must be non-empty.</i>
    let item (xs0 : seq<'a>) : Gen<'a> = gen {
        let xs = Array.ofSeq xs0
        if Array.isEmpty xs then
            return crashEmpty "xs"
        else
            let! ix = Range.ofArray xs |> integral
            return Array.item ix xs
    }

    /// Uses a weighted distribution to randomly select one of the gens in the list.
    /// This generator shrinks towards the first generator in the list.
    /// <i>The input list must be non-empty.</i>
    let frequency (xs0 : seq<int * Gen<'a>>) : Gen<'a> =
        let xs =
            List.ofSeq xs0

        let total =
            List.sumBy fst xs

        let rec pick n = function
            | [] ->
                crashEmpty "xs"
            | (k, y) :: ys ->
                if n <= k then
                    y
                else
                    pick (n - k) ys

        let f n =
            let smallWeights =
                xs
                |> List.map fst
                |> List.scan (+) 0
                |> List.pairwise
                |> List.takeWhile (fun (a, _) -> a < n)
                |> List.map snd
                |> List.toArray
            let length = smallWeights |> Array.length
            Shrink.createTree 0 (length - 1)
            |> Tree.map (fun i -> smallWeights.[i])

        gen {
            let! n =
                Range.constant 1 total
                |> integral
                |> toRandom
                |> Random.map (Tree.outcome >> f)
                |> ofRandom
            return! pick n xs
        }

    /// Randomly selects one of the gens in the list.
    /// <i>The input list must be non-empty.</i>
    let choice (xs0 : seq<Gen<'a>>) : Gen<'a> = gen {
        let xs = Array.ofSeq xs0
        if Array.isEmpty xs then
            return crashEmpty "xs" xs
        else
            let! ix = Range.ofArray xs |> integral
            return! Array.item ix xs
    }

    /// Randomly selects from one of the gens in either the non-recursive or the
    /// recursive list. When a selection is made from the recursive list, the size
    /// is halved. When the size gets to one or less, selections are no longer made
    /// from the recursive list.
    /// <i>The first argument (i.e. the non-recursive input list) must be non-empty.</i>
    let choiceRec (nonrecs : seq<Gen<'a>>) (recs : seq<Gen<'a>>) : Gen<'a> =
        sized (fun n ->
            let scaledRecs =
                if n <= 1 then
                    Seq.empty
                else
                    recs
                    |> Seq.map (scale (fun x -> x / 2))

            scaledRecs
            |> Seq.append nonrecs
            |> choice
        )

    //
    // Combinators - Conditional
    //

    /// More or less the same logic as suchThatMaybe from QuickCheck, except
    /// modified to ensure that the shrinks also obey the predicate.
    let private tryFilterRandom (p : 'a -> bool) (r0 : Random<Tree<'a>>) : Random<Option<Tree<'a>>> =
        let rec tryN k = function
            | 0 ->
                Random.constant None
            | n ->
                let r = Random.resize (2 * k + n) r0
                r |> Random.bind (fun x ->
                    if p (Tree.outcome x) then
                        Tree.filter p x |> Some |> Random.constant
                    else
                        tryN (k + 1) (n - 1))

        Random.sized (tryN 0 << max 1)

    /// Generates a value that satisfies a predicate.
    let filter (p : 'a -> bool) (g : Gen<'a>) : Gen<'a> =
        let rec loop () =
            toRandom g
            |> tryFilterRandom p
            |> Random.bind (function
                | None ->
                    Random.sized (fun n ->
                        Random.resize (n + 1) (Random.delay loop))
                | Some x ->
                    Random.constant x)

        loop ()
        |> ofRandom

    /// Tries to generate a value that satisfies a predicate.
    let tryFilter (p : 'a -> bool) (g : Gen<'a>) : Gen<'a option> =
        toRandom g
        |> tryFilterRandom p
        |> Random.bind (OptionTree.sequence >> Random.constant)
        |> ofRandom

    /// Runs an option generator until it produces a 'Some'.
    let some (g : Gen<'a option>) : Gen<'a> =
        filter Option.isSome g |> bind (function
            | Some x ->
                constant x
            | None ->
                invalidOp "internal error, unexpected None")

    //
    // Combinators - Collections
    //

    /// Generates a 'None' part of the time.
    let option (g : Gen<'a>) : Gen<'a option> =
        sized (fun n ->
            frequency [
                2, constant None
                1 + n, map Some g
            ])

    /// Generates a list using a 'Range' to determine the length.
    let list (range : Range<int>) (gen : Gen<'a>) : Gen<List<'a>> =
        let sequence minLength trees =
            trees
            |> Seq.toList
            |> Shrink.sequenceList
            |> Tree.filter (fun list -> List.length list >= minLength)

        let replicate minLength times =
            toRandom gen
            |> Random.replicate times
            |> Random.map (sequence minLength)

        let sizedList size =
            let minLength = Range.lowerBound size range
            range
            |> Random.integral
            |> Random.bind (replicate minLength)

        Random.sized sizedList
        |> ofRandom

    /// Generates an array using a 'Range' to determine the length.
    let array (range : Range<int>) (g : Gen<'a>) : Gen<array<'a>> =
        list range g |> map Array.ofList

    /// Generates a sequence using a 'Range' to determine the length.
    let seq (range : Range<int>) (g : Gen<'a>) : Gen<seq<'a>> =
        list range g |> map Seq.ofList

    //
    // Combinators - Characters
    //

    // Generates a random character in the specified range.
    let char (lo : char) (hi : char) : Gen<char> =
        Range.constant (int lo) (int hi)
        |> integral
        |> map char

    /// Generates a Unicode character, including invalid standalone surrogates:
    /// '\000'..'\65535'
    let unicodeAll : Gen<char> =
        let lo = Char.MinValue
        let hi = Char.MaxValue
        char lo hi

    /// Generates a random digit.
    let digit : Gen<char> =
        char '0' '9'

    /// Generates a random lowercase character.
    let lower : Gen<char> =
        char 'a' 'z'

    /// Generates a random uppercase character.
    let upper : Gen<char> =
        char 'A' 'Z'

    /// Generates an ASCII character: '\000'..'\127'
    let ascii : Gen<char> =
        char '\000' '\127'

    /// Generates a Latin-1 character: '\000'..'\255'
    let latin1 : Gen<char> =
        char '\000' '\255'

    /// Generates a Unicode character, excluding noncharacters
    /// ('\65534', '\65535') and invalid standalone surrogates
    /// ('\000'..'\65535' excluding '\55296'..'\57343').
    let unicode : Gen<char> =
        let isNoncharacter x =
               x = Operators.char 65534
            || x = Operators.char 65535
        unicodeAll
        |> filter (not << isNoncharacter)
        |> filter (not << Char.IsSurrogate)

    /// Generates a random alpha character.
    let alpha : Gen<char> =
        choice [lower; upper]

    /// Generates a random alpha-numeric character.
    let alphaNum : Gen<char> =
        choice [lower; upper; digit]

    /// Generates a random string using 'Range' to determine the length and the
    /// specified character generator.
    let string (range : Range<int>) (g : Gen<char>) : Gen<string> =
        array range g
        |> map String

    //
    // Combinators - Primitives
    //

    /// Generates a random boolean.
    let bool : Gen<bool> =
        item [false; true]

    /// Generates a random byte.
    let byte (range : Range<byte>) : Gen<byte> =
        integral range

    /// Generates a random signed byte.
    let sbyte (range : Range<sbyte>) : Gen<sbyte> =
        integral range

    /// Generates a random signed 16-bit integer.
    let int16 (range : Range<int16>) : Gen<int16> =
        integral range

    /// Generates a random unsigned 16-bit integer.
    let uint16 (range : Range<uint16>) : Gen<uint16> =
        integral range

    /// Generates a random signed 32-bit integer.
    let int32 (range : Range<int32>) : Gen<int32> =
        integral range

    /// Generates a random unsigned 32-bit integer.
    let uint32 (range : Range<uint32>) : Gen<uint32> =
        integral range

    /// Generates a random signed 64-bit integer.
    let int64 (range : Range<int64>) : Gen<int64> =
        integral range

    /// Generates a random unsigned 64-bit integer.
    let uint64 (range : Range<uint64>) : Gen<uint64> =
        integral range

    /// Generates a random 64-bit floating point number.
    let double (range : Range<double>) : Gen<double> =
        Random.double range
        |> create (Shrink.towardsDouble (Range.origin range))

    /// Generates a random 32-bit floating point number.
    let single (range : Range<single>) : Gen<single> =
        double (Range.map ExtraTopLevelOperators.double range) |> map single

    /// Generates a random decimal floating-point number.
    let decimal (range : Range<decimal>) : Gen<decimal> =
        double (Range.map ExtraTopLevelOperators.double range) |> map decimal

    /// Generates a random big integer.
    let bigint (range : Range<bigint>) : Gen<bigint> =
        integral range

    //
    // Combinators - Constructed
    //

    /// Generates a random globally unique identifier.
    let guid : Gen<Guid> = gen {
        let! bs = Range.constantBounded () |> byte |> array (Range.singleton 16)
        return Guid bs
    }

    /// Generates a random DateTime using the specified range.
    /// For example:
    ///   let range =
    ///      Range.constantFrom
    ///          (DateTime (2000, 1, 1)) DateTime.MinValue DateTime.MaxValue
    ///   Gen.dateTime range
    let dateTime (range : Range<DateTime>) : Gen<DateTime> =
        gen {
            let! ticks = range |> Range.map (fun dt -> dt.Ticks) |> integral
            return DateTime ticks
        }

    /// Generates a random DateTimeOffset using the specified range.
    let dateTimeOffset (range : Range<DateTimeOffset>) : Gen<DateTimeOffset> =
        gen {
            let! ticks = range |> Range.map (fun dt -> dt.Ticks) |> integral
            // Ensure there is no overflow near the edges when adding the offset
            let minOffsetMinutes =
              max
                (-14L * 60L)
                ((DateTimeOffset.MaxValue.Ticks - ticks) / TimeSpan.TicksPerMinute * -1L)
            let maxOffsetMinutes =
              min
                (14L * 60L)
                ((ticks - DateTimeOffset.MinValue.Ticks) / TimeSpan.TicksPerMinute)
            let! offsetMinutes = int32 (Range.linearFrom 0 (Operators.int minOffsetMinutes) (Operators.int maxOffsetMinutes))
            return DateTimeOffset(ticks, TimeSpan.FromMinutes (Operators.float offsetMinutes))
        }
        
#if !FABLE_COMPILER
    /// Generates a random TimeSpan using the specified range.
    let timeSpan (range : Range<TimeSpan>) : Gen<TimeSpan> =
        range
        |> Range.map (fun x -> x.Ticks (* Fable can't do this *))
        |> int64
        |> map TimeSpan.FromTicks
#endif

    //
    // Sampling
    //

    let sampleTree (size : Size) (count : int) (g : Gen<'a>) : seq<Tree<'a>> =
        let seed = Seed.random ()
        toRandom g
        |> Random.replicate count
        |> Random.run seed size

    let sample (size : Size) (count : int) (g : Gen<'a>) : seq<'a> =
        sampleTree size count g
        |> Seq.map Tree.outcome

    /// Run a generator. The size passed to the generator is always 30;
    /// if you want another size then you should explicitly use 'resize'.
    let generateTree (g : Gen<'a>) : Tree<'a> =
        let seed = Seed.random ()
        toRandom g
        |> Random.run seed 30

    let renderSample (gen : Gen<'a>) : string =
        String.concat Environment.NewLine [
            let forest = sampleTree 10 5 gen
            for tree in forest do
                yield "=== Outcome ==="
                yield sprintf "%A" (Tree.outcome tree)
                yield "=== Shrinks ==="
                for shrink in Tree.shrinks tree do
                    yield sprintf "%A" (Tree.outcome shrink)
                yield "."
        ]

    module Operators =
        let (<!>) f g = map f g
        let (<*>) gf g = apply g gf
        let (>>=) g f = bind f g

[<AutoOpen>]
module GenBuilder =
    let gen = Gen.Builder ()
