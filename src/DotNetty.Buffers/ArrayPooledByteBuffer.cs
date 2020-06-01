﻿using System;
using System.Buffers;
using DotNetty.Common;
using DotNetty.Common.Internal;

namespace DotNetty.Buffers
{
    abstract partial class ArrayPooledByteBuffer : AbstractReferenceCountedByteBuffer
    {
        readonly ThreadLocalPool.Handle recyclerHandle;

        protected internal byte[] Memory;
        protected ArrayPooledByteBufferAllocator _allocator;
        ArrayPool<byte> _arrayPool;
        int _capacity;

        protected ArrayPooledByteBuffer(ThreadLocalPool.Handle recyclerHandle, int maxCapacity)
            : base(maxCapacity)
        {
            this.recyclerHandle = recyclerHandle;
        }

        /// <summary>Method must be called before reuse this {@link ArrayPooledByteBufAllocator}.</summary>
        /// <param name="allocator"></param>
        /// <param name="initialCapacity"></param>
        /// <param name="maxCapacity"></param>
        /// <param name="arrayPool"></param>
        internal void Reuse(ArrayPooledByteBufferAllocator allocator, ArrayPool<byte> arrayPool, int initialCapacity, int maxCapacity)
        {
            _allocator = allocator;
            _arrayPool = arrayPool;
            SetArray(AllocateArray(initialCapacity));

            this.SetMaxCapacity(maxCapacity);
            this.SetReferenceCount(1);
            this.SetIndex0(0, 0);
            this.DiscardMarks();
        }

        internal void Reuse(ArrayPooledByteBufferAllocator allocator, ArrayPool<byte> arrayPool, byte[] buffer, int length, int maxCapacity)
        {
            _allocator = allocator;
            _arrayPool = arrayPool;
            SetArray(buffer);

            this.SetMaxCapacity(maxCapacity);
            this.SetReferenceCount(1);
            this.SetIndex0(0, length);
            this.DiscardMarks();
        }

        public override int Capacity => _capacity;

        protected virtual byte[] AllocateArray(int initialCapacity) => _arrayPool.Rent(initialCapacity);

        protected virtual void FreeArray(byte[] bytes)
        {
#if DEBUG
            // for unit testing
            try
            {
                _arrayPool.Return(bytes);
            }
            catch { } // 防止回收非 BufferMannager 的 byte array 抛异常
#else
            _arrayPool.Return(bytes);
#endif
        }

        protected void SetArray(byte[] initialArray)
        {
            this.Memory = initialArray;
            _capacity = initialArray.Length;
        }

        public sealed override IByteBuffer AdjustCapacity(int newCapacity)
        {
            this.CheckNewCapacity(newCapacity);

            uint unewCapacity = (uint)newCapacity;
            uint oldCapacity = (uint)_capacity;
            if (oldCapacity == unewCapacity)
            {
                return this;
            }
            int bytesToCopy;
            if (unewCapacity > oldCapacity)
            {
                bytesToCopy = _capacity;
            }
            else
            {
                this.TrimIndicesToCapacity(newCapacity);
                bytesToCopy = newCapacity;
            }
            byte[] oldArray = this.Memory;
            byte[] newArray = this.AllocateArray(newCapacity);
            PlatformDependent.CopyMemory(oldArray, 0, newArray, 0, bytesToCopy);

            this.SetArray(newArray);
            this.FreeArray(oldArray);
            return this;
        }

        public sealed override IByteBufferAllocator Allocator => this._allocator;

        public sealed override IByteBuffer Unwrap() => null;

        public sealed override IByteBuffer RetainedDuplicate() => ArrayPooledDuplicatedByteBuffer.NewInstance(this, this, this.ReaderIndex, this.WriterIndex);

        public sealed override IByteBuffer RetainedSlice()
        {
            int index = this.ReaderIndex;
            return this.RetainedSlice(index, this.WriterIndex - index);
        }

        public sealed override IByteBuffer RetainedSlice(int index, int length) => ArrayPooledSlicedByteBuffer.NewInstance(this, this, index, length);

        protected internal sealed override void Deallocate()
        {
            var buffer = Memory;
            if (_arrayPool is object & buffer is object)
            {
                FreeArray(buffer);

                _arrayPool = null;
                Memory = null;

                this.Recycle();
            }
        }

        void Recycle() => this.recyclerHandle.Release(this);

        public sealed override bool IsSingleIoBuffer => true;

        public sealed override int IoBufferCount => 1;

        public sealed override ArraySegment<byte> GetIoBuffer(int index, int length)
        {
            this.CheckIndex(index, length);
            return new ArraySegment<byte>(this.Memory, index, length);
        }

        public sealed override ArraySegment<byte>[] GetIoBuffers(int index, int length) => new[] { this.GetIoBuffer(index, length) };

        public sealed override bool HasArray => true;

        public sealed override byte[] Array
        {
            get
            {
                this.EnsureAccessible();
                return this.Memory;
            }
        }

        public sealed override int ArrayOffset => 0;

        public sealed override bool HasMemoryAddress => true;

        public sealed override ref byte GetPinnableMemoryAddress()
        {
            this.EnsureAccessible();
            return ref this.Memory[0];
        }

        public sealed override IntPtr AddressOfPinnedMemory() => IntPtr.Zero;
    }
}