module Fapac.Core.Util.Unsafe

open FSharp.NativeInterop

#nowarn "9"

let inline getStackPtr() =
    NativePtr.stackalloc<byte> 1
    |> NativePtr.toNativeInt
