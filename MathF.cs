using System;
using System.Runtime.CompilerServices;

namespace PedalDrumMatrix
{
    // System.MathF does not exist on .NET Framework 4.8 (1503 notes 1.3), and
    // pulling in a NuGet polyfill would add a runtime DLL next to the machine.
    // This shim forwards to System.Math with float casts so every DSP call site
    // stays exactly as written. Being in the PedalDrumMatrix namespace, it is
    // what "MathF" resolves to throughout the machine.
    internal static class MathF
    {
        public const float PI = (float)Math.PI;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Abs(float x) => Math.Abs(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Max(float a, float b) => Math.Max(a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Min(float a, float b) => Math.Min(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sin(float x) => (float)Math.Sin(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Cos(float x) => (float)Math.Cos(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Tan(float x) => (float)Math.Tan(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Tanh(float x) => (float)Math.Tanh(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Exp(float x) => (float)Math.Exp(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Pow(float x, float y) => (float)Math.Pow(x, y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sqrt(float x) => (float)Math.Sqrt(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Log2(float x) => (float)(Math.Log(x) * 1.4426950408889634);   // 1 / ln 2

        // Math.Round defaults to banker's rounding (MidpointRounding.ToEven),
        // the same as MathF.Round, so quantisation is unchanged.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Round(float x) => (float)Math.Round(x);
    }
}
