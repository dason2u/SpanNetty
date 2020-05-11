﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
//
// This idea taken from https://github.com/dotnet/corefxlab/tree/master/src/System.Buffers.ReaderWriter/System/Buffers/Writer/BufferWriterT_writable.cs

#if !NET40

namespace DotNetty.Buffers
{
    using System;
    using System.Buffers;
    using System.Runtime.CompilerServices;

    public ref partial struct ByteBufferWriter
    {
        public void Write<TWritable>(TWritable value, in TransformationFormat format) where TWritable : IWritable
        {
            int bytesWritten;
            while (true)
            {
                OperationStatus status = value.Write(_buffer, out bytesWritten, format.Format);

                switch (status)
                {
                    case OperationStatus.Done:
                        goto Transform;

                    case OperationStatus.DestinationTooSmall:
                        goto GrowMore;

                    case OperationStatus.NeedMoreData:
                    case OperationStatus.InvalidData:
                    default:
                        Throw(status); break;
                }
            Transform:
                if (format.TryTransform(_buffer, ref bytesWritten)) { goto Done; }
            GrowMore:
                GrowAndEnsure();
            }
        Done:
            Advance(ref bytesWritten);
        }

        public void Write<TWritable>(TWritable value, StandardFormat format = default) where TWritable : IWritable
        {
            int idx = 0;
            while (true)
            {
                OperationStatus status = value.Write(_buffer, out int bytesWritten, format);
                idx += bytesWritten;

                switch (status)
                {
                    case OperationStatus.Done:
                        goto Done;

                    case OperationStatus.DestinationTooSmall:
                        AdvanceAndGrow(ref idx);
                        continue;

                    case OperationStatus.NeedMoreData:
                    case OperationStatus.InvalidData:
                    default:
                        Throw(status); break;
                }
            }
        Done:
            Advance(ref idx);
        }

        public void Write<TOperation>(TOperation operation, in ReadOnlySpan<byte> input) where TOperation : IBufferOperation
        {
            int idx = 0, partialConsumed = 0;
            while (true)
            {
                OperationStatus status = operation.Execute(
                    input.Slice(partialConsumed), _buffer.Slice(idx), out int bytesConsumed, out int bytesWritten);
                idx += bytesWritten;

                switch (status)
                {
                    case OperationStatus.Done:
                        goto Done;

                    case OperationStatus.DestinationTooSmall:
                        partialConsumed += bytesConsumed;
                        AdvanceAndGrow(ref idx);
                        continue;

                    case OperationStatus.NeedMoreData:
                    case OperationStatus.InvalidData:
                    default:
                        Throw(status); break;
                }
            }
        Done:
            Advance(ref idx);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Throw(OperationStatus status)
        {
            throw GetException();
            InvalidOperationException GetException()
            {
                return new InvalidOperationException(status.ToString());
            }
        }
    }
}

#endif