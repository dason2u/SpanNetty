﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
#if !NET40

namespace DotNetty.Buffers
{
    using System;
    using System.Buffers;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using CuteAnt.Buffers;
    using DotNetty.Common.Utilities;

    partial class ByteBufferUtil
    {
        /// <summary>
        ///     Returns the reader index of needle in haystack, or -1 if needle is not in haystack.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOf(IByteBuffer needle, IByteBuffer haystack)
        {
            return haystack.IndexOf(needle);
        }

        /// <summary>
        ///     Returns {@code true} if and only if the two specified buffers are
        ///     identical to each other for {@code length} bytes starting at {@code aStartIndex}
        ///     index for the {@code a} buffer and {@code bStartIndex} index for the {@code b} buffer.
        ///     A more compact way to express this is:
        ///     <p />
        ///     {@code a[aStartIndex : aStartIndex + length] == b[bStartIndex : bStartIndex + length]}
        /// </summary>
        public static bool Equals(IByteBuffer a, int aStartIndex, IByteBuffer b, int bStartIndex, int length)
        {
            if (aStartIndex < 0 || bStartIndex < 0 || length < 0)
            {
                ThrowHelper.ThrowArgumentException_NonNegative();
            }
            if (a.WriterIndex - length < aStartIndex || b.WriterIndex - length < bStartIndex)
            {
                return false;
            }

            var spanA = a.GetReadableSpan(aStartIndex, length);
            var spanB = b.GetReadableSpan(bStartIndex, length);
            return spanA.SequenceEqual(spanB);
        }

        /// <summary>
        ///     Returns {@code true} if and only if the two specified buffers are
        ///     identical to each other as described in {@link ByteBuf#equals(Object)}.
        ///     This method is useful when implementing a new buffer type.
        /// </summary>
        public static bool Equals(IByteBuffer bufferA, IByteBuffer bufferB)
        {
            int aLen = bufferA.ReadableBytes;
            if (aLen != bufferB.ReadableBytes)
            {
                return false;
            }

            return Equals(bufferA, bufferA.ReaderIndex, bufferB, bufferB.ReaderIndex, aLen);
        }

        /// <summary>
        ///     Compares the two specified buffers as described in {@link ByteBuf#compareTo(ByteBuf)}.
        ///     This method is useful when implementing a new buffer type.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Compare(IByteBuffer bufferA, IByteBuffer bufferB)
        {
            return bufferA.GetReadableSpan().SequenceCompareTo(bufferB.GetReadableSpan());
        }

        // Fast-Path implementation
        internal static int WriteUtf8(AbstractByteBuffer buffer, int writerIndex, ICharSequence value)
        {
            if (value is IHasUtf16Span hasUtf16)
            {
                var utf16Span = MemoryMarshal.AsBytes(hasUtf16.Utf16Span);
                try
                {
                    var bufSpan = buffer.GetSpan(writerIndex, buffer.Capacity - writerIndex);
                    var status = ToUtf8(utf16Span, bufSpan, out _, out var written);
                    if (status == OperationStatus.Done) { return written; }
                }
                catch
                {
                    if (TryWriteUtf8Composite(buffer, writerIndex, utf16Span, out var written)) { return written; }
                }
            }
            return WriteUtf80(buffer, writerIndex, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryWriteUtf8Composite(AbstractByteBuffer buffer, int writerIndex, ReadOnlySpan<byte> utf16Span, out int written)
        {
            var memory = BufferManager.Shared.Rent(buffer.Capacity);
            try
            {
                var status = ToUtf8(utf16Span, memory.AsSpan(), out _, out written);
                if (status == OperationStatus.Done)
                {
                    buffer.SetBytes(writerIndex, memory, 0, written);
                    return true;
                }
            }
            finally
            {
                BufferManager.Shared.Return(memory);
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteUtf80(AbstractByteBuffer buffer, int writerIndex, ICharSequence value)
        {
            int oldWriterIndex = writerIndex;
            var len = value.Count;

            // We can use the _set methods as these not need to do any index checks and reference checks.
            // This is possible as we called ensureWritable(...) before.
            for (int i = 0; i < len; i++)
            {
                char c = value[i];
                if (c < 0x80)
                {
                    buffer._SetByte(writerIndex++, (byte)c);
                }
                else if (c < 0x800)
                {
                    buffer._SetByte(writerIndex++, (byte)(0xc0 | (c >> 6)));
                    buffer._SetByte(writerIndex++, (byte)(0x80 | (c & 0x3f)));
                }
                else if (char.IsSurrogate(c))
                {
                    if (!char.IsHighSurrogate(c))
                    {
                        buffer._SetByte(writerIndex++, WriteUtfUnknown);
                        continue;
                    }
                    char c2;
                    try
                    {
                        // Surrogate Pair consumes 2 characters. Optimistically try to get the next character to avoid
                        // duplicate bounds checking with charAt. If an IndexOutOfBoundsException is thrown we will
                        // re-throw a more informative exception describing the problem.
                        c2 = value[++i];
                    }
                    catch (IndexOutOfRangeException)
                    {
                        buffer._SetByte(writerIndex++, WriteUtfUnknown);
                        break;
                    }
                    if (!char.IsLowSurrogate(c2))
                    {
                        buffer._SetByte(writerIndex++, WriteUtfUnknown);
                        buffer._SetByte(writerIndex++, char.IsHighSurrogate(c2) ? WriteUtfUnknown : c2);
                        continue;
                    }
                    int codePoint = CharUtil.ToCodePoint(c, c2);
                    // See http://www.unicode.org/versions/Unicode7.0.0/ch03.pdf#G2630.
                    buffer._SetByte(writerIndex++, (byte)(0xf0 | (codePoint >> 18)));
                    buffer._SetByte(writerIndex++, (byte)(0x80 | ((codePoint >> 12) & 0x3f)));
                    buffer._SetByte(writerIndex++, (byte)(0x80 | ((codePoint >> 6) & 0x3f)));
                    buffer._SetByte(writerIndex++, (byte)(0x80 | (codePoint & 0x3f)));
                }
                else
                {
                    buffer._SetByte(writerIndex++, (byte)(0xe0 | (c >> 12)));
                    buffer._SetByte(writerIndex++, (byte)(0x80 | ((c >> 6) & 0x3f)));
                    buffer._SetByte(writerIndex++, (byte)(0x80 | (c & 0x3f)));
                }
            }

            return writerIndex - oldWriterIndex;
        }

        // Fast-Path implementation
        internal static int WriteUtf8(AbstractByteBuffer buffer, int writerIndex, string value)
        {
            var utf16Span = MemoryMarshal.AsBytes(MemoryMarshal.AsBytes(value.AsSpan()));

            try
            {
                var bufSpan = buffer.GetSpan(writerIndex, buffer.Capacity - writerIndex);
                var status = ToUtf8(utf16Span, bufSpan, out _, out var written);
                if (status == OperationStatus.Done) { return written; }
            }
            catch
            {
                if (TryWriteUtf8Composite(buffer, writerIndex, utf16Span, out var written)) { return written; }
            }
            return WriteUtf80(buffer, writerIndex, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteUtf80(AbstractByteBuffer buffer, int writerIndex, string value)
        {
            int oldWriterIndex = writerIndex;
            var len = value.Length;

            // We can use the _set methods as these not need to do any index checks and reference checks.
            // This is possible as we called ensureWritable(...) before.
            for (int i = 0; i < len; i++)
            {
                char c = value[i];
                if (c < 0x80)
                {
                    buffer._SetByte(writerIndex++, (byte)c);
                }
                else if (c < 0x800)
                {
                    buffer._SetByte(writerIndex++, (byte)(0xc0 | (c >> 6)));
                    buffer._SetByte(writerIndex++, (byte)(0x80 | (c & 0x3f)));
                }
                else if (char.IsSurrogate(c))
                {
                    if (!char.IsHighSurrogate(c))
                    {
                        buffer._SetByte(writerIndex++, WriteUtfUnknown);
                        continue;
                    }
                    char c2;
                    try
                    {
                        // Surrogate Pair consumes 2 characters. Optimistically try to get the next character to avoid
                        // duplicate bounds checking with charAt. If an IndexOutOfBoundsException is thrown we will
                        // re-throw a more informative exception describing the problem.
                        c2 = value[++i];
                    }
                    catch (IndexOutOfRangeException)
                    {
                        buffer._SetByte(writerIndex++, WriteUtfUnknown);
                        break;
                    }
                    // Extra method to allow inlining the rest of writeUtf8 which is the most likely code path.
                    writerIndex = WriteUtf8Surrogate(buffer, writerIndex, c, c2);
                }
                else
                {
                    buffer._SetByte(writerIndex++, (byte)(0xe0 | (c >> 12)));
                    buffer._SetByte(writerIndex++, (byte)(0x80 | ((c >> 6) & 0x3f)));
                    buffer._SetByte(writerIndex++, (byte)(0x80 | (c & 0x3f)));
                }
            }

            return writerIndex - oldWriterIndex;
        }

        static int WriteUtf8Surrogate(AbstractByteBuffer buffer, int writerIndex, char c, char c2)
        {
            if (!char.IsLowSurrogate(c2))
            {
                buffer._SetByte(writerIndex++, WriteUtfUnknown);
                buffer._SetByte(writerIndex++, char.IsHighSurrogate(c2) ? WriteUtfUnknown : c2);
                return writerIndex;
            }
            int codePoint = CharUtil.ToCodePoint(c, c2);
            // See http://www.unicode.org/versions/Unicode7.0.0/ch03.pdf#G2630.
            buffer._SetByte(writerIndex++, (byte)(0xf0 | (codePoint >> 18)));
            buffer._SetByte(writerIndex++, (byte)(0x80 | ((codePoint >> 12) & 0x3f)));
            buffer._SetByte(writerIndex++, (byte)(0x80 | ((codePoint >> 6) & 0x3f)));
            buffer._SetByte(writerIndex++, (byte)(0x80 | (codePoint & 0x3f)));
            return writerIndex;
        }

        // Encoding Helpers
        const char HighSurrogateStart = '\ud800';
        const char HighSurrogateEnd = '\udbff';
        const char LowSurrogateStart = '\udc00';
        const char LowSurrogateEnd = '\udfff';

        // TODO: Replace this with publicly shipping implementation: https://github.com/dotnet/corefx/issues/34094
        /// <summary>
        /// Converts a span containing a sequence of UTF-16 bytes into UTF-8 bytes.
        ///
        /// This method will consume as many of the input bytes as possible.
        ///
        /// On successful exit, the entire input was consumed and encoded successfully. In this case, <paramref name="bytesConsumed"/> will be
        /// equal to the length of the <paramref name="utf16Source"/> and <paramref name="bytesWritten"/> will equal the total number of bytes written to
        /// the <paramref name="utf8Destination"/>.
        /// </summary>
        /// <param name="utf16Source">A span containing a sequence of UTF-16 bytes.</param>
        /// <param name="utf8Destination">A span to write the UTF-8 bytes into.</param>
        /// <param name="bytesConsumed">On exit, contains the number of bytes that were consumed from the <paramref name="utf16Source"/>.</param>
        /// <param name="bytesWritten">On exit, contains the number of bytes written to <paramref name="utf8Destination"/></param>
        /// <returns>A <see cref="OperationStatus"/> value representing the state of the conversion.</returns>
        private unsafe static OperationStatus ToUtf8(ReadOnlySpan<byte> utf16Source, Span<byte> utf8Destination, out int bytesConsumed, out int bytesWritten)
        {
            //
            //
            // KEEP THIS IMPLEMENTATION IN SYNC WITH https://github.com/dotnet/coreclr/blob/master/src/System.Private.CoreLib/shared/System/Text/UTF8Encoding.cs#L841
            //
            //
            fixed (byte* chars = &MemoryMarshal.GetReference(utf16Source))
            fixed (byte* bytes = &MemoryMarshal.GetReference(utf8Destination))
            {
                char* pSrc = (char*)chars;
                byte* pTarget = bytes;

                char* pEnd = (char*)(chars + utf16Source.Length);
                byte* pAllocatedBufferEnd = pTarget + utf8Destination.Length;

                // assume that JIT will enregister pSrc, pTarget and ch

                // Entering the fast encoding loop incurs some overhead that does not get amortized for small
                // number of characters, and the slow encoding loop typically ends up running for the last few
                // characters anyway since the fast encoding loop needs 5 characters on input at least.
                // Thus don't use the fast decoding loop at all if we don't have enough characters. The threashold
                // was choosen based on performance testing.
                // Note that if we don't have enough bytes, pStop will prevent us from entering the fast loop.
                while (pEnd - pSrc > 13)
                {
                    // we need at least 1 byte per character, but Convert might allow us to convert
                    // only part of the input, so try as much as we can.  Reduce charCount if necessary
                    int available = Math.Min(PtrDiff(pEnd, pSrc), PtrDiff(pAllocatedBufferEnd, pTarget));

                    // FASTLOOP:
                    // - optimistic range checks
                    // - fallbacks to the slow loop for all special cases, exception throwing, etc.

                    // To compute the upper bound, assume that all characters are ASCII characters at this point,
                    //  the boundary will be decreased for every non-ASCII character we encounter
                    // Also, we need 5 chars reserve for the unrolled ansi decoding loop and for decoding of surrogates
                    // If there aren't enough bytes for the output, then pStop will be <= pSrc and will bypass the loop.
                    char* pStop = pSrc + available - 5;
                    if (pSrc >= pStop)
                        break;

                    do
                    {
                        int ch = *pSrc;
                        pSrc++;

                        if (ch > 0x7F)
                        {
                            goto LongCode;
                        }
                        *pTarget = (byte)ch;
                        pTarget++;

                        // get pSrc aligned
                        if ((unchecked((int)pSrc) & 0x2) != 0)
                        {
                            ch = *pSrc;
                            pSrc++;
                            if (ch > 0x7F)
                            {
                                goto LongCode;
                            }
                            *pTarget = (byte)ch;
                            pTarget++;
                        }

                        // Run 4 characters at a time!
                        while (pSrc < pStop)
                        {
                            ch = *(int*)pSrc;
                            int chc = *(int*)(pSrc + 2);
                            if (((ch | chc) & unchecked((int)0xFF80FF80)) != 0)
                            {
                                goto LongCodeWithMask;
                            }

                            // Unfortunately, this is endianess sensitive
#if BIGENDIAN
                            *pTarget = (byte)(ch >> 16);
                            *(pTarget + 1) = (byte)ch;
                            pSrc += 4;
                            *(pTarget + 2) = (byte)(chc >> 16);
                            *(pTarget + 3) = (byte)chc;
                            pTarget += 4;
#else // BIGENDIAN
                            *pTarget = (byte)ch;
                            *(pTarget + 1) = (byte)(ch >> 16);
                            pSrc += 4;
                            *(pTarget + 2) = (byte)chc;
                            *(pTarget + 3) = (byte)(chc >> 16);
                            pTarget += 4;
#endif // BIGENDIAN
                        }
                        continue;

                    LongCodeWithMask:
#if BIGENDIAN
                        // be careful about the sign extension
                        ch = (int)(((uint)ch) >> 16);
#else // BIGENDIAN
                        ch = (char)ch;
#endif // BIGENDIAN
                        pSrc++;

                        if (ch > 0x7F)
                        {
                            goto LongCode;
                        }
                        *pTarget = (byte)ch;
                        pTarget++;
                        continue;

                    LongCode:
                        // use separate helper variables for slow and fast loop so that the jit optimizations
                        // won't get confused about the variable lifetimes
                        int chd;
                        if (ch <= 0x7FF)
                        {
                            // 2 byte encoding
                            chd = unchecked((sbyte)0xC0) | (ch >> 6);
                        }
                        else
                        {
                            // if (!IsLowSurrogate(ch) && !IsHighSurrogate(ch))
                            if (!IsInRangeInclusive(ch, HighSurrogateStart, LowSurrogateEnd))
                            {
                                // 3 byte encoding
                                chd = unchecked((sbyte)0xE0) | (ch >> 12);
                            }
                            else
                            {
                                // 4 byte encoding - high surrogate + low surrogate
                                // if (!IsHighSurrogate(ch))
                                if (ch > HighSurrogateEnd)
                                {
                                    // low without high -> bad
                                    goto InvalidData;
                                }

                                chd = *pSrc;

                                // if (!IsLowSurrogate(chd)) {
                                if (!IsInRangeInclusive(chd, LowSurrogateStart, LowSurrogateEnd))
                                {
                                    // high not followed by low -> bad
                                    goto InvalidData;
                                }

                                pSrc++;

                                ch = chd + (ch << 10) +
                                    (0x10000
                                    - LowSurrogateStart
                                    - (HighSurrogateStart << 10));

                                *pTarget = (byte)(unchecked((sbyte)0xF0) | (ch >> 18));
                                // pStop - this byte is compensated by the second surrogate character
                                // 2 input chars require 4 output bytes.  2 have been anticipated already
                                // and 2 more will be accounted for by the 2 pStop-- calls below.
                                pTarget++;

                                chd = unchecked((sbyte)0x80) | (ch >> 12) & 0x3F;
                            }
                            *pTarget = (byte)chd;
                            pStop--;                    // 3 byte sequence for 1 char, so need pStop-- and the one below too.
                            pTarget++;

                            chd = unchecked((sbyte)0x80) | (ch >> 6) & 0x3F;
                        }
                        *pTarget = (byte)chd;
                        pStop--;                        // 2 byte sequence for 1 char so need pStop--.

                        *(pTarget + 1) = (byte)(unchecked((sbyte)0x80) | ch & 0x3F);
                        // pStop - this byte is already included

                        pTarget += 2;
                    }
                    while (pSrc < pStop);

                    Debug.Assert(pTarget <= pAllocatedBufferEnd, "[UTF8Encoding.GetBytes]pTarget <= pAllocatedBufferEnd");
                }

                while (pSrc < pEnd)
                {
                    // SLOWLOOP: does all range checks, handles all special cases, but it is slow

                    // read next char. The JIT optimization seems to be getting confused when
                    // compiling "ch = *pSrc++;", so rather use "ch = *pSrc; pSrc++;" instead
                    int ch = *pSrc;
                    pSrc++;

                    if (ch <= 0x7F)
                    {
                        if (pAllocatedBufferEnd - pTarget <= 0)
                            goto DestinationFull;

                        *pTarget = (byte)ch;
                        pTarget++;
                        continue;
                    }

                    int chd;
                    if (ch <= 0x7FF)
                    {
                        if (pAllocatedBufferEnd - pTarget <= 1)
                            goto DestinationFull;

                        // 2 byte encoding
                        chd = unchecked((sbyte)0xC0) | (ch >> 6);
                    }
                    else
                    {
                        // if (!IsLowSurrogate(ch) && !IsHighSurrogate(ch))
                        if (!IsInRangeInclusive(ch, HighSurrogateStart, LowSurrogateEnd))
                        {
                            if (pAllocatedBufferEnd - pTarget <= 2)
                                goto DestinationFull;

                            // 3 byte encoding
                            chd = unchecked((sbyte)0xE0) | (ch >> 12);
                        }
                        else
                        {
                            if (pAllocatedBufferEnd - pTarget <= 3)
                                goto DestinationFull;

                            // 4 byte encoding - high surrogate + low surrogate
                            // if (!IsHighSurrogate(ch))
                            if (ch > HighSurrogateEnd)
                            {
                                // low without high -> bad
                                goto InvalidData;
                            }

                            if (pSrc >= pEnd)
                                goto NeedMoreData;

                            chd = *pSrc;

                            // if (!IsLowSurrogate(chd)) {
                            if (!IsInRangeInclusive(chd, LowSurrogateStart, LowSurrogateEnd))
                            {
                                // high not followed by low -> bad
                                goto InvalidData;
                            }

                            pSrc++;

                            ch = chd + (ch << 10) +
                                (0x10000
                                - LowSurrogateStart
                                - (HighSurrogateStart << 10));

                            *pTarget = (byte)(unchecked((sbyte)0xF0) | (ch >> 18));
                            pTarget++;

                            chd = unchecked((sbyte)0x80) | (ch >> 12) & 0x3F;
                        }
                        *pTarget = (byte)chd;
                        pTarget++;

                        chd = unchecked((sbyte)0x80) | (ch >> 6) & 0x3F;
                    }

                    *pTarget = (byte)chd;
                    *(pTarget + 1) = (byte)(unchecked((sbyte)0x80) | ch & 0x3F);

                    pTarget += 2;
                }

                bytesConsumed = (int)((byte*)pSrc - chars);
                bytesWritten = (int)(pTarget - bytes);
                return OperationStatus.Done;

            InvalidData:
                bytesConsumed = (int)((byte*)(pSrc - 1) - chars);
                bytesWritten = (int)(pTarget - bytes);
                return OperationStatus.InvalidData;

            DestinationFull:
                bytesConsumed = (int)((byte*)(pSrc - 1) - chars);
                bytesWritten = (int)(pTarget - bytes);
                return OperationStatus.DestinationTooSmall;

            NeedMoreData:
                bytesConsumed = (int)((byte*)(pSrc - 1) - chars);
                bytesWritten = (int)(pTarget - bytes);
                return OperationStatus.NeedMoreData;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static int PtrDiff(char* a, char* b)
        {
            return (int)(((uint)((byte*)a - (byte*)b)) >> 1);
        }

        // byte* flavor just for parity
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static int PtrDiff(byte* a, byte* b)
        {
            return (int)(a - b);
        }

        /// <summary>
        /// Returns <see langword="true"/> iff <paramref name="value"/> is between
        /// <paramref name="lowerBound"/> and <paramref name="upperBound"/>, inclusive.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsInRangeInclusive(int value, int lowerBound, int upperBound)
            => (uint)(value - lowerBound) <= (uint)(upperBound - lowerBound);

        // Fast-Path implementation
        internal static int WriteAscii(AbstractByteBuffer buffer, int writerIndex, ICharSequence seq)
        {
            if (seq is IHasAsciiSpan hasAscii)
            {
                buffer.SetBytes(writerIndex, hasAscii.AsciiSpan);
                return seq.Count;
            }
            if (seq is IHasUtf16Span hasUtf16)
            {
                return WriteAscii0(buffer, writerIndex, hasUtf16.Utf16Span);
            }

            return WriteAscii0(buffer, writerIndex, seq);
        }

        internal static int WriteAscii(AbstractByteBuffer buffer, int writerIndex, string value)
        {
            return WriteAscii0(buffer, writerIndex, value.AsSpan());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteAscii0(AbstractByteBuffer buffer, int writerIndex, ICharSequence seq)
        {
            var len = seq.Count;
            // We can use the _set methods as these not need to do any index checks and reference checks.
            // This is possible as we called ensureWritable(...) before.
            for (int i = 0; i < len; i++)
            {
                buffer._SetByte(writerIndex++, AsciiString.CharToByte(seq[i]));
            }
            return len;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WriteAscii0(AbstractByteBuffer buffer, int writerIndex, ReadOnlySpan<char> utf16Source)
        {
            var charCount = utf16Source.Length;
            try
            {
                var asciiDestination = buffer.GetSpan(writerIndex, buffer.Capacity - writerIndex);
                WriteAscii0(utf16Source, asciiDestination, charCount);
            }
            catch
            {
                WriteAsciiComposite(buffer, writerIndex, utf16Source, charCount);
            }
            return charCount;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteAsciiComposite(AbstractByteBuffer buffer, int writerIndex, ReadOnlySpan<char> utf16Source, int length)
        {
            var memory = BufferManager.Shared.Rent(length);
            try
            {
                WriteAscii0(utf16Source, memory.AsSpan(), length);
                buffer.SetBytes(writerIndex, memory, 0, length);
            }
            finally
            {
                BufferManager.Shared.Return(memory);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteAscii0(ReadOnlySpan<char> utf16Source, Span<byte> asciiDestination, int length)
        {
#if NETCOREAPP
            System.Text.Encoding.ASCII.GetBytes(utf16Source, asciiDestination);
#else
            unsafe
            {
                fixed (char* chars = &MemoryMarshal.GetReference(utf16Source))
                {
                    fixed (byte* bytes = &MemoryMarshal.GetReference(asciiDestination))
                    {
                        System.Text.Encoding.ASCII.GetBytes(chars, length, bytes, length);
                    }
                }
            }
#endif
        }

        internal static IByteBuffer EncodeString0(IByteBufferAllocator alloc, bool enforceHeap, string src, Encoding encoding, int extraCapacity)
        {
            int length = encoding.GetMaxByteCount(src.Length) + extraCapacity;
            bool release = true;

            IByteBuffer dst = enforceHeap ? alloc.HeapBuffer(length) : alloc.Buffer(length);
            Debug.Assert(dst.HasArray, "Operation expects allocator to operate array-based buffers.");

            try
            {
#if NETCOREAPP
                int written = encoding.GetBytes(src.AsSpan(), dst.Free);
#else
                int written = encoding.GetBytes(src, 0, src.Length, dst.Array, dst.ArrayOffset + dst.WriterIndex);
#endif
                dst.SetWriterIndex(dst.WriterIndex + written);
                release = false;

                return dst;
            }
            finally
            {
                if (release)
                {
                    dst.Release();
                }
            }
        }

        public static string DecodeString(IByteBuffer src, int readerIndex, int len, Encoding encoding)
        {
            if (0u >= (uint)len) { return string.Empty; }

#if NET451
            if (src.IoBufferCount == 1)
            {
                ArraySegment<byte> ioBuf = src.GetIoBuffer(readerIndex, len);
                return encoding.GetString(ioBuf.Array, ioBuf.Offset, ioBuf.Count);
            }
            else
            {
                int maxLength = encoding.GetMaxCharCount(len);
                IByteBuffer buffer = src.Allocator.HeapBuffer(maxLength);
                try
                {
                    buffer.WriteBytes(src, readerIndex, len);
                    ArraySegment<byte> ioBuf = buffer.GetIoBuffer();
                    return encoding.GetString(ioBuf.Array, ioBuf.Offset, ioBuf.Count);
                }
                finally
                {
                    // Release the temporary buffer again.
                    buffer.Release();
                }
            }
#else
            var source = src.GetReadableSpan(readerIndex, len);
#if NETCOREAPP
            return encoding.GetString(source);
#else
            unsafe
            {
                fixed (byte* bytes = &MemoryMarshal.GetReference(source))
                {
                    return encoding.GetString(bytes, source.Length);
                }
            }
#endif
#endif
        }
    }
}

#endif