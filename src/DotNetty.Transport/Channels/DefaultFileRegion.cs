﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Transport.Channels
{
    using System;
    using System.IO;
    using DotNetty.Common;
    using DotNetty.Common.Internal.Logging;
    using DotNetty.Common.Utilities;

    public class DefaultFileRegion : AbstractReferenceCounted, IFileRegion
    {
        static readonly IInternalLogger Logger = InternalLoggerFactory.GetInstance<DefaultFileRegion>();

        readonly FileStream file;

        public DefaultFileRegion(FileStream file, long position, long count)
        {
            if (file is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.file); }
            if (!file.CanRead) { ThrowHelper.ThrowArgumentException(); }
            if (position < 0L) { ThrowHelper.ThrowArgumentException_FileRegionPosition(position); }
            if (count < 0L) { ThrowHelper.ThrowArgumentException_FileRegionCount(count); }

            this.file = file;
            this.Position = position;
            this.Count = count;
        }

        public override IReferenceCounted Touch(object hint) => this;

        public long Position { get; }

        public long Transferred { get; set; }

        public long Count { get; }

        public long TransferTo(Stream target, long pos)
        {
            if (target is null) { ThrowHelper.ThrowArgumentNullException(ExceptionArgument.target); }
            if (pos < 0L) { ThrowHelper.ThrowArgumentException_PositiveOrZero(pos, ExceptionArgument.pos); }

            long totalCount = this.Count - pos;
            if (totalCount < 0L)
            {
                ThrowHelper.ThrowArgumentException_Position(pos, this.Count);
            }

            if (0ul >= (ulong)totalCount)
            {
                return 0L;
            }
            if (0u >= (uint)this.ReferenceCount)
            {
                ThrowHelper.ThrowIllegalReferenceCountException(0);
            }

            var buffer = new byte[totalCount];
            int total = this.file.Read(buffer, (int)(this.Position + pos), (int)totalCount);
            target.Write(buffer, 0, total);
            if (total > 0)
            {
                this.Transferred += total;
            }
            else if (0ul >= (ulong)total)
            {
                // If the amount of written data is 0 we need to check if the requested count is bigger then the
                // actual file itself as it may have been truncated on disk.
                //
                // See https://github.com/netty/netty/issues/8868
                Validate(this, Position);
            }
            return total;
        }

        protected override void Deallocate()
        {
            FileStream fileStream = this.file;
            if (!fileStream.CanRead)
            {
                return;
            }

            try
            {
                fileStream.Dispose();
            }
            catch (Exception exception)
            {
                if (Logger.WarnEnabled)
                {
                    Logger.FailedToCloseAFileStream(exception);
                }
            }
        }

        private static void Validate(DefaultFileRegion region, long position)
        {
            // If the amount of written data is 0 we need to check if the requested count is bigger then the
            // actual file itself as it may have been truncated on disk.
            //
            // See https://github.com/netty/netty/issues/8868
            long size = region.file.Length;
            long regionCount = region.Count;
            long count = regionCount - position;
            if (region.Position + count + position > size)
            {
                ThrowHelper.ThrowIOException_Underlying_file_size_smaller_then_requested_count(size, regionCount);
            }
        }
    }
}
