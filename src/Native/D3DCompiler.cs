//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text;

namespace RemoteGameHub.Native;

// The shader compiler that ships with Windows, used at the moment a stream opens: the one shader
// this server has is written against the stream's own colour space, so it is built, not carried.
internal static unsafe class D3DCompiler
{
    // Optimise fully and do not keep debug information: this is compiled once per stream, and the
    // pixel shader below runs on every pixel of every frame.
    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15;

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    private static extern int D3DCompile(
        void* source, nuint sourceLength, byte* sourceName, void* defines, void* include,
        byte* entryPoint, byte* target, uint flags1, uint flags2, void** code, void** errors);

    // The compiled bytecode, with what produced it released. Throws with the compiler's own words,
    // which name the line: a shader that will not build is a mistake in this repository.
    internal static byte[] Compile(string source, string entryPoint, string target)
    {
        var sourceBytes = Encoding.ASCII.GetBytes(source);
        var entryBytes = Encoding.ASCII.GetBytes(entryPoint + "\0");
        var targetBytes = Encoding.ASCII.GetBytes(target + "\0");
        var nameBytes = Encoding.ASCII.GetBytes("colour.hlsl\0");

        void* code = null;
        void* errors = null;

        try
        {
            int hr;

            fixed (byte* text = sourceBytes)
            fixed (byte* name = nameBytes)
            fixed (byte* entry = entryBytes)
            fixed (byte* profile = targetBytes)
            {
                hr = D3DCompile(text, (nuint)sourceBytes.Length, name, null, null, entry, profile,
                                D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
            }

            if (hr < 0)
            {
                var said = errors is null ? Com.Describe(hr) : Text(errors);
                throw new InvalidOperationException(
                    $"the {entryPoint} shader did not compile: {said}");
            }

            var bytes = new byte[(int)Size(code)];
            Marshal.Copy((nint)Pointer(code), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Com.Release(code);
            Com.Release(errors);
        }
    }

    // ID3DBlob slot 3: GetBufferPointer
    private static void* Pointer(void* blob) =>
        ((delegate* unmanaged[Stdcall]<void*, void*>)Com.VTable(blob)[3])(blob);

    // ID3DBlob slot 4: GetBufferSize
    private static nuint Size(void* blob) =>
        ((delegate* unmanaged[Stdcall]<void*, nuint>)Com.VTable(blob)[4])(blob);

    private static string Text(void* blob) =>
        Marshal.PtrToStringAnsi((nint)Pointer(blob))?.Trim() ?? "no reason given";
}
