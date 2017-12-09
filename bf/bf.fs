( Bootstrap Forth, my first Forth for the CFM. )


\ -----------------------------------------------------------------------------
\ Instruction-level machine-code primitives.

\ The ALU instructions are written without a fused return for clarity, but the
\ effect of ; will fuse a return into the final instruction. The result is a
\ definition containing a single returning instruction, which will be noticed
\ by the inlining algorithm. As a result, these definitions function as an
\ assembler.

\ Instructions that map to traditional Forth words:
: +      [ $6203 asm, ] ;
: swap   [ $6180 asm, ] ;
: over   [ $6181 asm, ] ;
: nip    [ $6003 asm, ] ;
: lshift [ $6d03 asm, ] ;
: rshift [ $6903 asm, ] ;
: dup    [ $6081 asm, ] ;
: =      [ $6703 asm, ] ;
: drop   [ $6103 asm, ] ;
: invert [ $6600 asm, ] ;
: @      [ $6c00 asm, ] ;
: or     [ $6403 asm, ] ;
: and    [ $6303 asm, ] ;
: xor    [ $6503 asm, ] ;
: -      [ $6a03 asm, ] ;
: <      [ $6803 asm, ] ;
: u<     [ $6f03 asm, ] ;

\ Useful compound instructions are named for the equivalent sequence of Forth
\ words:
: 2dup_!_drop  ( x addr -- x )  [ $6123 asm, ] ;
: dup_@        ( addr -- addr x )  [ $6c81 asm, ] ;
: 2dup_xor     ( a b -- a b a^b )  [ $6581 asm, ] ;
: over_and     ( a b -- a b&a )  [ $6300 asm, ] ;
: 2dup_and     ( a b -- a b b&a )  [ $6381 asm, ] ;
: over_=       ( a b -- a b=a )  [ $6700 asm, ] ;
: 2dup_+       ( a b -- a b b+a )  [ $6281 asm, ] ;

\ -----------------------------------------------------------------------------
\ Support for CONSTANT. CONSTANT is implemented as if written with DOES>, but
\ we need to start slinging constants before we have DOES> (or CREATE or : for
\ that matter) so we must roll it by hand.

\ A word created with CONSTANT will call (docon) as its only instruction.
\ Immediately following the call is a cell containing the value of the
\ constant.  Thus, (docon) must consume its return address and load the cell.

\ We're working without a definition for R> here, because we're going to write
\ an optimizing assembler before writing R> .

: (docon)  ( -- x ) ( R: addr -- )
  [ $6b8d asm, ]  ( machine code for R> )
  @ ;

\ -----------------------------------------------------------------------------
\ Useful CONSTANTs.

\ System variables. These memory locations are wired into the bootstrap
\ program.
4 constant LATEST  ( head of wordlist )
6 constant DP  ( dictionary pointer, read by HERE )
8 constant U0  ( address of user area )
10 constant STATE  ( compiler state )
12 constant FREEZEP  ( high-water-mark for code immune to fusion )

$FFFF constant true  ( also abused as -1 below, since it's cheaper )
0 constant false
2 constant cell


\ -----------------------------------------------------------------------------
\ More useful Forth words.

: tuck  ( a b -- b a b )  swap over ;
: !  ( x addr -- )  2dup_!_drop drop ;
: +!  ( x addr -- )  tuck @ + swap ! ;
: aligned  ( addr -- a-addr )  dup 1 and + ;

: c@  ( c-addr -- c )
  dup_@
  swap 1 and if ( lsb set )
    8 rshift
  else
    $FF and
  then ;

: 2dup over over ;
: 2drop drop drop ;
: 1+ 1 + ;

\ -----------------------------------------------------------------------------
\ The Dictionary and the Optimizing Assembler.

\ Because the host manipulates the dictionary, it's important to keep the
\ layout consistent between us and the host. This is why LATEST, DP, and
\ FREEZEP are part of the system variables block.

: here  ( -- addr )  DP @ ;
: allot  DP +! ;
: raw,  here !  cell allot ;
: cells  1 lshift ;

\ We've been calling the host's emulation of asm, for building words out of
\ machine code. Here's the actual definition.
: asm,
  here FREEZEP @ xor if  ( Fusion is a possibility... )
    here cell - @   ( new-inst prev-inst )

    over $700C = if ( if we're assembling a bare return instruction... )
      dup $F04C and $6000 = if  ( ...on a non-returning ALU instruction )
        true cells allot
        nip  $100C or  asm, exit
      then
      dup $E000 and $4000 = if  ( ...on a call )
        true cells allot
        nip $1FFF and  asm, exit
      then
    then

    ( No patterns matched. )
    drop
  then
  ( Fusion was not possible, simply append the bits. )
  raw, ;

\ Sometimes we want a clear separation between one instruction and the next.
\ For example, if the second instruction is the target of control flow like a
\ loop or if. The word freeze updates FREEZEP, preventing fusion of any
\ instructions already present in the dictionary.
: freeze  here FREEZEP ! ;

\ Encloses a data cell in the dictionary. Prevents misinterpretation of the
\ data as instructions by using freeze . Thus using , to assemble machine
\ instructions will *work* but the results will have poor performance.
: ,  ( x -- )  raw, freeze ;

\ -----------------------------------------------------------------------------
\ Aside: IMMEDIATE and STATE manipulation.

\ Sets the flags on the most recent definition.
: immediate
  LATEST @  cell +  ( nfa )
  dup c@ + 1+ aligned
  true swap ! ;

\ Switches from compilation to interpretation.
: [ 0 STATE ! ; immediate
\ Switches from interpretation to compilation.
: ] 1 STATE ! ;

\ -----------------------------------------------------------------------------
\ Forth return stack words. These are machine-language primitives like we have
\ above, but since they affect the return stack, they (1) must be inlined at
\ their site of use, and (2) cannot be automatically inlined by the compiler,
\ because that would change the meaning of the code. Thus these are our first
\ IMMEDIATE definitions as they have side effects on the current definition.

\ It would be reasonable to describe this as the start of the compiler.

: r>  $6b8d asm, ; immediate
: >r  $6147 asm, ; immediate
: r@  $6b81 asm, ; immediate
: rdrop $600C asm, ; immediate
: exit  $700c asm, ; immediate

\ -----------------------------------------------------------------------------
\ LITERAL

\ Compiles code to insert a computed literal into a definition.
: literal  ( C: x -- )  ( -- x )
  dup 0 < if  ( MSB set )
    true swap invert
  else
    false swap
  then
  $8000 or asm,
  if $6600 asm, then ; immediate


\ -----------------------------------------------------------------------------
\ The inlining XT compiler.

\ Appends the execution semantics of a word to the current definition. In
\ practice, this means either compiling in a call, or inlining it (if the
\ target word contains a single returning instruction). The result goes
\ through asm, and thus may be subject to fusion.
: compile,  ( xt -- )
  \ Check if the instruction at the start of the target code field is a
  \ fused operate-return instruction.
  dup_@  $F04C and  $700C = if
    \ Retrieve it and mask out its return effect.
    @ $EFF3 and
  else
    \ Convert the CFA into a call.
    1 rshift $4000 or
  then
  asm, ;


\ -----------------------------------------------------------------------------
\ Our first evolution. This jettisons the host's implementation of the XT
\ compiler and dictionary maintenance words, and switches to using the target
\ versions, thus improving performance (and ensuring correctness).

<TARGET-EVOLVE>


\ -----------------------------------------------------------------------------
\ Basic control structures.

\ Records the current location as the destination of a backwards branch, yet
\ to be assembled by <resolve .
: mark<  ( -- dest )  freeze here ;
\ Assembles a backwards branch (using the given template) to a location left
\ by mark< .
: <resolve  ( dest template -- )
  swap 1 rshift  \ convert to word address
  or asm, ;

\ Assembles a forward branch (using the given template) to a yet-unknown
\ location. Leaves the address of the branch (the 'orig') on the stack for
\ fixup via >resolve .
: mark>  ( template -- orig )
  mark<  ( lightly abused for its 'freeze here' definition )
  swap asm, ;
\ Resolves a forward branch previously assembled by mark> by updating its
\ destination field.
: >resolve  ( orig -- )
  freeze
  dup_@  here 1 rshift or  swap ! ;

\ The host has been providing IF ELSE THEN until now. These definitions
\ immediately shadow the host versions.
: if  ( C: -- orig )  $2000 mark> ; immediate
: then  ( C: orig -- )  >resolve ; immediate
: else  ( C: orig1 -- orig2 )
  $0000 mark>
  swap >resolve ; immediate

\ Loop support!
: begin  ( C: -- dest )  mark< ; immediate
: again  ( C: dest -- )  0 <resolve ; immediate
: until  ( C: dest -- )  $2000 <resolve ; immediate
: while  ( C: dest -- orig dest )
  $2000 mark> swap ; immediate
: repeat  ( C: orig dest -- )
  $0000 <resolve
  >resolve ; immediate


\ -----------------------------------------------------------------------------
\ Dictionary search.

\ Compares a string to the name field of a definition.
: name= ( c-addr u nfa -- ? )
  >r  ( stash the NFA )
  r@ c@ over = if  ( lengths equal )
    r> 1+ swap   ( c-addr c-addr2 u )
    begin
      dup
    while
      >r
      over @ over @ xor if
        rdrop
        2drop 0 exit
      then
      1+ swap 1+
      r> 1 -
    repeat
    true
  else
    r> false
  then nip nip nip ;

\ Searches the dictionary for a definition with the given name. This is a
\ variant of standard FIND, which uses a counted string for some reason.
: sfind  ( c-addr u -- c-addr u 0 | xt flags true )
  LATEST
  begin          ( c-addr u lfa )
    @ dup
  while
    >r  ( stash the LFA ) ( c-addr u )              ( R: lfa )
    2dup                  ( c-addr u c-addr u )     ( R: lfa )
    r@ cell +             ( c-addr u c-addr u nfa ) ( R: lfa )
    name= if              ( c-addr u )              ( R: lfa )
      nip                 ( u )                     ( R: lfa )
      r> cell +           ( u nfa )
      1+  +  aligned      ( ffa )
      dup cell +          ( ffa cfa )
      swap @              ( cfa flags )
      true exit           ( cfa flags true )
    then    ( c-addr u ) ( R: lfa )
    r>      ( c-addr u lfa )
  repeat ;

\ Jettison the host's dictionary search code.
<TARGET-EVOLVE>


\ -----------------------------------------------------------------------------
\ More useful Forth words.

: execute  ( i*x xt -- j*x )  >r ; ( NOINLINE )
: min  ( n1 n2 -- lesser )
  2dup < if drop else nip then ;  ( TODO could be optimized )

: c!  dup >r
      1 and if ( lsb set )
        8 lshift
        r@ @ $FF and or
      else
        $FF and
        r@ @ $FF00 and or
      then
      r> ! ;

: c,  here c!  1 allot ;

: align
  here 1 and if 0 c, then ;
  ( TODO this shouldn't have to comma zeroes, but name= has a bug )

: 0= 0 = ;
: <> = invert ;


\ -----------------------------------------------------------------------------
\ Support for VARIABLE .

\ Because we don't need VARIABLE until much later in bootstrap, we can write
\ its code fragment more clearly than (docon) .

: (dovar) r> ;


\ -----------------------------------------------------------------------------
\ Basic source code input support and parsing.

\ Address and length of current input SOURCE.
variable 'SOURCE  cell allot
\ Returns the current input as a string.
: SOURCE  ( -- c-addr u )  'SOURCE dup_@ swap cell + @ ;

\ Holds the number of characters consumed from SOURCE so far.
variable >IN

: /string   ( c-addr u n -- c-addr' u' )
  >r  r@ - swap  r> + swap ;

: skip-while  ( c-addr u xt -- c-addr' u' )
  >r
  begin
    over c@ r@ execute
    over_and
  while
    1 /string
  repeat
  rdrop ;

: isspace? $21 u< ;
: isnotspace? isspace? 0= ;

: parse-name
  SOURCE  >IN @  /string
  [ ' isspace? ] literal skip-while over >r
  [ ' isnotspace? ] literal skip-while
  2dup  1 min +  'SOURCE @ -  >IN !
  drop r> tuck - ;


\ -----------------------------------------------------------------------------
\ Header creation and defining words.

\ Encloses a string in the dictionary as a counted string.
: s,  ( c-addr u -- )
  dup c,        ( Length byte )
  over + swap   ( c-addr-end c-addr-start )
  begin
    2dup_xor    ( cheap inequality test )
  while
    dup c@ c,
    1+
  repeat
  2drop align ;

\ Implementation factor of the other defining words: parses a name and creates
\ a header, without generating any code.
: (CREATE)
  ( link field )
  align here  LATEST @ ,  LATEST !
  ( name )
  parse-name s,
  ( flags )
  0 , ;

<TARGET-EVOLVE>
  \ Cause the host to notice 'SOURCE and >IN, which enables the use of target
  \ parsing words. Because our definitions for CONSTANT , VARIABLE , and : are
  \ about to shadow the host emulated versions, this support is important!

: :  (CREATE) ] ;
  \ Note that this definition gets used immediately.

: create
  (CREATE)
  [ ' (dovar) ] literal compile, ;

: constant
  (CREATE)
  [ ' (docon) ] literal compile,
  , ;

: variable create 0 , ;


\ -----------------------------------------------------------------------------
\ Semicolon. This is my favorite piece of code in the kernel, and the most
\ heavily commented punctuation character of my career thus far.

\ Recall that the Forth word ; (semicolon) has the effect of compiling in a
\ return-from-colon-definition sequence and returning to the interpreter.

\ Recall also that ; is an IMMEDIATE word (it has to be, to have those effects
\ during compilation).

\ Finally, note that BsForth never hides definitions. A definition is available
\ for recursion without further effort, in deviation from the standard.

\ Alright, that said, let's go.
: ;
  [ ' exit compile, ]  \ aka POSTPONE exit
  [ ' [    compile, ]  \ aka POSTPONE [

  \ Now we have a condundrum. How do we end this definition? We've been using a
  \ host-emulated version of ; to end definitions 'till now. But now that a
  \ definition exists in the target, it *immediately* shadows the emulated
  \ version. We can't simply write ; because ; is not yet IMMEDIATE. But we can
  \ fix that:
  [ immediate ]

  \ Because ; is now IMMEDIATE, we are going to recurse *at compile time.* We
  \ invoke the target definition of ; to complete the target definition of ; by
  \ performing the actions above.
  ;

\ Voila. Tying the knot in the Forth compiler.


\ -----------------------------------------------------------------------------
\ More useful Forth words.

: rot  ( x1 x2 x3 -- x2 x3 x1 )
  >r swap r> swap ;
: -rot  ( x1 x2 x3 -- x3 x1 x2 )
  rot rot ; ( TODO could likely be cleverer )

\ -----------------------------------------------------------------------------
\ User-facing terminal.

\ We assume there is a terminal that operates like a, well, terminal, without
\ fancy features like online editing. We can't assume anything about its
\ implementation, however, so we have to define terminal operations in terms
\ of hooks to be implemented later for a specific device.

\ XT storage for the key and emit vectors. This is strictly less efficient than
\ using DEFER and should probably get changed later.
variable 'key
variable 'emit

: key 'key @ execute ;
: emit 'emit @ execute ;

$20 constant bl
: space bl emit ;
: beep 7 emit ;

: cr $D emit $A emit ;
  \ This assumes a traditional terminal and is a candidate for vectoring.

: type  ( c-addr u -- )
  over + swap
  begin
    2dup_xor
  while
    dup c@ emit
    1+
  repeat 2drop ;

\ Receive a string of at most u characters, allowing basic line editing.
\ Returns the number of characters received, which may be zero.
: accept  ( c-addr u -- u )
  >r 0
  begin ( c-addr pos ) ( R: limit )
    key

    $1F over u< if  \ Printable character
      over r@ u< if   \ in bounds
        dup emit  \ echo character
        >r 2dup_+ r>  ( c-addr pos dest c )
        swap c! 1+    ( c-addr pos' )
        0  \ "key" for code above
      else  \ buffer full
        beep
      then
    then

    3 over_= if   \ ^C - abort
      2drop 0     \ Reset buffer to zero
      $D          \ act like a CR
    then

    8 over_= if   \ Backspace
      drop
      dup if  \ Not at start of line
        8 emit  space  8 emit   \ rub out character
        1 -
      else    \ At start of line
        beep
      then
      0
    then

    $D =
  until
  rdrop nip ;

\ -----------------------------------------------------------------------------
\ END OF GENERAL KERNEL CODE
\ -----------------------------------------------------------------------------
<TARGET-EVOLVE>  \ Clear stats on host emulated word usage.
.( After compiling general-purpose code, HERE is... )
here host.




( ----------------------------------------------------------- )
( Icestick SoC support code )

: #bit  1 swap lshift ;

( ----------------------------------------------------------- )
( Interrupt Controller )

$F000 constant irqcon-st  ( status / enable trigger )
$F002 constant irqcon-en  ( enable )
$F004 constant irqcon-se  ( set enable )
$F006 constant irqcon-ce  ( clear enable )

( Atomically enables interrupts and returns. This is intended to be tail )
( called from the end of an ISR. )
: enable-interrupts  irqcon-st 2dup_!_drop ;

: disable-irq  ( u -- )  #bit irqcon-ce ! ;
: enable-irq   ( u -- )  #bit irqcon-se ! ;

13 constant irq-timer-m1
14 constant irq-timer-m0
15 constant irq-inport-negedge

( ----------------------------------------------------------- )
( I/O ports )

$8000 constant outport      ( literal value)
$8002 constant outport-set  ( 1s set pins, 0s do nothing)
$8004 constant outport-clr  ( 1s clear pins, 0s do nothing)
$8006 constant outport-tog  ( 1s toggle pins, 0s do nothing)

$A000 constant inport

( ----------------------------------------------------------- )
( Timer )

$C000 constant timer-ctr
$C002 constant timer-flags
$C004 constant timer-m0
$C006 constant timer-m1

( ----------------------------------------------------------- )
( UART emulation )

.( Compiling soft UART... )
here

( Spins reading a variable until it contains zero. )
: poll0  ( addr -- )  begin dup_@ 0= until drop ;

( Decrements a counter variable and leaves its value on stack )
: -counter  ( addr -- u )
  dup_@   ( addr u )
  1 -     ( addr u' )
  swap    ( u' addr )
  2dup_!_drop ;  ( u' )

2500 constant cycles/bit
1250 constant cycles/bit/2

variable uart-tx-bits   ( holds bits as they're shifted out )
variable uart-tx-count  ( tracks the number of bits remaining )

: tx-isr
  1 timer-flags !     ( acknowledge interrupt )
  uart-tx-bits @
  1 over_and
  if outport-set else outport-clr then
  1 swap !
  1 rshift uart-tx-bits !

  uart-tx-count -counter if
    timer-ctr @ cycles/bit + timer-m1 !
  else
    irq-timer-m1 disable-irq
  then ;

: tx
  ( Wait for transmitter to be free )
  uart-tx-count poll0
  ( Frame the byte )
  1 lshift
  $200 or
  uart-tx-bits !
  10 uart-tx-count !
  irq-timer-m1 enable-irq ;

variable uart-rx-bits
variable uart-rx-bitcount

variable uart-rx-buf  3 cells allot
variable uart-rx-hd
variable uart-rx-tl

: CTSon 2 outport-clr ! ;
: CTSoff 2 outport-set ! ;

: rxq-empty? uart-rx-hd @ uart-rx-tl @ = ;
: rxq-full? uart-rx-hd @ uart-rx-tl @ - 4 = ;

( Inserts a cell into the receive queue. This is intended to be called from )
( interrupt context, so if it encounters a queue overrun, it simply drops )
( data. )
: >rxq
  rxq-full? if
    drop
  else
    uart-rx-buf  uart-rx-hd @ 6 and +  !
    2 uart-rx-hd +!
  then ;

( Takes a cell from the receive queue. If the queue is empty, spin. )
: rxq>
  begin rxq-empty? 0= until
  uart-rx-buf  uart-rx-tl @ 6 and +  @
  2 uart-rx-tl +! ;

: uart-rx-init
  \ Clear any pending negedge condition
  0 inport !
  \ Enable the initial negedge ISR to detect the start bit.
  irq-inport-negedge enable-irq ;

\ Triggered when we're between frames and RX drops.
: rx-negedge-isr
  \ Set up the timer to interrupt us again halfway into the start bit.
  \ First, the timer may have rolled over while we were waiting for a new
  \ frame, so clear its pending interrupt status.
  2 timer-flags !
  \ Next set the match register to the point in time we want.
  timer-ctr @  cycles/bit/2 +  timer-m0 !
  \ We don't need to clear the IRQ condition, because we won't be re-enabling
  \ it any time soon. Mask our interrupt.
  irq-inport-negedge disable-irq

  \ Prepare to receive a ten bit frame.
  10 uart-rx-bitcount !

  \ Now enable its interrupt.
  irq-timer-m0 enable-irq ;

\ Triggered at each sampling point during an RX frame.
: rx-timer-isr
  \ Sample the input port into the high bit of a word.
  inport @  15 lshift
  \ Reset the timer for the next sample point.
  timer-ctr @  cycles/bit +  timer-m0 !
  \ Load this into the frame shift register.
  uart-rx-bits @  1 rshift  or  uart-rx-bits !
  \ Decrement the bit count.
  uart-rx-bitcount -counter if \ we have more bits to receive
    \ Clear the interrupt condition.
    2 timer-flags !
  else  \ we're done, disable timer interrupt
    irq-timer-m0 disable-irq
    \ Enqueue the received frame
    uart-rx-bits @ >rxq
    \ Clear any pending negedge condition
    0 inport !
    \ Enable the initial negedge ISR to detect the start bit.
    irq-inport-negedge enable-irq
    \ Conservatively deassert CTS to try and stop sender.
    CTSoff
  then ;

\ Receives a byte from RX, returning the bits and a valid flag. The valid flag may
\ be false in the event of a framing error.
: rx  ( -- c ? )
  rxq>

  \ Dissect the frame and check for framing error. The frame is in the
  \ upper bits of the word.
  6 rshift
  dup 1 rshift $FF and   \ extract the data bits
  swap $201 and          \ extract the start/stop bits.
  $200 =                 \ check for valid framing
  rxq-empty? if CTSon then  \ allow sender to resume if we've emptied the queue.
  ;


( ----------------------------------------------------------- )
( Icestick board features )

: ledtog  4 + #bit outport-tog ! ;


( ----------------------------------------------------------- )
( Demo wiring below )

: delay 0 begin 1+ dup 0 = until drop ;

: isr
  irqcon-st @
  $4000 over_and if
    rx-timer-isr
  then
  $8000 over_and if
    rx-negedge-isr
  then
  $2000 over_and if
    tx-isr
  then
  drop
  r> 2 - >r
  enable-interrupts ;

create TIB 80 allot

: rx! rx 0= if rx! exit then ;

' tx 'emit !
' rx! 'key !

: cold
  uart-rx-init
  enable-interrupts
  $FF tx
  35 emit
  LATEST @ cell + 1 + 4 type
  35 emit
  begin
    TIB 80 accept
    cr
    TIB swap type
    cr
  again ;

( install cold as the reset vector )
' cold  1 rshift  0 !
( install isr as the interrupt vector )
' isr  1 rshift  2 !

.( Compilation complete. HERE is... )
here host.
